using Newtonsoft.Json;
using System;
using System.IO;
using System.Threading;

namespace BattleTrackerOverlay
{
    public class StatsReader
    {
        // Try to read with a short retry window to survive transient locks.
        public (bool ok, Root? root, string? raw, string? error) TryRead(string path, int retries = 5, int delayMs = 60)
        {
            Exception? last = null;
            Log.Info($"Reading stats file from '{path}'");
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        Log.Warn("Stats file not found.");
                        return (false, null, null, "file not found");
                    }

                    var raw = File.ReadAllText(path);
                    try
                    {
                        var root = JsonConvert.DeserializeObject<Root>(raw);
                        if (root == null)
                        {
                            Log.Error("Deserialiser returned null root");
                            return (false, null, raw, "null after parse");
                        }
                        Log.Info($"Stats parsed successfully (party members: {root.PartyMembers.Count})");
                        return (true, root, raw, null);
                    }
                    catch (JsonReaderException jsonEx)
                    {
                        last = jsonEx;
                        Log.Warn($"JSON parse error while reading stats (attempt {i + 1}/{retries}): {jsonEx.Message}");
                        Thread.Sleep(delayMs);
                    }
                }
                catch (IOException ex)
                {
                    last = ex;
                    Log.Warn($"IO exception while reading stats (attempt {i + 1}/{retries}): {ex.Message}");
                    Thread.Sleep(delayMs);
                }
                catch (Exception ex)
                {
                    Log.Error("Unhandled exception while reading stats", ex);
                    return (false, null, null, ex.Message);
                }
            }
            if (last != null)
            {
                Log.Error("Failed to read stats after retries", last);
            }
            return (false, null, null, last?.Message);
        }
    }
}
