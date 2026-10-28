// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Threading;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Services;
using ReflowLink.UI;

namespace ReflowLink {
    internal static class Program {
        [STAThread]
        private static void Main() {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowCrash(e.Exception);
            // On another thread the process is about to end: write the report only (no window from that thread).
            // It's shown when ReflowLink starts again.
            AppDomain.CurrentDomain.UnhandledException += (s, e) => AppLog.WriteCrash(e.ExceptionObject as Exception);

            Theme.Initialize();
            var settings = AppSettings.Load();
            AppLog.Info($"ReflowLink {typeof(Program).Assembly.GetName().Version} started");
            Application.Run(new MainForm(settings));
            AppLog.Info("ReflowLink closed");
        }

        private static int _showing;

        /// <summary>An error on the interface thread: logged, reported, and ReflowLink carries on.</summary>
        private static void ShowCrash(Exception ex) {
            if (ex == null) return;
            AppLog.Error("Unexpected error: " + ex);
            if (Interlocked.Exchange(ref _showing, 1) == 1) return;
            try {
                DarkDialog.Show(null, "Unexpected error",
                    ex.Message + "\n\n" + ex.GetType().Name + (ex.StackTrace != null ? "\n" + FirstLines(ex.StackTrace, 6) : "")
                    + "\n\nDetails are in the event log: " + AppLog.Folder,
                    DialogTone.Error, ["Continue"]);
            } catch {
                MessageBox.Show(ex.ToString(), "ReflowLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
            } finally { _showing = 0; }
        }

        private static string FirstLines(string s, int n) {
            var lines = s.Split('\n');
            return string.Join("\n", lines, 0, Math.Min(n, lines.Length));
        }
    }
}
