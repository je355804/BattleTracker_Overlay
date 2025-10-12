using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace BattleTrackerOverlay
{
    public class Root
    {
        [JsonProperty("partyMembers")] public Dictionary<string, PartyMember> PartyMembers { get; set; } = new();
        [JsonProperty("party")] public PartyRoster Party { get; set; } = new();
        [JsonProperty("currentBattle")] public CurrentBattle? CurrentBattle { get; set; }
        [JsonProperty("metadata")] public Metadata? Metadata { get; set; }
    }

    public class PartyRoster
    {
        [JsonProperty("memberIds")] public List<string> MemberIds { get; set; } = new();
    }

    public class CurrentBattle
    {
        [JsonProperty("party")] public CurrentBattleSide? Party { get; set; }
        [JsonProperty("hostiles")] public CurrentBattleSide? Hostiles { get; set; }
    }

    public class CurrentBattleSide
    {
        [JsonProperty("memberIds")] public List<string> MemberIds { get; set; } = new();
    }

    public class Metadata
    {
        [JsonProperty("schemaVersion")] public string SchemaVersion { get; set; } = "";
        [JsonProperty("generatedAt")] public string GeneratedAt { get; set; } = "";
        [JsonProperty("currentSaveSnapshotId")] public string CurrentSaveSnapshotId { get; set; } = "";
    }

    public class PartyMember
    {
        [JsonProperty("cumulative")] public StatTotals? Cumulative { get; set; }
        [JsonProperty("currentCombatTotals")] public StatTotals? CurrentCombatTotals { get; set; }
        [JsonProperty("currentLevelTotals")] public StatTotals? CurrentLevelTotals { get; set; }
        [JsonProperty("inCombatActive")] public bool InCombatActive { get; set; }
        [JsonProperty("level")] public int Level { get; set; }
        [JsonProperty("side")] public string Side { get; set; } = "";
    }

    public class StatTotals
    {
        [JsonProperty("DamageDealt")] public double DamageDealt { get; set; }
        [JsonProperty("DamageTaken")] public double DamageTaken { get; set; }
        [JsonProperty("HealingOthers")] public double HealingOthers { get; set; }
        [JsonProperty("HealingSelf")] public double HealingSelf { get; set; }
        [JsonProperty("RoundsTotal")] public double RoundsTotal { get; set; }

    [JsonExtensionData] public IDictionary<string, JToken>? ExtensionData { get; set; }
    private Dictionary<string, double>? _metrics;

        public IReadOnlyDictionary<string, double> Metrics
        {
            get
            {
                if (_metrics != null) return _metrics;

                _metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DamageDealt"] = DamageDealt,
                    ["DamageTaken"] = DamageTaken,
                    ["HealingOthers"] = HealingOthers,
                    ["HealingSelf"] = HealingSelf,
                    ["RoundsTotal"] = RoundsTotal,
                };

                if (ExtensionData != null)
                {
                    foreach (var kvp in ExtensionData)
                    {
                        if (kvp.Value == null) continue;
                        if (kvp.Value.Type == JTokenType.Integer || kvp.Value.Type == JTokenType.Float)
                        {
                            _metrics[kvp.Key] = kvp.Value.Value<double>();
                        }
                    }
                }

                return _metrics;
            }
        }

        public double GetValueOrDefault(string key)
        {
            var metrics = Metrics;
            return metrics.TryGetValue(key, out var value) ? value : 0d;
        }
    }
}
