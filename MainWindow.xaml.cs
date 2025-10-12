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
        private const double TabVisibilityThreshold = 300;

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
            var vm = new SettingsDialogViewModel(snapshot.ScopeMetrics, snapshot.OverlayOpacity);
            var dlg = new SettingsWindow { Owner = this, DataContext = vm };
            if (dlg.ShowDialog() == true)
            {
                var result = vm.BuildResult();
                _vm.ApplySettings(result);
                Opacity = _vm.OverlayOpacity;
                BuildTables();
                _vm.ErrorBanner = "";
            }
        }

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BuildTables();
            if (Tabs?.SelectedItem is TabItem tab && tab.Tag is StatScope scope)
            {
                UpdateScopeTitle(scope);
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

                _vm.ErrorBanner = "";
                _vm.LastWriteTimeUtc = File.GetLastWriteTimeUtc(StatsPath);
                _vm.Raw = raw!;
                _vm.Root = root;
                _vm.RefreshMetricCatalog();

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
                Log.Info($"Rendering tables for scope {scope}");

                UpdateScopeTitle(scope);
                TablesHost.Children.Add(CreateScopeHeader(scope));

                switch (scope)
                {
                    case StatScope.CurrentCombat:
                        TablesHost.Children.Add(BuildListView(StatScope.CurrentCombat, _vm.GetLayout(StatScope.CurrentCombat), _vm.BuildRows_CurrentFight()));
                        break;

                    case StatScope.CurrentLevel:
                        TablesHost.Children.Add(BuildListView(StatScope.CurrentLevel, _vm.GetLayout(StatScope.CurrentLevel), _vm.BuildRows_CurrentLevel()));
                        break;

                    case StatScope.Cumulative:
                        TablesHost.Children.Add(BuildListView(StatScope.Cumulative, _vm.GetLayout(StatScope.Cumulative), _vm.BuildRows_Cumulative()));
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
                _ => scope.ToString()
            };
        }

        private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewState.OverlayOpacity))
            {
                Dispatcher.Invoke(() => Opacity = _vm.OverlayOpacity);
            }
        }

    private ListView BuildListView(StatScope scope, IReadOnlyList<ViewState.StatField> layout, List<RowItem> rows)
        {
            var lv = new ListView { ItemsSource = rows, Height = Double.NaN };
            var gv = new GridView();
            lv.View = gv;

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

            Add("Character", "Character", nameof(RowItem.Name), 140, TextAlignment.Left);
            foreach (var field in layout)
            {
                Add(field.Key, field.Header, $"Metrics[{field.Key}]", field.Width, field.Alignment);
            }

            return lv;
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
        };

        private double _overlayOpacity = 0.9;
        public double OverlayOpacity
        {
            get => _overlayOpacity;
            set => Set(ref _overlayOpacity, Math.Clamp(value, 0.2, 1.0));
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

    internal IReadOnlyList<StatField> GetLayout(StatScope scope) => _scopeSettings[scope].GetActiveFields();

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

        public void RefreshMetricCatalog()
        {
            if (Root == null) return;

            foreach (var member in Root.PartyMembers.Values)
            {
                RegisterMetrics(StatScope.CurrentCombat, member.CurrentCombatTotals);
                RegisterMetrics(StatScope.CurrentLevel, member.CurrentLevelTotals);
                RegisterMetrics(StatScope.Cumulative, member.Cumulative);
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
            return new SettingsDialogResult(metrics, OverlayOpacity);
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

            public IReadOnlyList<StatField> GetActiveFields()
            {
                return _ordered
                    .Where(s => s.Enabled)
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
                    OverlayOpacity = snapshot.OverlayOpacity
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
                var result = new SettingsDialogResult(snapshot, opacity);
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
        }
    }
}