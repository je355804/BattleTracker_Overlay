using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace BattleTrackerOverlay
{
    public class SettingsDialogViewModel : ObservableBase
    {
        public ObservableCollection<ScopeSettingsViewModel> Scopes { get; }

        private double _overlayOpacity;
        public double OverlayOpacity
        {
            get => _overlayOpacity;
            set => Set(ref _overlayOpacity, value);
        }

        private bool _useCompactLayout;
        public bool UseCompactLayout
        {
            get => _useCompactLayout;
            set => Set(ref _useCompactLayout, value);
        }

        private int _compactLayoutColumns;
        public int CompactLayoutColumns
        {
            get => _compactLayoutColumns;
            set => Set(ref _compactLayoutColumns, value);
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

        private ScopeSettingsViewModel? _selectedScope;
        public ScopeSettingsViewModel? SelectedScope
        {
            get => _selectedScope;
            set => Set(ref _selectedScope, value);
        }

        public ObservableCollection<GlobalHeaderViewModel> GlobalHeaders { get; }

        public SettingsDialogViewModel(Dictionary<StatScope, List<MetricSettingSnapshot>> snapshot, double overlayOpacity, bool useCompactLayout, int compactLayoutColumns, double fontSize, List<string>? metricOrder, string custom1Name = "Custom 1", string custom2Name = "Custom 2")
        {
            var orderedScopes = snapshot
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new ScopeSettingsViewModel(kvp.Key, kvp.Value));

            Scopes = new ObservableCollection<ScopeSettingsViewModel>(orderedScopes);
            SelectedScope = Scopes.FirstOrDefault();
            _overlayOpacity = overlayOpacity;
            _useCompactLayout = useCompactLayout;
            _compactLayoutColumns = compactLayoutColumns;
            _fontSize = fontSize;
            _custom1Name = custom1Name;
            _custom2Name = custom2Name;

            // Build global headers from all unique metric keys across all scopes
            // Track which scopes originally contain each metric
            var metricToScopes = new Dictionary<string, HashSet<StatScope>>();
            foreach (var kvp in snapshot)
            {
                foreach (var metric in kvp.Value)
                {
                    if (!metricToScopes.ContainsKey(metric.Key))
                    {
                        metricToScopes[metric.Key] = new HashSet<StatScope>();
                    }
                    metricToScopes[metric.Key].Add(kvp.Key);
                }
            }

            var allMetrics = snapshot.Values
                .SelectMany(list => list)
                .GroupBy(m => m.Key)
                .Select(g => g.First())
                .ToDictionary(m => m.Key, m => m);

            // Apply saved order if available, otherwise use alphabetical
            List<GlobalHeaderViewModel> globalHeaders;
            if (metricOrder != null && metricOrder.Count > 0)
            {
                globalHeaders = new List<GlobalHeaderViewModel>();
                // Add items in saved order
                foreach (var key in metricOrder)
                {
                    if (allMetrics.TryGetValue(key, out var metric))
                    {
                        globalHeaders.Add(new GlobalHeaderViewModel(metric.Key, metric.Header));
                    }
                }
                // Add any new metrics not in saved order
                foreach (var kvp in allMetrics)
                {
                    if (!metricOrder.Contains(kvp.Key))
                    {
                        globalHeaders.Add(new GlobalHeaderViewModel(kvp.Value.Key, kvp.Value.Header));
                    }
                }
            }
            else
            {
                globalHeaders = allMetrics.Values
                    .Select(m => new GlobalHeaderViewModel(m.Key, m.Header))
                    .OrderBy(h => h.Key)
                    .ToList();
            }
            
            // Wire up change propagation: when global header changes, update ONLY scopes that originally had this metric
            foreach (var globalHeader in globalHeaders)
            {
                globalHeader.OnHeaderChanged = (key, newHeader) =>
                {
                    // Only update scopes that originally contained this metric
                    if (metricToScopes.TryGetValue(key, out var validScopes))
                    {
                        foreach (var scope in Scopes.Where(s => validScopes.Contains(s.Scope)))
                        {
                            var metric = scope.Metrics.FirstOrDefault(m => m.Key == key);
                            if (metric != null)
                            {
                                metric.Header = newHeader;
                            }
                        }
                    }
                };
            }

            GlobalHeaders = new ObservableCollection<GlobalHeaderViewModel>(globalHeaders.OrderBy(h => h.Key));
        }

        public SettingsDialogResult BuildResult()
        {
            var metrics = Scopes.ToDictionary(scope => scope.Scope, scope => scope.BuildSnapshot());
            var metricOrder = GlobalHeaders.Select(h => h.Key).ToList();
            return new SettingsDialogResult(metrics, OverlayOpacity, UseCompactLayout, CompactLayoutColumns, FontSize, metricOrder, Custom1Name, Custom2Name);
        }
    }

    public class ScopeSettingsViewModel : ObservableBase
    {
        public StatScope Scope { get; }
        public string DisplayName { get; }
        public ObservableCollection<MetricOptionViewModel> Metrics { get; }

        public ScopeSettingsViewModel(StatScope scope, IEnumerable<MetricSettingSnapshot> metrics)
        {
            Scope = scope;
            DisplayName = scope switch
            {
                StatScope.CurrentCombat => "Current Combat",
                StatScope.CurrentLevel => "Current Level",
                StatScope.Cumulative => "Cumulative",
                StatScope.Custom1 => "Custom 1",
                StatScope.Custom2 => "Custom 2",
                _ => scope.ToString()
            };

            Metrics = new ObservableCollection<MetricOptionViewModel>(
                metrics.Select(m => new MetricOptionViewModel(m.Key, m.IsEnabled, m.Header))
            );
        }

        public void SelectAll()
        {
            foreach (var metric in Metrics)
            {
                metric.IsEnabled = true;
            }
        }

        public void DeselectAll()
        {
            foreach (var metric in Metrics)
            {
                metric.IsEnabled = false;
            }
        }

        public List<MetricSettingSnapshot> BuildSnapshot()
        {
            return Metrics
                .Select(m => new MetricSettingSnapshot(m.Key, m.IsEnabled, m.Header))
                .ToList();
        }
    }

    public class MetricOptionViewModel : ObservableBase
    {
        public string Key { get; }

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set => Set(ref _isEnabled, value);
        }

        private string _header;
        public string Header
        {
            get => _header;
            set => Set(ref _header, value);
        }

        public MetricOptionViewModel(string key, bool isEnabled, string header)
        {
            Key = key;
            _isEnabled = isEnabled;
            _header = header;
        }
    }

    public class GlobalHeaderViewModel : ObservableBase
    {
        public string Key { get; }

        private string _header;
        public string Header
        {
            get => _header;
            set
            {
                if (Set(ref _header, value))
                {
                    // Notify that this header changed
                    OnHeaderChanged?.Invoke(Key, value);
                }
            }
        }

        public System.Action<string, string>? OnHeaderChanged { get; set; }

        public GlobalHeaderViewModel(string key, string header)
        {
            Key = key;
            _header = header;
        }
    }
}
