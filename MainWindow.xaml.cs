using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace BattleTrackerOverlay
{
    public partial class MainWindow : Window
    {
        private readonly ViewState _vm = new();
        private readonly StatsReader _reader = new();
        private FileSystemWatcher? _watcher;
        private readonly System.Timers.Timer _debounce = new(150) { AutoReset = false };
        private readonly System.Timers.Timer _tick     = new(1000) { AutoReset = true };
    private readonly DispatcherTimer _collapsedHoldTimer;
    private bool _expanded = false;
    private bool _collapsedPressPending;
    private Point _collapsedPressPoint;
    private Button? _collapsedPressSource;
    private const double CollapsedDragDistance = 6.0;
    private const double ResizeGrip = 8.0;
    private bool _showCompactTabs;
    private bool _isBuildingTables;
        private static readonly DependencyPropertyDescriptor? ColumnWidthDescriptor =
            DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));
        private static readonly Dictionary<string, double> ColumnWidthOverrides = new();
        private const double TabVisibilityThreshold = 360;

        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
        [DllImport("gdi32.dll")] static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;
            Opacity = _vm.OverlayOpacity;
            _vm.PropertyChanged += VmOnPropertyChanged;
            UpdateScopeTitle(StatScope.Cumulative);

            _collapsedHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _collapsedHoldTimer.Tick += (_, __) =>
            {
                _collapsedHoldTimer.Stop();
                if (_collapsedPressPending)
                {
                    BeginCollapsedDrag();
                }
            };

            Loaded += (_, __) =>
            {
                // Explicitly set the Tag property for custom tabs to ensure they're set correctly
                TabCustom1.Tag = StatScope.Custom1;
                TabCustom2.Tag = StatScope.Custom2;
                Log.Info($"Custom tab tags set: TabCustom1.Tag={TabCustom1.Tag}, TabCustom2.Tag={TabCustom2.Tag}");
                
                Log.Info("Main window loaded; applying collapsed shape and initializing watcher.");
                ApplyCollapsedShape();
                InitWatcher();
                _tick.Elapsed += (_, __) => Dispatcher.Invoke(UpdateUpdatedAgo);
                _tick.Start();
                DebouncedRefresh();
            };

            _debounce.Elapsed += (_, __) => Dispatcher.Invoke(RefreshFromFile);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var src = (HwndSource)PresentationSource.FromVisual(this)!;
            src.AddHook(WndProc);

            SizeChanged += (_, __) =>
            {
                if (_expanded)
                {
                    ApplyRegion(CreateRoundRectRgn(0, 0, (int)ActualWidth, (int)ActualHeight, 20, 20));
                }
                else
                {
                    ApplyRegion(CreateEllipticRgn(0, 0, (int)ActualWidth, (int)ActualHeight));
                }

                UpdateTabVisibility();
            };
        }

        #region Window shaping & toggle
        private void ApplyCollapsedShape()
        {
            ResetCollapsedInteraction();
            _expanded = false;
            Log.Info("Switching to collapsed overlay view.");
            CollapsedContainer.Visibility = Visibility.Visible;
            ExpandedPanel.Visibility = Visibility.Collapsed;

            Width = 100; Height = 100;
            ApplyRegion(CreateEllipticRgn(0, 0, (int)Width, (int)Height));
        }

        private void ApplyExpandedShape()
        {
            ResetCollapsedInteraction();
            _expanded = true;
            Log.Info("Switching to expanded overlay view.");
            CollapsedContainer.Visibility = Visibility.Collapsed;
            ExpandedPanel.Visibility = Visibility.Visible;

            Width = 950; Height = 400;
            ApplyRegion(CreateRoundRectRgn(0, 0, (int)Width, (int)Height, 20, 20));
            UpdateTabVisibility();
            BuildTables();
        }

        private void ApplyRegion(IntPtr rgn)
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowRgn(h, rgn, true);
            DeleteObject(rgn);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                      HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

            if (msg == WM_NCHITTEST && _expanded)
            {
                int x = unchecked((short)(long)lParam);
                int y = unchecked((short)((long)lParam >> 16));
                var p = PointFromScreen(new Point(x, y));

                bool left = p.X <= ResizeGrip;
                bool right = p.X >= ActualWidth - ResizeGrip;
                bool top = p.Y <= ResizeGrip;
                bool bottom = p.Y >= ActualHeight - ResizeGrip;

                if (left && top) { handled = true; return (IntPtr)HTTOPLEFT; }
                if (right && top) { handled = true; return (IntPtr)HTTOPRIGHT; }
                if (left && bottom) { handled = true; return (IntPtr)HTBOTTOMLEFT; }
                if (right && bottom) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
                if (left) { handled = true; return (IntPtr)HTLEFT; }
                if (right) { handled = true; return (IntPtr)HTRIGHT; }
                if (top) { handled = true; return (IntPtr)HTTOP; }
                if (bottom) { handled = true; return (IntPtr)HTBOTTOM; }
            }

            return IntPtr.Zero;
        }
        #endregion

        #region Drag functionality
        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try 
            { 
                this.DragMove(); 
            } 
            catch { /* ignore */ }
        }

        private void DragArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
        }

        private void DragArea_MouseMove(object sender, MouseEventArgs e)
        {
            // DragMove() is called in MouseDown, so we don't need to do anything here
        }

        private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try 
            { 
                this.DragMove(); 
            } 
            catch { /* ignore */ }
            e.Handled = true; // Prevent expanding when dragging
        }

        private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // Prevent expanding when dragging
        }

        private void DragHandle_MouseMove(object sender, MouseEventArgs e)
        {
            e.Handled = true; // Prevent expanding when dragging
        }
        #endregion

        #region Collapsed interaction
        private void ShowPanelButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_expanded) return;

            _collapsedPressPending = true;
            _collapsedPressPoint = e.GetPosition(this);
            _collapsedPressSource = sender as Button;

            _collapsedHoldTimer.Stop();
            _collapsedHoldTimer.Start();

            _collapsedPressSource?.CaptureMouse();
            e.Handled = true;
        }

        private void ShowPanelButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_collapsedPressPending) return;

            if (_collapsedPressSource == null)
            {
                CancelCollapsedPress();
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CancelCollapsedPress();
                return;
            }

            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _collapsedPressPoint.X) >= CollapsedDragDistance ||
                Math.Abs(pos.Y - _collapsedPressPoint.Y) >= CollapsedDragDistance)
            {
                _collapsedHoldTimer.Stop();
                BeginCollapsedDrag();
                e.Handled = true;
            }
        }

        private void ShowPanelButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _collapsedHoldTimer.Stop();
            var button = _collapsedPressSource ?? sender as Button;

            if (_collapsedPressPending)
            {
                _collapsedPressPending = false;
                button?.ReleaseMouseCapture();

                if (button != null && button.IsMouseOver)
                {
                    ApplyExpandedShape();
                }

                _collapsedPressSource = null;
                e.Handled = true;
                return;
            }

            button?.ReleaseMouseCapture();
            _collapsedPressSource = null;
            e.Handled = true;
        }

        private void ShowPanelButton_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_collapsedPressPending)
            {
                CancelCollapsedPress();
            }
        }

        private void BeginCollapsedDrag()
        {
            if (!_collapsedPressPending) return;
            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                CancelCollapsedPress();
                return;
            }

            _collapsedPressPending = false;
            _collapsedPressSource?.ReleaseMouseCapture();

            try
            {
                DragMove();
            }
            catch
            {
                // ignore failures (e.g., button released)
            }
            finally
            {
                _collapsedPressSource = null;
            }
        }

        private void CancelCollapsedPress()
        {
            _collapsedHoldTimer.Stop();
            _collapsedPressPending = false;
            _collapsedPressSource?.ReleaseMouseCapture();
            _collapsedPressSource = null;
        }

        private void ResetCollapsedInteraction()
        {
            _collapsedHoldTimer.Stop();
            _collapsedPressPending = false;
            _collapsedPressSource?.ReleaseMouseCapture();
            _collapsedPressSource = null;
        }
        #endregion

        #region Buttons
        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ApplyCollapsedShape();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Close();
        }

        private void Gear_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (_vm.Root == null)
            {
                _vm.ErrorBanner = "No data yet; open combat to configure.";
                return;
            }

            _vm.RefreshMetricCatalog();

            var snapshot = _vm.CreateSettingsSnapshot();
            var vm = new SettingsDialogViewModel(snapshot.ScopeMetrics, snapshot.OverlayOpacity, snapshot.UseCompactLayout, snapshot.CompactLayoutColumns, snapshot.FontSize, snapshot.MetricOrder, snapshot.Custom1Name, snapshot.Custom2Name);
            var dlg = new SettingsWindow { Owner = this, DataContext = vm };
            
            // Subscribe to apply settings without closing
            dlg.ApplySettings += () =>
            {
                var result = vm.BuildResult();
                _vm.ApplySettings(result);
                Opacity = _vm.OverlayOpacity;
                BuildTables();
                _vm.ErrorBanner = "";
            };
            
            dlg.ShowDialog();
        }

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // If a custom tab is selected, make the selector appear selected too
            if (Tabs?.SelectedItem == TabCustom1 || Tabs?.SelectedItem == TabCustom2)
            {
                // A custom tab is selected, so also select the selector to keep it highlighted
                TabCustomSelector.IsSelected = true;
            }
            
            BuildTables();
            if (Tabs?.SelectedItem is TabItem tab && tab.Tag is StatScope scope)
            {
                UpdateScopeTitle(scope);
            }
        }

        private void TabCustomSelector_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Prevent the tab from being actually selected
            e.Handled = true;
            
            // Open the popup to show custom tab options
            CustomTabPopup.IsOpen = true;
        }

        private void CustomTab1_Click(object sender, RoutedEventArgs e)
        {
            CustomTabPopup.IsOpen = false;
            if (Tabs != null && TabCustomSelector != null)
            {
                Log.Info($"CustomTab1_Click: Setting TabCustomSelector.Tag to Custom1");
                TabCustomSelector.Tag = StatScope.Custom1;
                // Make sure it's selected
                Tabs.SelectedItem = TabCustomSelector;
                UpdateScopeTitle(StatScope.Custom1);
                BuildTables();
            }
        }

        private void CustomTab2_Click(object sender, RoutedEventArgs e)
        {
            CustomTabPopup.IsOpen = false;
            if (Tabs != null && TabCustomSelector != null)
            {
                Log.Info($"CustomTab2_Click: Setting TabCustomSelector.Tag to Custom2");
                TabCustomSelector.Tag = StatScope.Custom2;
                // Make sure it's selected
                Tabs.SelectedItem = TabCustomSelector;
                UpdateScopeTitle(StatScope.Custom2);
                BuildTables();
            }
        }
        #endregion

        #region Watcher & refresh
        private string StatsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Larian Studios", "Baldur's Gate 3", "Script Extender", "BattleTracker", "views", "current.json");

        private void InitWatcher()
        {
            var dir = Path.GetDirectoryName(StatsPath)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(StatsPath))
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _watcher.Changed += (_, __) => OnStatsFileTouched("Changed");
            _watcher.Created += (_, __) => OnStatsFileTouched("Created");
            _watcher.Renamed += (_, args) => OnStatsFileTouched($"Renamed to {args.Name}");
            _watcher.EnableRaisingEvents = true;
            Log.Info($"FileSystemWatcher active on '{StatsPath}'.");
        }

        private void DebouncedRefresh()
        {
            _debounce.Stop();
            _debounce.Start();
            Log.Info("Scheduled debounced stats refresh.");
        }

        private void RefreshFromFile()
        {
            try
            {
                var (ok, root, raw, err) = _reader.TryRead(StatsPath);
                if (!ok || root == null)
                {
                    var reason = string.IsNullOrWhiteSpace(err) ? "unknown error" : err;
                    _vm.ErrorBanner = $"Parse failed: {reason}. Showing last good data.";
                    Log.Warn($"Stats refresh failed: {reason}");
                    UpdateUpdatedAgo();
                    return;
                }

                var wasInCombat = _vm.Root?.PartyMembers.Values.Any(p => p.InCombatActive) ?? false;

                _vm.ErrorBanner = "";
                _vm.LastWriteTimeUtc = File.GetLastWriteTimeUtc(StatsPath);
                _vm.Raw = raw!;
                _vm.Root = root;
                _vm.RefreshMetricCatalog();

                var isInCombat = _vm.Root.PartyMembers.Values.Any(p => p.InCombatActive);
                if (isInCombat && !wasInCombat)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (Tabs.SelectedIndex != 1)
                        {
                            Log.Info("Combat start detected, switching to Current Combat tab.");
                            Tabs.SelectedIndex = 1;
                        }
                    });
                }

                Log.Info("Stats refresh succeeded.");
                BuildTables();
                UpdateUpdatedAgo();
            }
            catch (Exception ex)
            {
                _vm.ErrorBanner = "Read error; showing last good data.";
                Log.Error("Exception during stats refresh.", ex);
                Console.WriteLine(ex);
            }
        }

        private void OnStatsFileTouched(string reason)
        {
            Log.Info($"Stats file watcher event: {reason}.");
            DebouncedRefresh();
        }

        private void UpdateUpdatedAgo()
        {
            if (_vm.LastWriteTimeUtc == DateTime.MinValue)
            {
                _vm.UpdatedAgo = "Waiting for data…";
                return;
            }
            var secs = (int)(DateTime.UtcNow - _vm.LastWriteTimeUtc).TotalSeconds;
            _vm.UpdatedAgo = $"Updated {secs}s ago";
        }
        #endregion

        #region UI build
        private void BuildTables()
        {
            if (_isBuildingTables) return;
            _isBuildingTables = true;
            try
            {
                if (!_expanded || _vm.Root == null) return;

                UpdateTabVisibilityInternal();

                TablesHost.Children.Clear();

                var selectedTab = Tabs.SelectedItem as TabItem;
                if (selectedTab == null && Tabs.Items.Count > 0)
                {
                    Tabs.SelectedIndex = 0;
                    selectedTab = Tabs.SelectedItem as TabItem;
                }

                if (selectedTab == null)
                {
                    return;
                }

                var scope = selectedTab.Tag is StatScope typedScope ? typedScope : StatScope.CurrentCombat;
                Log.Info($"Rendering tables for selected tab '{selectedTab.Name}' with Tag='{selectedTab.Tag}' resolved to scope {scope}");

                UpdateScopeTitle(scope);
                TablesHost.Children.Add(CreateScopeHeader(scope));

                UIElement content;
                switch (scope)
                {
                    case StatScope.CurrentCombat:
                        content = _vm.UseCompactLayout
                            ? BuildCompactLayout(StatScope.CurrentCombat, _vm.GetLayout(StatScope.CurrentCombat), _vm.BuildRows_CurrentFight())
                            : BuildListView(StatScope.CurrentCombat, _vm.GetLayout(StatScope.CurrentCombat), _vm.BuildRows_CurrentFight());
                        TablesHost.Children.Add(content);
                        break;

                    case StatScope.CurrentLevel:
                        content = _vm.UseCompactLayout
                            ? BuildCompactLayout(StatScope.CurrentLevel, _vm.GetLayout(StatScope.CurrentLevel), _vm.BuildRows_CurrentLevel())
                            : BuildListView(StatScope.CurrentLevel, _vm.GetLayout(StatScope.CurrentLevel), _vm.BuildRows_CurrentLevel());
                        TablesHost.Children.Add(content);
                        break;

                    case StatScope.Cumulative:
                        content = _vm.UseCompactLayout
                            ? BuildCompactLayout(StatScope.Cumulative, _vm.GetLayout(StatScope.Cumulative), _vm.BuildRows_Cumulative())
                            : BuildListView(StatScope.Cumulative, _vm.GetLayout(StatScope.Cumulative), _vm.BuildRows_Cumulative());
                        TablesHost.Children.Add(content);
                        break;

                    case StatScope.Custom1:
                        content = _vm.UseCompactLayout
                            ? BuildCompactLayout(StatScope.Custom1, _vm.GetLayout(StatScope.Custom1), _vm.BuildRows_Custom1())
                            : BuildListView(StatScope.Custom1, _vm.GetLayout(StatScope.Custom1), _vm.BuildRows_Custom1());
                        TablesHost.Children.Add(content);
                        break;

                    case StatScope.Custom2:
                        content = _vm.UseCompactLayout
                            ? BuildCompactLayout(StatScope.Custom2, _vm.GetLayout(StatScope.Custom2), _vm.BuildRows_Custom2())
                            : BuildListView(StatScope.Custom2, _vm.GetLayout(StatScope.Custom2), _vm.BuildRows_Custom2());
                        TablesHost.Children.Add(content);
                        break;
                }
            }
            finally
            {
                _isBuildingTables = false;
            }
        }

        private void UpdateTabVisibility()
        {
            if (Tabs == null) return;
            var previous = _showCompactTabs;
            UpdateTabVisibilityInternal();

            if (previous != _showCompactTabs && !_isBuildingTables)
            {
                BuildTables();
            }
        }

        private void UpdateTabVisibilityInternal()
        {
            if (Tabs == null) return;
            var hideTabs = ActualHeight > 0 && ActualHeight < TabVisibilityThreshold;
            Tabs.Visibility = hideTabs ? Visibility.Collapsed : Visibility.Visible;
            _showCompactTabs = hideTabs;
        }

        private void CompactTab_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton radio || Tabs == null) return;

            if (radio.Tag is StatScope scope)
            {
                var match = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Tag, scope));
                if (match != null && !Equals(Tabs.SelectedItem, match))
                {
                    Tabs.SelectedItem = match;
                }
            }
        }

        private UIElement CreateScopeHeader(StatScope scope)
        {
            if (!_showCompactTabs)
            {
                return new Border { Height = 0, Margin = new Thickness(0) };
            }

            return CreateCompactTabsPanel(scope);
        }

        private UIElement CreateCompactTabsPanel(StatScope selectedScope)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2)
            };

            panel.Children.Add(CreateCompactTabButton(StatScope.Cumulative, "Cumulative", selectedScope == StatScope.Cumulative));
            panel.Children.Add(CreateCompactTabButton(StatScope.CurrentCombat, "Current Combat", selectedScope == StatScope.CurrentCombat));
            panel.Children.Add(CreateCompactTabButton(StatScope.CurrentLevel, "Current Level", selectedScope == StatScope.CurrentLevel));

            return panel;
        }

        private RadioButton CreateCompactTabButton(StatScope scope, string label, bool isSelected)
        {
            var button = new RadioButton
            {
                Content = label,
                Tag = scope,
                IsChecked = isSelected,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 32
            };

            if (TryFindResource("CompactTabRadioStyle") is Style style)
            {
                button.Style = style;
            }

            button.Checked += CompactTab_Checked;
            return button;
        }

        private void UpdateScopeTitle(StatScope scope)
        {
            if (HeaderScopeLabel == null) return;

            HeaderScopeLabel.Text = scope switch
            {
                StatScope.Cumulative => "Cumulative",
                StatScope.CurrentCombat => "Current Combat",
                StatScope.CurrentLevel => "Current Level",
                StatScope.Custom1 => _vm.Custom1Name,
                StatScope.Custom2 => _vm.Custom2Name,
                _ => scope.ToString()
            };
        }

        private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewState.OverlayOpacity))
            {
                Dispatcher.Invoke(() => Opacity = _vm.OverlayOpacity);
            }
            else if (e.PropertyName == nameof(ViewState.Custom1Name) || e.PropertyName == nameof(ViewState.Custom2Name))
            {
                Dispatcher.Invoke(() =>
                {
                    // Update header if a custom tab is currently selected
                    if (Tabs?.SelectedItem is TabItem tab && tab.Tag is StatScope scope)
                    {
                        if (scope == StatScope.Custom1 || scope == StatScope.Custom2)
                        {
                            UpdateScopeTitle(scope);
                        }
                    }
                });
            }
        }

    private ListView BuildListView(StatScope scope, IReadOnlyList<ViewState.StatField> layout, List<RowItem> rows)
        {
            var lv = new ListView { ItemsSource = rows, Height = Double.NaN };
            var gv = new GridView();
            lv.View = gv;

            // Hook into column reorder events
            lv.Loaded += (s, e) => AttachColumnReorderHandler(lv, scope);

            void Add(string key, string header, string bind, double? defaultWidth = null, TextAlignment align = TextAlignment.Left)
            {
                var col = new GridViewColumn { Header = header, DisplayMemberBinding = new System.Windows.Data.Binding(bind) };
                var widthKey = string.IsNullOrEmpty(key) ? null : ColumnKey(scope, key);

                if (!string.IsNullOrEmpty(widthKey) && ColumnWidthOverrides.TryGetValue(widthKey, out var overrideWidth))
                {
                    col.Width = overrideWidth;
                }
                else if (defaultWidth.HasValue)
                {
                    col.Width = defaultWidth.Value;
                }
                else
                {
                    col.Width = double.NaN;
                }

                if (align == TextAlignment.Right)
                {
                    col.DisplayMemberBinding = null;
                    var template = new DataTemplate();
                    var fef = new FrameworkElementFactory(typeof(TextBlock));
                    fef.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(bind));
                    fef.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
                    fef.SetValue(TextBlock.MarginProperty, new Thickness(6, 0, 6, 0));
                    template.VisualTree = fef;
                    col.CellTemplate = template;
                }
                gv.Columns.Add(col);

                if (!string.IsNullOrEmpty(widthKey))
                {
                    TrackColumnWidth(col, widthKey);
                }
            }

            // Always add Character column first
            Add("Character", "Character", nameof(RowItem.Name), 140, TextAlignment.Left);
            
            // Check if we have a saved column order for this scope
            if (_vm.ColumnOrder.TryGetValue(scope, out var savedOrder) && savedOrder.Count > 0)
            {
                // Apply saved order (skip "Character" which is always first)
                var orderedFields = new List<ViewState.StatField>();
                foreach (var key in savedOrder.Skip(1)) // Skip "Character"
                {
                    var field = layout.FirstOrDefault(f => f.Key == key);
                    if (field != null)
                    {
                        orderedFields.Add(field);
                    }
                }
                
                // Add any fields not in the saved order (newly added metrics)
                foreach (var field in layout)
                {
                    if (!savedOrder.Contains(field.Key))
                    {
                        orderedFields.Add(field);
                    }
                }
                
                // Add columns in saved order
                foreach (var field in orderedFields)
                {
                    Add(field.Key, field.Header, $"Metrics[{field.Key}]", field.Width, field.Alignment);
                }
            }
            else
            {
                // No saved order, use default layout order
                foreach (var field in layout)
                {
                    Add(field.Key, field.Header, $"Metrics[{field.Key}]", field.Width, field.Alignment);
                }
            }

            return lv;
        }

        private UIElement BuildCompactLayout(StatScope scope, IReadOnlyList<ViewState.StatField> layout, List<RowItem> rows)
        {
            // Create a main container for all character cards
            var mainPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0) };

            foreach (var row in rows)
            {
                // Create a card for each character
                var characterBorder = new Border
                {
                    Background = (Brush)TryFindResource("Panel") ?? Brushes.Transparent,
                    BorderBrush = (Brush)TryFindResource("BorderColor") ?? Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 5),
                    Padding = new Thickness(8, 8, 4, 8)
                };
                

                var cardContent = new StackPanel { Orientation = Orientation.Vertical };

                // Character name header
                var nameHeader = new TextBlock
                {
                    Text = row.Name,
                    FontSize = _vm.FontSize,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)TryFindResource("Text") ?? Brushes.White,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                cardContent.Children.Add(nameHeader);

                // Create grid for metrics
                var metricsGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                
                // Calculate how many metrics we have and how to lay them out
                var metricsList = layout.ToList();
                var totalMetrics = metricsList.Count;
                
                // Layout: fill horizontally first (up to N columns of metric/value pairs)
                // Then create new rows as needed
                var maxColumns = _vm.CompactLayoutColumns; // Use setting: 3 or 6 metric/value pairs per row
                var rowCount = (int)Math.Ceiling(totalMetrics / (double)maxColumns);
                
                // Define columns: for each metric/value pair we need 2 columns (label + value)
                for (int i = 0; i < maxColumns * 2; i++)
                {
                    metricsGrid.ColumnDefinitions.Add(new ColumnDefinition 
                    { 
                        Width = i % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star)
                    });
                }
                
                // Define rows
                for (int i = 0; i < rowCount; i++)
                {
                    metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }

                // Fill in the metrics
                for (int i = 0; i < metricsList.Count; i++)
                {
                    var field = metricsList[i];
                    var metricValue = row.Metrics.TryGetValue(field.Key, out var val) ? val : "-";
                    
                    // Calculate position
                    var gridRow = i / maxColumns;
                    var gridColPair = i % maxColumns;
                    var labelCol = gridColPair * 2;
                    var valueCol = gridColPair * 2 + 1;
                    
                    // Metric label
                    var label = new TextBlock
                    {
                        Text = field.Header + ":",
                        Foreground = (Brush)TryFindResource("DimText") ?? Brushes.Gray,
                        FontSize = _vm.FontSize,
                        Margin = new Thickness(0, 2, 8, 2),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetRow(label, gridRow);
                    Grid.SetColumn(label, labelCol);
                    metricsGrid.Children.Add(label);
                    
                    // Metric value
                    var value = new TextBlock
                    {
                        Text = metricValue,
                        Foreground = (Brush)TryFindResource("Text") ?? Brushes.White,
                        FontSize = _vm.FontSize,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 2, 16, 2),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetRow(value, gridRow);
                    Grid.SetColumn(value, valueCol);
                    metricsGrid.Children.Add(value);
                }

                cardContent.Children.Add(metricsGrid);
                characterBorder.Child = cardContent;
                mainPanel.Children.Add(characterBorder);
            }

            return mainPanel;
        }

        private static void TrackColumnWidth(GridViewColumn column, string key)
        {
            if (ColumnWidthDescriptor == null) return;

            void Handler(object? sender, EventArgs _) 
            {
                var width = column.Width;
                if (double.IsNaN(width) || width <= 0)
                {
                    ColumnWidthOverrides.Remove(key);
                }
                else
                {
                    ColumnWidthOverrides[key] = width;
                }
            }

            ColumnWidthDescriptor.AddValueChanged(column, Handler);
        }

        private static string ColumnKey(StatScope scope, string key) => $"{scope}:{key}";

        private void AttachColumnReorderHandler(ListView listView, StatScope scope)
        {
            if (listView.View is not GridView gridView) return;

            // Find the GridViewHeaderRowPresenter in the visual tree
            listView.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, 
                new System.Windows.Controls.Primitives.DragCompletedEventHandler((sender, e) =>
                {
                    // Extract current column order after drag is complete
                    var columnOrder = new List<string>();
                    columnOrder.Add("Character"); // Character column is always first
                    
                    foreach (var col in gridView.Columns.Skip(1)) // Skip the Character column
                    {
                        if (col.Header is string header)
                        {
                            // Find the key that matches this header
                            var matchingField = _vm.GetLayout(scope).FirstOrDefault(f => f.Header == header);
                            if (matchingField != null)
                            {
                                columnOrder.Add(matchingField.Key);
                            }
                        }
                    }

                    // Save the order and trigger persistence
                    _vm.ColumnOrder[scope] = columnOrder;
                    // Trigger save via ApplySettings with current snapshot
                    Dispatcher.BeginInvoke(() =>
                    {
                        var snapshot = _vm.CreateSettingsSnapshot();
                        _vm.ApplySettings(snapshot, persist: true);
                    });
                }));
        }

        #endregion
    }

    public class ViewState : ObservableBase
    {
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BattleTrackerOverlay",
            "overlay-settings.json");

        public Root? Root { get; set; }
        public string Raw { get; set; } = "";
        public DateTime LastWriteTimeUtc { get; set; } = DateTime.MinValue;

        private string _updatedAgo = "Waiting for data…";
        public string UpdatedAgo { get => _updatedAgo; set => Set(ref _updatedAgo, value); }

        private string _error = "";
        public string ErrorBanner { get => _error; set => Set(ref _error, value); }

        public ViewState()
        {
            LoadSettings();
        }

        // Populate this map once with every character key you care about (usually your full roster of up to 8).
        // Keys must match the entries under "partyMembers" in current.json. Values are the friendly names you want shown.
        private static readonly Dictionary<string, string> PartyNameOverrides = new()
        {
            { "Elves_Female_High_Player_a3b3ad94-c0cb-41de-75f1-31a0365cbe24", "Tav" },
            { "S_Player_Laezel_58a69333-40bf-8358-1d17-fff240d7fb12", "Lae'zel" },
            // Add the remaining six character keys here once; the UI will pick whichever four appear in current.json.
        };

        private static readonly Dictionary<string, string> NameMap = new()
        {
            { "Elves_Female_Wood_Player_097e6584-f066-ea2f-f34a-af9cbf42df37", "Anodika" },
        };

        private static readonly Dictionary<string, string> HeaderShortNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DamageDealt"] = "DMG",
            ["DamageEffective"] = "DMG Eff",
            ["DamageOverkill"] = "Overkill",
            ["DamagePerTurnEffective"] = "DPR Eff",
            ["DamagePerTurnParticipated"] = "DPR Part",
            ["DamageTaken"] = "DMG Taken",
            ["HealingSelf"] = "Heal Self",
            ["HealingOthers"] = "Heal Others",
            ["HealingPerformedTotal"] = "Heal Done",
            ["HealingReceivedSelf"] = "Heal Self Recv",
            ["HealingReceivedOthers"] = "Heal Others Recv",
            ["HealingReceivedTotal"] = "Heal Recv",
            ["HostilesOpposed"] = "Hostiles",
            ["KillingBlows"] = "Kills",
            ["RoundsEffective"] = "Rounds Eff",
            ["RoundsTotal"] = "Rounds",
        };

        private readonly Dictionary<StatScope, ScopeSettings> _scopeSettings = new()
        {
            [StatScope.CurrentCombat] = ScopeSettings.CreateDefault(new[] { "DamageDealt", "DamageTaken", "HealingSelf", "HealingOthers", "RoundsTotal" }, GetDefaultHeader),
            [StatScope.CurrentLevel] = ScopeSettings.CreateDefault(new[] { "DamageDealt", "DamageTaken", "HealingSelf", "HealingOthers", "RoundsTotal" }, GetDefaultHeader),
            [StatScope.Cumulative] = ScopeSettings.CreateDefault(new[] { "DamageDealt", "DamageTaken", "HealingSelf", "HealingOthers", "RoundsTotal" }, GetDefaultHeader),
            [StatScope.Custom1] = ScopeSettings.CreateDefault(new[] { "DamageDealt", "DamageTaken" }, GetDefaultHeader),
            [StatScope.Custom2] = ScopeSettings.CreateDefault(new[] { "DamageDealt", "DamageTaken" }, GetDefaultHeader),
        };

        private double _overlayOpacity = 0.9;
        public double OverlayOpacity
        {
            get => _overlayOpacity;
            set => Set(ref _overlayOpacity, Math.Clamp(value, 0.5, 1.0));
        }

        private bool _useCompactLayout = false;
        public bool UseCompactLayout
        {
            get => _useCompactLayout;
            set => Set(ref _useCompactLayout, value);
        }

        private int _compactLayoutColumns = 3;
        public int CompactLayoutColumns
        {
            get => _compactLayoutColumns;
            set => Set(ref _compactLayoutColumns, value);
        }

        private List<string> _metricOrder = new();
        public List<string> MetricOrder
        {
            get => _metricOrder;
            set => Set(ref _metricOrder, value ?? new List<string>());
        }

        private double _fontSize = 12.0;
        public double FontSize
        {
            get => _fontSize;
            set => Set(ref _fontSize, value);
        }

        private string _custom1Name = "Custom 1";
        public string Custom1Name
        {
            get => _custom1Name;
            set => Set(ref _custom1Name, value);
        }

        private string _custom2Name = "Custom 2";
        public string Custom2Name
        {
            get => _custom2Name;
            set => Set(ref _custom2Name, value);
        }

        private Dictionary<StatScope, List<string>> _columnOrder = new();
        public Dictionary<StatScope, List<string>> ColumnOrder
        {
            get => _columnOrder;
            set => Set(ref _columnOrder, value ?? new Dictionary<StatScope, List<string>>());
        }

        private record PartySlot(string Key, string? DisplayOverride = null);
        internal record StatField(string Key, string Header, double? Width = null, TextAlignment Alignment = TextAlignment.Right);

        private static string Friendly(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return string.Empty;

            var tag = "S_Player_";
            var ix = id.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (ix >= 0)
            {
                var rem = id[(ix + tag.Length)..];
                var us = rem.IndexOf('_');
                if (us > 0) return rem[..us];
            }
            return id.Length > 12 ? id[..12] : id;
        }

        private static string ResolveDisplay(string key, string? overrideName)
        {
            if (!string.IsNullOrWhiteSpace(overrideName)) return overrideName!;
            if (!string.IsNullOrWhiteSpace(key) && PartyNameOverrides.TryGetValue(key, out var configured)) return configured;
            if (!string.IsNullOrWhiteSpace(key) && NameMap.TryGetValue(key, out var mapped)) return mapped;
            if (!string.IsNullOrWhiteSpace(key)) return Friendly(key);
            return "(unused slot)";
        }

        private static string GetDefaultHeader(string key)
        {
            return HeaderShortNames.TryGetValue(key, out var header)
                ? header
                : key;
        }

        private static string FormatMetric(IReadOnlyDictionary<string, double>? metrics, string key)
        {
            if (metrics == null || !metrics.TryGetValue(key, out var value))
            {
                return "-";
            }

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return "-";
            }

            var rounded = Math.Round(value);
            if (Math.Abs(value - rounded) < 0.0005)
            {
                return ((int)rounded).ToString();
            }

            return Math.Round(value, 2).ToString("0.##");
        }

        private RowItem BuildRowForSlot(PartySlot slot, StatScope scope, Func<PartyMember?, StatTotals?> selector, IReadOnlyList<StatField> layout)
        {
            PartyMember? member = null;
            if (Root != null && !string.IsNullOrWhiteSpace(slot.Key))
            {
                Root.PartyMembers.TryGetValue(slot.Key, out member);
            }

            var totals = selector(member);
            var metrics = totals?.Metrics;
            double sortValue = 0;
            if (metrics != null)
            {
                if (!metrics.TryGetValue("DamageDealt", out sortValue))
                {
                    foreach (var field in layout)
                    {
                        if (metrics.TryGetValue(field.Key, out var fallback))
                        {
                            sortValue = fallback;
                            break;
                        }
                    }
                }
            }

            var row = new RowItem
            {
                Name = ResolveDisplay(slot.Key, slot.DisplayOverride),
                SortValue = sortValue
            };

            foreach (var field in layout)
            {
                row.Metrics[field.Key] = FormatMetric(metrics, field.Key);
            }

            return row;
        }

        private List<PartySlot> ResolveActivePartySlots()
        {
            var slots = new List<PartySlot>();
            var activeIds = Root?.Party?.MemberIds ?? new List<string>();

            foreach (var id in activeIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                var display = PartyNameOverrides.TryGetValue(id, out var configured)
                    ? configured
                    : null;
                slots.Add(new PartySlot(id, display));
                if (slots.Count == 4) break;
            }

            // If the roster list is empty, fall back to the first few party members we know about.
            if (slots.Count == 0 && Root?.PartyMembers is { Count: > 0 })
            {
                foreach (var kvp in Root.PartyMembers)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
                    var display = PartyNameOverrides.TryGetValue(kvp.Key, out var configured)
                        ? configured
                        : null;
                    slots.Add(new PartySlot(kvp.Key, display));
                    if (slots.Count == 4) break;
                }
            }

            return slots;
        }

    internal IReadOnlyList<StatField> GetLayout(StatScope scope) => _scopeSettings[scope].GetActiveFields(MetricOrder);

        private List<RowItem> BuildRowsForScope(StatScope scope, Func<PartyMember?, StatTotals?> selector)
        {
            var layout = GetLayout(scope);
            var rows = new List<RowItem>();
            foreach (var slot in ResolveActivePartySlots())
            {
                rows.Add(BuildRowForSlot(slot, scope, selector, layout));
            }
            rows.Sort((a, b) => b.SortValue.CompareTo(a.SortValue));
            return rows;
        }

        public List<RowItem> BuildRows_CurrentFight() => BuildRowsForScope(StatScope.CurrentCombat, m => m?.CurrentCombatTotals);
        public List<RowItem> BuildRows_CurrentLevel() => BuildRowsForScope(StatScope.CurrentLevel, m => m?.CurrentLevelTotals);
        public List<RowItem> BuildRows_Cumulative() => BuildRowsForScope(StatScope.Cumulative, m => m?.Cumulative);
        public List<RowItem> BuildRows_Custom1() => BuildRowsForScope(StatScope.Custom1, m => m?.Cumulative);
        public List<RowItem> BuildRows_Custom2() => BuildRowsForScope(StatScope.Custom2, m => m?.Cumulative);

        public void RefreshMetricCatalog()
        {
            if (Root == null) return;

            foreach (var member in Root.PartyMembers.Values)
            {
                RegisterMetrics(StatScope.CurrentCombat, member.CurrentCombatTotals);
                RegisterMetrics(StatScope.CurrentLevel, member.CurrentLevelTotals);
                RegisterMetrics(StatScope.Cumulative, member.Cumulative);
                // Custom tabs use cumulative data source but maintain their own metric selections
                RegisterMetrics(StatScope.Custom1, member.Cumulative);
                RegisterMetrics(StatScope.Custom2, member.Cumulative);
            }
        }

        private void RegisterMetrics(StatScope scope, StatTotals? totals)
        {
            if (totals == null) return;
            if (_scopeSettings[scope].EnsureKeys(totals.Metrics.Keys, GetDefaultHeader))
            {
                PersistSettings();
            }
        }

        public SettingsDialogResult CreateSettingsSnapshot()
        {
            var metrics = _scopeSettings.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToSnapshot());
            return new SettingsDialogResult(metrics, OverlayOpacity, UseCompactLayout, CompactLayoutColumns, FontSize, MetricOrder, Custom1Name, Custom2Name);
        }

        public void ApplySettings(SettingsDialogResult result, bool persist = true)
        {
            foreach (var kvp in result.ScopeMetrics)
            {
                if (_scopeSettings.TryGetValue(kvp.Key, out var settings))
                {
                    settings.Apply(kvp.Value, GetDefaultHeader);
                }
            }

            OverlayOpacity = result.OverlayOpacity;
            UseCompactLayout = result.UseCompactLayout;
            CompactLayoutColumns = result.CompactLayoutColumns;
            FontSize = result.FontSize;
            MetricOrder = result.MetricOrder;
            Custom1Name = result.Custom1Name;
            Custom2Name = result.Custom2Name;

            if (persist)
            {
                PersistSettings();
            }
        }

        private sealed class ScopeSettings
        {
            private readonly List<MetricSetting> _ordered = new();
            private readonly Dictionary<string, MetricSetting> _lookup = new(StringComparer.OrdinalIgnoreCase);

            private ScopeSettings()
            {
            }

            public static ScopeSettings CreateDefault(IEnumerable<string> enabledKeys, Func<string, string> headerFactory)
            {
                var scope = new ScopeSettings();
                foreach (var key in enabledKeys)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    var setting = scope.GetOrAdd(key, headerFactory);
                    setting.Enabled = true;
                }
                return scope;
            }

            public bool EnsureKeys(IEnumerable<string> keys, Func<string, string> headerFactory)
            {
                var added = false;
                foreach (var key in keys)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (!_lookup.ContainsKey(key))
                    {
                        var setting = new MetricSetting(key, headerFactory(key));
                        _lookup[key] = setting;
                        _ordered.Add(setting);
                        added = true;
                    }
                }
                return added;
            }

            public IReadOnlyList<StatField> GetActiveFields(List<string> metricOrder)
            {
                var enabled = _ordered.Where(s => s.Enabled).ToList();
                
                // If we have a saved order, apply it
                if (metricOrder != null && metricOrder.Count > 0)
                {
                    var orderedFields = new List<StatField>();
                    foreach (var key in metricOrder)
                    {
                        var field = enabled.FirstOrDefault(s => s.Key == key);
                        if (field != null)
                        {
                            orderedFields.Add(new StatField(field.Key, field.Header, null, TextAlignment.Right));
                        }
                    }
                    // Add any enabled fields not in the order list (newly added metrics)
                    foreach (var field in enabled)
                    {
                        if (!metricOrder.Contains(field.Key))
                        {
                            orderedFields.Add(new StatField(field.Key, field.Header, null, TextAlignment.Right));
                        }
                    }
                    return orderedFields;
                }
                
                // Default: use _ordered as-is
                return enabled
                    .Select(s => new StatField(s.Key, s.Header, null, TextAlignment.Right))
                    .ToList();
            }

            public List<MetricSettingSnapshot> ToSnapshot()
            {
                return _ordered
                    .Select(s => new MetricSettingSnapshot(s.Key, s.Enabled, s.Header))
                    .ToList();
            }

            public void Apply(IEnumerable<MetricSettingSnapshot> snapshots, Func<string, string> headerFactory)
            {
                foreach (var snap in snapshots)
                {
                    var setting = GetOrAdd(snap.Key, headerFactory);
                    setting.Enabled = snap.IsEnabled;
                    setting.Header = string.IsNullOrWhiteSpace(snap.Header)
                        ? headerFactory(snap.Key)
                        : snap.Header.Trim();
                }
            }

            private MetricSetting GetOrAdd(string key, Func<string, string> headerFactory)
            {
                if (_lookup.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var setting = new MetricSetting(key, headerFactory(key));
                _lookup[key] = setting;
                _ordered.Add(setting);
                return setting;
            }
        }

        private sealed class MetricSetting
        {
            public MetricSetting(string key, string header)
            {
                Key = key;
                Header = header;
            }

            public string Key { get; }
            public bool Enabled { get; set; }
            public string Header { get; set; }
        }

        private void PersistSettings()
        {
            try
            {
                var snapshot = CreateSettingsSnapshot();
                var payload = new SettingsFile
                {
                    Scopes = snapshot.ScopeMetrics.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value),
                    OverlayOpacity = snapshot.OverlayOpacity,
                    UseCompactLayout = snapshot.UseCompactLayout,
                    CompactLayoutColumns = snapshot.CompactLayoutColumns,
                    FontSize = snapshot.FontSize,
                    MetricOrder = snapshot.MetricOrder,
                    ColumnOrder = ColumnOrder.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value),
                    Custom1Name = snapshot.Custom1Name,
                    Custom2Name = snapshot.Custom2Name
                };

                var directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(SettingsFilePath, JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to persist settings: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFilePath)) return;

                var raw = File.ReadAllText(SettingsFilePath);
                var payload = JsonConvert.DeserializeObject<SettingsFile>(raw);
                if (payload == null) return;

                var snapshot = new Dictionary<StatScope, List<MetricSettingSnapshot>>();
                if (payload.Scopes != null)
                {
                    foreach (var kvp in payload.Scopes)
                    {
                        if (Enum.TryParse(kvp.Key, out StatScope scope))
                        {
                            snapshot[scope] = kvp.Value ?? new List<MetricSettingSnapshot>();
                        }
                    }
                }

                var opacity = payload.OverlayOpacity ?? OverlayOpacity;
                var compactLayout = payload.UseCompactLayout ?? UseCompactLayout;
                var compactColumns = payload.CompactLayoutColumns ?? CompactLayoutColumns;
                var fontSize = payload.FontSize ?? FontSize;
                var metricOrder = payload.MetricOrder ?? MetricOrder;
                var custom1Name = payload.Custom1Name ?? Custom1Name;
                var custom2Name = payload.Custom2Name ?? Custom2Name;
                
                // Load column order
                if (payload.ColumnOrder != null)
                {
                    var columnOrder = new Dictionary<StatScope, List<string>>();
                    foreach (var kvp in payload.ColumnOrder)
                    {
                        if (Enum.TryParse(kvp.Key, out StatScope scope))
                        {
                            columnOrder[scope] = kvp.Value ?? new List<string>();
                        }
                    }
                    ColumnOrder = columnOrder;
                }
                
                var result = new SettingsDialogResult(snapshot, opacity, compactLayout, compactColumns, fontSize, metricOrder, custom1Name, custom2Name);
                ApplySettings(result, persist: false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to load settings: {ex.Message}");
            }
        }

        private sealed class SettingsFile
        {
            public Dictionary<string, List<MetricSettingSnapshot>> Scopes { get; set; } = new();
            public double? OverlayOpacity { get; set; }
            public bool? UseCompactLayout { get; set; }
            public int? CompactLayoutColumns { get; set; }
            public double? FontSize { get; set; }
            public List<string>? MetricOrder { get; set; }
            public Dictionary<string, List<string>>? ColumnOrder { get; set; }
            public string? Custom1Name { get; set; }
            public string? Custom2Name { get; set; }
        }
    }
}