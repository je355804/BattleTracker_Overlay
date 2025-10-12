using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BattleTrackerOverlay
{
    public class ObservableBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value; Raise(name); return true;
        }
    }

    public class RowItem
    {
        public string Name { get; set; } = "";
        public Dictionary<string, string> Metrics { get; } = new();
        public double SortValue { get; set; }
    }

    public record MetricSettingSnapshot(string Key, bool IsEnabled, string Header);

    public record SettingsDialogResult(Dictionary<StatScope, List<MetricSettingSnapshot>> ScopeMetrics, double OverlayOpacity);
}
