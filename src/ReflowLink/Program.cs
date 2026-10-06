// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Threading;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.UI;

namespace ReflowLink {
    internal static class Program {
        [STAThread]
        private static void Main() {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrash(e.ExceptionObject as Exception);

            Theme.Initialize();
            var settings = AppSettings.Load();
            Application.Run(new MainForm(settings));
        }

        private static int _showing;

        private static void ShowCrash(Exception ex) {
            if (ex == null || Interlocked.Exchange(ref _showing, 1) == 1) return;
            try {
                DarkDialog.Show(null, "Unexpected error",
                    ex.Message + "\n\n" + ex.GetType().Name + (ex.StackTrace != null ? "\n" + FirstLines(ex.StackTrace, 6) : ""),
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
