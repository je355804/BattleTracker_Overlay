using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BattleTrackerOverlay
{
    public partial class SettingsWindow : Window
    {
        private Point _dragStartPoint;
        private GlobalHeaderViewModel? _draggedItem;

        public SettingsWindow()
        {
            InitializeComponent();
            Loaded += SettingsWindow_Loaded;
        }

        private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Set DataContext for each tab based on the actual StatScope, not array index
            if (DataContext is SettingsDialogViewModel vm)
            {
                var cumulativeTab = FindName("CumulativeTab") as TabItem;
                var currentCombatTab = FindName("CurrentCombatTab") as TabItem;
                var currentLevelTab = FindName("CurrentLevelTab") as TabItem;
                var custom1Tab = FindName("Custom1Tab") as TabItem;
                var custom2Tab = FindName("Custom2Tab") as TabItem;

                if (cumulativeTab != null)
                    cumulativeTab.DataContext = vm.Scopes.FirstOrDefault(s => s.Scope == StatScope.Cumulative);
                if (currentCombatTab != null)
                    currentCombatTab.DataContext = vm.Scopes.FirstOrDefault(s => s.Scope == StatScope.CurrentCombat);
                if (currentLevelTab != null)
                    currentLevelTab.DataContext = vm.Scopes.FirstOrDefault(s => s.Scope == StatScope.CurrentLevel);
                if (custom1Tab != null)
                    custom1Tab.DataContext = vm.Scopes.FirstOrDefault(s => s.Scope == StatScope.Custom1);
                if (custom2Tab != null)
                    custom2Tab.DataContext = vm.Scopes.FirstOrDefault(s => s.Scope == StatScope.Custom2);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // Apply settings without closing the window
            ApplySettings?.Invoke();
        }

        // Event that MainWindow can subscribe to
        public event Action? ApplySettings;

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is ScopeSettingsViewModel scope)
            {
                scope.SelectAll();
            }
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is ScopeSettingsViewModel scope)
            {
                scope.DeselectAll();
            }
        }

        #region Drag and Drop for Global Headers

        private void GlobalHeaders_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            
            // Get the item being clicked
            if (e.OriginalSource is FrameworkElement element)
            {
                var listViewItem = FindAncestor<ListViewItem>(element);
                if (listViewItem != null)
                {
                    _draggedItem = listViewItem.DataContext as GlobalHeaderViewModel;
                }
            }
        }

        private void GlobalHeaders_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedItem != null)
            {
                Point currentPosition = e.GetPosition(null);
                Vector diff = _dragStartPoint - currentPosition;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    // Start drag operation
                    var listView = FindName("GlobalHeadersListView") as ListView;
                    if (listView != null)
                    {
                        DragDrop.DoDragDrop(listView, _draggedItem, DragDropEffects.Move);
                        _draggedItem = null;
                    }
                }
            }
        }

        private void GlobalHeaders_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(GlobalHeaderViewModel)))
            {
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void GlobalHeaders_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(GlobalHeaderViewModel)))
            {
                var droppedItem = e.Data.GetData(typeof(GlobalHeaderViewModel)) as GlobalHeaderViewModel;
                if (droppedItem == null) return;

                // Find the target position
                var targetElement = e.OriginalSource as FrameworkElement;
                var targetListViewItem = FindAncestor<ListViewItem>(targetElement);
                
                if (targetListViewItem != null)
                {
                    var targetItem = targetListViewItem.DataContext as GlobalHeaderViewModel;
                    if (targetItem != null && DataContext is SettingsDialogViewModel viewModel)
                    {
                        int oldIndex = viewModel.GlobalHeaders.IndexOf(droppedItem);
                        int newIndex = viewModel.GlobalHeaders.IndexOf(targetItem);

                        if (oldIndex != -1 && newIndex != -1 && oldIndex != newIndex)
                        {
                            viewModel.GlobalHeaders.Move(oldIndex, newIndex);
                        }
                    }
                }
            }
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T ancestor)
                {
                    return ancestor;
                }
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        #endregion
    }
}
