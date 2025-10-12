using System;
using System.IO;
using System.Text;

namespace BattleTrackerOverlay
{
    internal static class Log
    {
        private static readonly object Sync = new();
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BattleTrackerOverlay");
        private static readonly string LogPath = Path.Combine(LogDirectory, "overlay.log");

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message, Exception? ex = null)
        {
            if (ex == null)
            {
                Write("ERROR", message);
            }
            else
            {
                Write("ERROR", message + " | " + ex);
            }
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(LogDirectory);
                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(LogPath, line, Encoding.UTF8);
                    Console.WriteLine(line.TrimEnd());
                }
            }
            catch
            {
                // Swallow logging failures – never interrupt overlay behaviour.
            }
        }
    }
}
