// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.IO;
using System.Linq;
using System.Text;
using ReflowLink.Model;

namespace ReflowLink.Services {
    /// <summary>
    /// Event log: status messages, dialogs and errors, one file per day under the settings folder (Logs). Helps work out
    /// afterwards what happened during a run. Logging never throws.
    /// </summary>
    public static class AppLog {
        private static readonly object Sync = new();
        private const int KeepDays = 30;
        private static bool _cleaned;

        /// <summary>Folder of the event logs and crash reports.</summary>
        public static string Folder => Path.Combine(AppSettings.SettingsFolder, "Logs");

        /// <summary>Set by the tests to keep their messages out of the real log.</summary>
        public static bool Disabled { get; set; }

        public static string TodayPath => Path.Combine(Folder, $"events-{DateTime.Now:yyyy-MM-dd}.log");

        /// <summary>Crash report written when ReflowLink failed; shown on the next start.</summary>
        public static string PendingCrashPath => Path.Combine(Folder, "crash-pending.log");

        public static void Info(string message) => Write("INFO", message);
        public static void Warning(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Write(string level, string message) {
            if (Disabled || string.IsNullOrEmpty(message)) return;
            try {
                lock (Sync) {
                    Directory.CreateDirectory(Folder);
                    string text = message.Replace("\r", "").Replace("\n", " | ");
                    File.AppendAllText(TodayPath, $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {text}{Environment.NewLine}", Encoding.UTF8);
                    if (!_cleaned) {
                        _cleaned = true;
                        Cleanup();
                    }
                }
            } catch { /* logging must never break anything */ }
        }

        /// <summary>Writes a crash report (kept for the next start) and logs the error. Never throws.</summary>
        public static string WriteCrash(Exception ex) {
            try {
                lock (Sync) {
                    Directory.CreateDirectory(Folder);
                    var sb = new StringBuilder();
                    sb.AppendLine($"ReflowLink {typeof(AppLog).Assembly.GetName().Version} crashed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}");
                    sb.AppendLine();
                    sb.AppendLine(ex?.ToString() ?? "(no exception)");
                    File.WriteAllText(PendingCrashPath, sb.ToString(), Encoding.UTF8);
                    string kept = Path.Combine(Folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    File.Copy(PendingCrashPath, kept, true);
                }
                Error("Crash: " + ex?.GetType().Name + ": " + ex?.Message);
                return PendingCrashPath;
            } catch {
                return null;
            }
        }

        /// <summary>The last lines of today's log, for "Copy diagnostics".</summary>
        public static string Tail(int lines) {
            try {
                lock (Sync) {
                    if (!File.Exists(TodayPath)) return "";
                    var all = File.ReadAllLines(TodayPath);
                    return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
                }
            } catch {
                return "";
            }
        }

        private static void Cleanup() {
            try {
                foreach (var f in Directory.GetFiles(Folder, "*.log")) {
                    if (Path.GetFileName(f) == "crash-pending.log") continue;
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)) File.Delete(f);
                }
            } catch { }
        }
    }
}
