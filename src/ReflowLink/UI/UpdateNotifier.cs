// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReflowLink.Services;

namespace ReflowLink.UI {
    /// <summary>
    /// Tells the user about a newer release on GitHub: once a day at start-up (unless switched off), or on request from
    /// Settings → About. The notice waits until nothing is going on, so it never interrupts a run.
    /// </summary>
    public sealed class UpdateNotifier {
        private readonly AppState _state;
        private readonly Form _owner;
        private readonly Func<bool> _canShow;
        private readonly Timer _timer = new() { Interval = 3000 };
        private UpdateInfo _pending;
        private bool _checking;

        /// <param name="canShow">True when a notice may open now (no run, no operation, no other dialog).</param>
        public UpdateNotifier(AppState state, Form owner, Func<bool> canShow) {
            _state = state;
            _owner = owner;
            _canShow = canShow;
            _timer.Tick += (s, e) => ShowPendingIfIdle();
        }

        /// <summary>At start-up: asks GitHub at most once a day, silently, and only if the user hasn't switched it off.</summary>
        public void CheckOnStartup() {
            var s = _state.Settings;
            if (!s.CheckForUpdates || (DateTime.Now - s.LastUpdateCheck).TotalHours < 20) return;
            _ = RunAsync(manual: false);
        }

        /// <summary>"Check for updates" in Settings: always asks, and always says what it found.</summary>
        public Task CheckNowAsync() => RunAsync(manual: true);

        private async Task RunAsync(bool manual) {
            if (_checking) return;
            _checking = true;
            try {
                if (manual) _state.SetStatus("Checking for updates…");
                var info = await UpdateChecker.CheckAsync();
                _state.Settings.LastUpdateCheck = DateTime.Now;
                _state.Settings.Save();
                if (info == null) {
                    AppLog.Info($"Update check: {UpdateChecker.Current} is the latest version");
                    if (manual) {
                        _state.SetStatus("ReflowLink is up to date", StatusKind.Success);
                        DarkDialog.Show(_owner, "ReflowLink is up to date", $"You have the latest version ({UpdateChecker.Current}).", DialogTone.Info, ["OK"]);
                    }
                } else if (manual) {
                    Show(info);
                } else if (string.Equals(info.Tag, _state.Settings.SkippedUpdate, StringComparison.OrdinalIgnoreCase)) {
                    AppLog.Info($"Update check: {info.Tag} is available but was skipped");
                } else {
                    _pending = info;
                    ShowPendingIfIdle();
                }
            } catch (Exception ex) {
                // At start-up there may simply be no internet: say nothing then.
                AppLog.Warning("Update check failed: " + ex.GetBaseException().Message);
                if (manual) {
                    _state.SetStatus("Couldn't check for updates", StatusKind.Warning);
                    DarkDialog.Show(_owner, "Couldn't check for updates",
                        $"GitHub couldn't be reached ({ex.GetBaseException().Message}). Check the internet connection, or look for new releases at:\n\n{UpdateChecker.ReleasesPage}",
                        DialogTone.Warning, ["OK"]);
                }
            } finally {
                _checking = false;
            }
        }

        private void ShowPendingIfIdle() {
            if (_pending == null) {
                _timer.Stop();
                return;
            }
            if (!_canShow()) {
                _timer.Start(); // try again in a few seconds
                return;
            }
            _timer.Stop();
            var info = _pending;
            _pending = null;
            Show(info);
        }

        private void Show(UpdateInfo info) {
            string notes = UpdateChecker.Summary(info.Notes);
            string message = $"ReflowLink {info.Version} is available. You have {UpdateChecker.Current}."
                + (notes.Length > 0 ? "\n\nWhat's new:\n" + notes : "")
                + "\n\nDownload it from GitHub and replace ReflowLink.exe. Your settings, profiles and recordings are kept.";
            int r = DarkDialog.Show(_owner, "Update available", message, DialogTone.Info, ["Open download page", "Skip this version", "Later"], 0);
            if (r == 0) {
                try {
                    Process.Start(new ProcessStartInfo { FileName = info.Url, UseShellExecute = true });
                } catch (Exception ex) {
                    DarkDialog.Error(_owner, "Couldn't open the browser", $"{ex.Message}\n\nThe release is at:\n{info.Url}");
                }
            } else if (r == 1) {
                _state.Settings.SkippedUpdate = info.Tag;
                _state.Settings.Save();
                _state.SetStatus($"ReflowLink {info.Version} skipped: it won't be offered again at start-up", StatusKind.Normal);
            }
        }
    }
}
