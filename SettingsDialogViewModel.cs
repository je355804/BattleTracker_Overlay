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

        private ScopeSettingsViewModel? _selectedScope;
        public ScopeSettingsViewModel? SelectedScope
        {
            get => _selectedScope;
            set => Set(ref _selectedScope, value);
        }

        public SettingsDialogViewModel(Dictionary<StatScope, List<MetricSettingSnapshot>> snapshot, double overlayOpacity)
        {
            var orderedScopes = snapshot
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new ScopeSettingsViewModel(kvp.Key, kvp.Value));

            Scopes = new ObservableCollection<ScopeSettingsViewModel>(orderedScopes);
            SelectedScope = Scopes.FirstOrDefault();
            _overlayOpacity = overlayOpacity;
        }

        public SettingsDialogResult BuildResult()
        {
            var metrics = Scopes.ToDictionary(scope => scope.Scope, scope => scope.BuildSnapshot());
            return new SettingsDialogResult(metrics, OverlayOpacity);
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
                _ => scope.ToString()
            };

            Metrics = new ObservableCollection<MetricOptionViewModel>(
                metrics.Select(m => new MetricOptionViewModel(m.Key, m.IsEnabled, m.Header))
            );
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
}
