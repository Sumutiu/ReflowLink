// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Protocol;
using ReflowLink.Services;
using ReflowLink.UI.Controls;
using ReflowLink.UI.Pages;

namespace ReflowLink.UI {
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class MainForm : Form {
        private readonly AppState _state;
        private readonly DeviceService _device, _bedDevice;
        private readonly Panel _pageHost;
        private readonly PageHeader _header;
        private readonly Dictionary<string, PageBase> _pages = [];
        private readonly Dictionary<string, NavButton> _nav = [];
        private readonly ToolTip _navTip = Theme.CreateToolTip();
        private readonly DashboardPage _dashboard;
        private readonly ProfilesPage _profiles;
        private readonly ConnectionBox _irBox, _bedBox;
        private readonly ThemedLabel _statusText;
        private readonly ModernProgressBar _progress;
        private readonly Timer _statusFade = new() { Interval = 8000 };
        private PageBase _current;

        public MainForm(AppSettings settings) {
            Text = "ReflowLink";
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            var work = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(Math.Min(Theme.S(1180), work.Width), Math.Min(Theme.S(700), work.Height));
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            DoubleBuffered = true;
            try {
                using var s = typeof(MainForm).Assembly.GetManifestResourceStream("ReflowLink.app.ico");
                if (s != null) Icon = new System.Drawing.Icon(s);
            } catch { }

            _device = new DeviceService { PollIntervalMs = settings.PollIntervalMs };
            _bedDevice = new DeviceService { PollIntervalMs = settings.Bed.PollIntervalMs };
            _state = new AppState(settings, _device, _bedDevice) { Owner = this };

            // ---------------------------------------------------------------- sidebar
            var sidebar = new Panel { Dock = DockStyle.Left, Width = Theme.S(236), BackColor = Theme.Sidebar };
            var brand = new BrandPanel { Dock = DockStyle.Top, Height = Theme.S(84), Subtitle = settings.StationChosen ? settings.Station.Name : "PC410 controller" };

            // Fills the space between the brand and the connection boxes, and scrolls on very small screens.
            var navStack = new FlowLayoutPanel {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Height = Theme.S(8 * 44 + 4 * 30 + 12),
                BackColor = Theme.Sidebar,
                Padding = new Padding(0, Theme.S(4), 0, 0),
            };
            void AddNav(string key, string text, Glyph glyph) {
                var b = new NavButton(text, glyph) { Width = Theme.S(236), Margin = new Padding(0, 0, 0, Theme.S(2)) };
                b.Click += (s, e) => ShowPage(key);
                navStack.Controls.Add(b);
                _nav[key] = b;
            }
            void AddHeader(string text) => navStack.Controls.Add(new NavHeader(text) { Width = Theme.S(236), Margin = Padding.Empty });
            AddHeader("Overview");
            AddNav("dashboard", "Dashboard", Glyph.Dashboard);
            AddNav("profiles", "Profiles", Glyph.Profile);
            AddHeader("Top IR");
            AddNav("tuning", "Control Tuning", Glyph.Tune);
            AddNav("console", "Console", Glyph.Console);
            AddHeader("Bed Heat");
            AddNav("bedswitch", "Switching", Glyph.Spark);
            AddNav("bedtuning", "Control Tuning", Glyph.Tune);
            AddNav("bedconsole", "Console", Glyph.Console);
            AddHeader("ReflowLink");
            AddNav("settings", "Settings", Glyph.Settings);

            // Connection blocks at the bottom of the sidebar: top IR, then bed heat
            _irBox = new ConnectionBox("Top IR Connection") { Dock = DockStyle.Top };
            _irBox.Button.Click += async (s, e) => await ToggleConnection();
            _bedBox = new ConnectionBox("Bed Heat Control") { Dock = DockStyle.Top, Margin = Padding.Empty };
            _bedBox.Button.Click += async (s, e) => await ToggleBedConnection();
            var connWrap = new Panel { Dock = DockStyle.Bottom, Height = _irBox.Height * 2 + Theme.S(8 + 12), Padding = Theme.Pad(12, 0, 12, 12), BackColor = Theme.Sidebar };
            var gap = new Panel { Dock = DockStyle.Top, Height = Theme.S(8), BackColor = Theme.Sidebar };
            connWrap.Controls.Add(_bedBox);
            connWrap.Controls.Add(gap);
            connWrap.Controls.Add(_irBox);

            Theme.ApplyDarkScrollbars(navStack);
            sidebar.Controls.Add(navStack);
            sidebar.Controls.Add(brand);
            sidebar.Controls.Add(connWrap);

            // ---------------------------------------------------------------- status bar
            var statusBar = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(30), BackColor = Theme.Sidebar, Padding = Theme.Pad(24, 0, 16, 0) };
            _statusText = new ThemedLabel("Ready", Theme.Small, Theme.TextDim) { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var copyright = new ThemedLabel("© 2026 Marius Sumutiu", Theme.Small, Theme.TextFaint) { AutoSize = false, Dock = DockStyle.Right, Width = Theme.S(170), TextAlign = ContentAlignment.MiddleRight };
            var progHost = new Panel { Dock = DockStyle.Right, Width = Theme.S(200), BackColor = Theme.Sidebar, Padding = new Padding(Theme.S(16), Theme.S(13), Theme.S(16), Theme.S(13)) };
            _progress = new ModernProgressBar { Dock = DockStyle.Fill, Visible = false };
            progHost.Controls.Add(_progress);
            statusBar.Controls.Add(_statusText);
            statusBar.Controls.Add(progHost);
            statusBar.Controls.Add(copyright);
            _statusText.BringToFront();

            // ---------------------------------------------------------------- content
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, Padding = Theme.Pad(28, 20, 28, 20) };
            _header = new PageHeader { Dock = DockStyle.Top, Height = Theme.S(70) };
            _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };
            content.Controls.Add(_pageHost);
            content.Controls.Add(_header);

            Controls.Add(content);
            Controls.Add(statusBar);
            Controls.Add(sidebar);
            content.BringToFront();

            // ---------------------------------------------------------------- pages
            _dashboard = new DashboardPage(_state);
            _profiles = new ProfilesPage(_state);
            AddPage("dashboard", _dashboard);
            AddPage("profiles", _profiles);
            AddPage("tuning", new TuningPage(_state, _state.Ir));
            AddPage("bedswitch", new BedSwitchPage(_state));
            AddPage("bedtuning", new TuningPage(_state, _state.BedChannel));
            AddPage("console", new ConsolePage(_state, _state.Ir));
            AddPage("bedconsole", new ConsolePage(_state, _state.BedChannel));
            AddPage("settings", new SettingsPage(_state));

            // ---------------------------------------------------------------- wiring
            _state.StatusChanged += SetStatus;
            _state.ProgressChanged += p => {
                _progress.Visible = p.HasValue;
                _progress.Indeterminate = p.HasValue && p.Value < 0;
                if (p.HasValue && p.Value >= 0) _progress.Value = p.Value;
            };
            _state.NavigateRequested += ShowPage;
            _state.StationChanged += () => brand.Subtitle = _state.Settings.Station.Name;
            _state.BusyChanged += UpdateConnectionUi;
            _device.StateChanged += (s, e) => { UpdateConnectionUi(); UpdateTitle(); };
            _bedDevice.StateChanged += (s, e) => { UpdateConnectionUi(); UpdateTitle(); };
            _state.BedEnabledChanged += () => {
                if (!_state.DualBed && _bedDevice.IsConnected) _bedDevice.Disconnect();
                UpdateConnectionUi();
                UpdateNav();
            };
            _state.BedSwitch.CommandFailed += msg => SetStatus(msg, StatusKind.Error);
            _dashboard.ActiveChanged += active => UpdateTitle();
            _statusFade.Tick += (s, e) => { _statusFade.Stop(); _statusText.ForeColor = Theme.TextFaint; };

            ApplySavedBounds(settings);
            UpdateConnectionUi();
            UpdateNav();
            ShowPage("dashboard");
        }

        /// <summary>Sidebar pages that need bed hardware are faded out unless that hardware is selected.</summary>
        private void UpdateNav() {
            SetAvailable("bedswitch", _state.SingleBed, "Choose \"Single PC410 (IN2 + OUT2)\" under Settings → Station → Bed heat to use this page.");
            SetAvailable("bedtuning", _state.DualBed, "Choose \"Second PC410\" under Settings → Station → Bed heat to use this page.");
            SetAvailable("bedconsole", _state.DualBed, _state.SingleBed
                ? "With a single PC410 the bed is on the Top IR controller: use the Top IR Console (P2 reads IN2)."
                : "Choose \"Second PC410\" under Settings → Station → Bed heat to use this page.");
            if (_current != null && _nav.TryGetValue(KeyOf(_current), out var nav) && !nav.Available) ShowPage("dashboard");
        }

        private void SetAvailable(string key, bool available, string hint) {
            var b = _nav[key];
            b.Available = available;
            _navTip.SetToolTip(b, available ? "" : hint);
            b.Tag = available ? null : hint;
        }

        private string KeyOf(PageBase page) {
            foreach (var kv in _pages)
                if (kv.Value == page) return kv.Key;
            return "";
        }

        private void AddPage(string key, PageBase page) {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _pages[key] = page;
            _pageHost.Controls.Add(page);
        }

        private void ShowPage(string key) {
            if (!_pages.TryGetValue(key, out var page)) return;
            if (_nav.TryGetValue(key, out var nav) && !nav.Available) {
                SetStatus(nav.Tag as string ?? "This page doesn't apply to the current setup.", StatusKind.Warning);
                return;
            }
            SuspendLayout();
            foreach (var kv in _pages) kv.Value.Visible = kv.Value == page;
            foreach (var kv in _nav) kv.Value.Selected = kv.Key == key;
            _header.Title = page.Title;
            _header.Subtitle = page.Subtitle;
            _current = page;
            ResumeLayout();
            page.OnShown();
        }

        private void SetStatus(string message, StatusKind kind) {
            _statusText.Text = message;
            _statusText.ForeColor = kind == StatusKind.Normal ? Theme.TextDim : AppState.StatusColor(kind);
            _statusFade.Stop();
            _statusFade.Start();
        }

        // ==================================================================== connection

        private void UpdateConnectionUi() {
            var set = _state.Settings;
            _irBox.Show(_device, true, !_state.Busy, set.UseSimulator, set.PortName, set.BaudRate);
            var bed = set.Bed;
            if (_state.SingleBed && !_bedDevice.IsConnected) _bedBox.ShowShared(_device.IsConnected);
            else _bedBox.Show(_bedDevice, _state.DualBed, !_state.Busy, bed.UseSimulator, bed.PortName, bed.BaudRate);
        }

        private async System.Threading.Tasks.Task ToggleConnection() {
            if (_device.IsConnected) {
                if (_dashboard.IsProgramActive &&
                    !DarkDialog.Confirm(this, "Disconnect while running?", "The controller keeps running the program on its own after you disconnect. Monitoring and recording will stop.", "Disconnect", "Cancel"))
                    return;
                if (_state.Ir.AutoTuneRunning &&
                    !DarkDialog.Confirm(this, "Disconnect during auto-tune?", AutoTuneWarning, "Disconnect", "Cancel", danger: true))
                    return;
                // ReflowLink regulates a single PC410's bed: switch OUT2 off while it still can.
                if (_state.SingleBed && !SwitchBedOff() &&
                    !DarkDialog.Confirm(this, "Bed not confirmed off", BedOffWarning, "Disconnect anyway", "Cancel", danger: true)) {
                    _state.BedSwitch.ResumeAfterSwitchOff();
                    return;
                }
                _dashboard.Shutdown();
                _device.Disconnect();
                SetStatus("Top IR controller disconnected", StatusKind.Normal);
                return;
            }
            if (!_state.Settings.UseSimulator && _bedDevice.IsConnected && !_bedDevice.IsSimulator &&
                string.Equals(_state.Settings.PortName, _state.Settings.Bed.PortName, StringComparison.OrdinalIgnoreCase)) {
                DarkDialog.Error(this, "Same serial port", $"{_state.Settings.PortName} is already used by the Bed Heat controller. Each PC410 needs its own serial port; choose another one under Top IR Connection in Settings.");
                return;
            }
            try {
                SetStatus(_state.Settings.UseSimulator ? "Starting simulator…" : $"Connecting to {_state.Settings.PortName}…", StatusKind.Normal);
                await _device.ConnectAsync(_state.Settings.IrConnection());
                _state.Settings.Save();
                SetStatus($"Top IR connected to {_device.LinkName}", StatusKind.Success);
            } catch (Exception ex) {
                SetStatus("Top IR connection failed", StatusKind.Error);
                int r = DarkDialog.Show(this, "Could not connect to the Top IR controller", ex.Message, DialogTone.Error, ["Open settings", "Close"], 1);
                if (r == 0) ShowPage("settings");
            }
        }

        private async System.Threading.Tasks.Task ToggleBedConnection() {
            if (_bedDevice.IsConnected) {
                if (_dashboard.IsProgramActive &&
                    !DarkDialog.Confirm(this, "Disconnect the bed controller while running?",
                        "The bed controller keeps running its program on its own, but ReflowLink can no longer keep the two heaters in step or record the bed.",
                        "Disconnect", "Cancel"))
                    return;
                if (_state.BedChannel.AutoTuneRunning &&
                    !DarkDialog.Confirm(this, "Disconnect during auto-tune?", AutoTuneWarning, "Disconnect", "Cancel", danger: true))
                    return;
                _bedDevice.Disconnect();
                SetStatus("Bed heat controller disconnected", StatusKind.Normal);
                return;
            }
            if (!_state.DualBed) return;
            var bed = _state.Settings.Bed;
            if (!bed.UseSimulator && _device.IsConnected && !_device.IsSimulator && string.Equals(bed.PortName, _state.Settings.PortName, StringComparison.OrdinalIgnoreCase)) {
                DarkDialog.Error(this, "Same serial port", $"{bed.PortName} is already used by the Top IR controller. Each PC410 needs its own serial port; choose another one under Bed Heat Connection in Settings.");
                return;
            }
            try {
                SetStatus(bed.UseSimulator ? "Starting bed simulator…" : $"Connecting to {bed.PortName}…", StatusKind.Normal);
                await _bedDevice.ConnectAsync(bed.Connection());
                _state.Settings.Save();
                SetStatus($"Bed heat connected to {_bedDevice.LinkName}", StatusKind.Success);
            } catch (Exception ex) {
                SetStatus("Bed heat connection failed", StatusKind.Error);
                int r = DarkDialog.Show(this, "Could not connect to the Bed Heat controller", ex.Message, DialogTone.Error, ["Open settings", "Close"], 1);
                if (r == 0) ShowPage("settings");
            }
        }

        /// <summary>Switches a single PC410's bed off (OUT2) with a wait window. False if that couldn't be confirmed.</summary>
        private bool SwitchBedOff(int timeoutMs = 4000) {
            try {
                return BusyDialog.Run(this, "Switching the bed off…", () => _state.BedSwitch.SwitchOffAsync(timeoutMs));
            } catch {
                return false;
            }
        }

        /// <summary>Shows in the title bar (and the taskbar) whether a program is running.</summary>
        private void UpdateTitle() {
            string title = _dashboard.IsProgramActive && (_device.IsConnected || _bedDevice.IsConnected) ? "ReflowLink · Running" : "ReflowLink";
            if (Text != title) Text = title;
        }

        private const string BedOffWarning =
            "The Top IR controller didn't confirm that OUT2 was switched off (A2 = 500). Once disconnected, ReflowLink can't switch the bed off: "
            + "the bed controller behind OUT2 keeps heating to its own setpoint. Check the bed on the station.";

        private const string AutoTuneWarning =
            "The controller keeps tuning on its own, and ReflowLink can't stop it once it's disconnected. "
            + "Stop the tune first on its Control Tuning page, or switch Tune off on the controller's front panel.";

        // ==================================================================== window

        private void ApplySavedBounds(AppSettings s) {
            var size = new Size(Math.Max(MinimumSize.Width, s.WindowWidth), Math.Max(MinimumSize.Height, s.WindowHeight));
            var screen = Screen.FromPoint(new Point(s.WindowX == int.MinValue ? 0 : s.WindowX, s.WindowY == int.MinValue ? 0 : s.WindowY)).WorkingArea;
            size.Width = Math.Min(size.Width, screen.Width);
            size.Height = Math.Min(size.Height, screen.Height);
            var loc = s.WindowX == int.MinValue
                ? new Point(screen.X + (screen.Width - size.Width) / 2, screen.Y + (screen.Height - size.Height) / 2)
                : new Point(s.WindowX, s.WindowY);
            var rect = new Rectangle(loc, size);
            if (!screen.IntersectsWith(rect)) rect.Location = new Point(screen.X + 40, screen.Y + 40);
            Bounds = rect;
            if (s.WindowMaximized) WindowState = FormWindowState.Maximized;
        }

        protected override void OnShown(EventArgs e) {
            base.OnShown(e);
            if (_state.Settings.LoadWarning != null)
                DarkDialog.Show(this, "Settings couldn't be read", _state.Settings.LoadWarning, DialogTone.Warning, ["OK"]);
            if (_state.Settings.InterruptedWrite is int slot) {
                _state.Settings.Save(); // keeps the block
                DarkDialog.Show(this, $"Pattern {slot} may be half-written",
                    $"ReflowLink closed while it was writing pattern {slot}. The pattern may hold part of the new profile and part of the old one, "
                    + "so it's blocked: write a profile to it on the Profiles page before running it.", DialogTone.Warning, ["OK"]);
            }
            ShowPendingCrash();
            if (_state.Settings.StationChosen) return;
            // First run: ask which station the PC410 is in (sets units and temperature limit).
            string id = StationPickerDialog.Pick(this, StationCatalog.DefaultId);
            _state.ChangeStation(id);
        }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            Theme.ApplyDarkTitleBar(this);
        }

        /// <summary>ReflowLink closed unexpectedly last time: say where the report is.</summary>
        private void ShowPendingCrash() {
            string path = AppLog.PendingCrashPath;
            try {
                if (!File.Exists(path)) return;
                string kept = Path.Combine(AppLog.Folder, $"crash-{File.GetLastWriteTime(path):yyyyMMdd-HHmmss}.log");
                File.Delete(path);
                int r = DarkDialog.Show(this, "ReflowLink closed unexpectedly",
                    "ReflowLink stopped because of an unexpected error the last time it ran. A report was saved:\n\n" + kept
                    + "\n\nIf a program was running, check the station: the controller carried on with it on its own.",
                    DialogTone.Warning, ["Open folder", "Close"], 1);
                if (r == 0) DashboardPage.OpenFolder(AppLog.Folder);
            } catch { }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
            // F12 stops the program from any page, even while another operation is running.
            switch (keyData) {
                case Keys.F12:
                    _dashboard.StopFromShortcut();
                    return true;
                case Keys.F5 when _current == _dashboard:
                    _dashboard.StartFromShortcut();
                    return true;
                case Keys.F6 when _current == _dashboard:
                    _dashboard.HoldFromShortcut();
                    return true;
            }
            if (_current != null && (keyData & Keys.Control) != 0 && _current.HandleShortcut(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e) {
            if (e.CloseReason == CloseReason.UserClosing) {
                // Every question first, so nothing is stopped or switched off before the user has agreed to all of it.
                if (_state.Busy &&
                    DarkDialog.Show(this, "An operation is in progress",
                        "ReflowLink is still talking to the controller (for example writing a profile). Closing now can leave a pattern half-written.",
                        DialogTone.Warning, ["Exit anyway", "Cancel"], 1, ButtonKind.Danger) != 0) { e.Cancel = true; return; }

                bool stop = false;
                if (_dashboard.IsProgramActive && (_device.IsConnected || _bedDevice.IsConnected)) {
                    string text = _state.SingleBed
                        ? "The Top IR controller keeps running the program after ReflowLink closes. The bed is switched off (OUT2), since only ReflowLink regulates it."
                        : "The controller keeps running the program after ReflowLink closes.";
                    int r = DarkDialog.Show(this, "A program is running", text,
                        DialogTone.Warning, ["Stop program and exit", "Exit, keep running", "Cancel"], 2);
                    if (r is 2 or < 0) { e.Cancel = true; return; }
                    stop = r == 0;
                }
                if (_state.AutoTuneRunning && (_device.IsConnected || _bedDevice.IsConnected) &&
                    !DarkDialog.Confirm(this, "Auto-tune is running", AutoTuneWarning, "Exit anyway", "Cancel", danger: true)) { e.Cancel = true; return; }
                if (!_profiles.ConfirmClose()) { e.Cancel = true; return; }

                // Then act. A failure still lets the user stay.
                if (_state.Busy) FinishOperationBeforeExit();
                if (stop && !TryStopBeforeExit()) {
                    int r2 = DarkDialog.Show(this, "Could not stop the program",
                        "The controller did not confirm the stop command. The heater program may still be running — stop it on the station's front panel.",
                        DialogTone.Error, ["Exit anyway", "Cancel"], 1, ButtonKind.Danger);
                    if (r2 != 0) { e.Cancel = true; return; }
                }
                if (_state.SingleBed && _device.IsConnected && !SwitchBedOff() &&
                    !DarkDialog.Confirm(this, "Bed not confirmed off", BedOffWarning, "Exit anyway", "Cancel", danger: true)) {
                    _state.BedSwitch.ResumeAfterSwitchOff();
                    e.Cancel = true;
                    return;
                }
            } else if (_state.SingleBed && _device.IsConnected) {
                // Windows shutting down or logging off: no questions, but still switch the bed off.
                SwitchBedOff(2000);
            }
            var s = _state.Settings;
            s.WindowMaximized = WindowState == FormWindowState.Maximized;
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            s.WindowX = b.X; s.WindowY = b.Y; s.WindowWidth = b.Width; s.WindowHeight = b.Height;
            s.Save();
            _dashboard.Shutdown();
            _device.Dispose();
            _bedDevice.Dispose();
            base.OnFormClosing(e);
        }

        /// <summary>
        /// "Exit anyway" during an operation: cancel it and give it a few seconds to finish, so a half-written pattern gets
        /// cleared and blocked before the ports close.
        /// </summary>
        private void FinishOperationBeforeExit() {
            _state.CancelOperation();
            try {
                BusyDialog.Run(this, "Finishing the current operation…", async () => {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (_state.Busy && sw.ElapsedMilliseconds < 6000) await Task.Delay(50);
                    return true;
                });
            } catch { }
        }

        /// <summary>
        /// Sends Stop to every connected controller and confirms they report idle. False if that couldn't be confirmed;
        /// then the user hold stays on, so nothing is resumed automatically (as after a failed Stop on the Dashboard).
        /// </summary>
        private bool TryStopBeforeExit() {
            _state.BeginStop();
            _state.Sync.UserHold = true;
            if (_state.SingleBed) _state.BedSwitch.Stop();
            var devices = new[] { _device, _bedDevice }.Where(d => d.IsConnected).ToList();
            bool ok;
            try {
                ok = BusyDialog.Run(this, "Stopping the program…", async () => {
                    var results = await Task.WhenAll(devices.Select(StopAndConfirmAsync));
                    return results.All(r => r);
                });
            } catch {
                ok = false;
            }
            if (ok) _state.Sync.Halt();
            AppLog.Write(ok ? "INFO" : "ERROR", ok ? "Program stopped before exit" : "Stop before exit not confirmed");
            return ok;
        }

        private static async Task<bool> StopAndConfirmAsync(DeviceService device) {
            try {
                var t = device.RunAsync(d => {
                    d.Stop();
                    return d.ReadState();
                });
                if (await Task.WhenAny(t, Task.Delay(8000)) != t) return false;
                return await t == ProgramState.Idle;
            } catch {
                return false;
            }
        }

        // ==================================================================== small painted parts

        /// <summary>Sidebar block with a controller's link status and its Connect button.</summary>
        [System.ComponentModel.DesignerCategory("Code")]
        private sealed class ConnectionBox : CardPanel {
            private readonly ThemedLabel _caption, _name;
            private readonly StatusPill _pill;

            public ConnectionBox(string caption) {
                Height = Theme.S(118);
                BackColor = Theme.Surface;
                Padding = Theme.Pad(12);
                int w = Theme.S(236 - 24 - 24);
                _caption = new ThemedLabel(caption.ToUpperInvariant(), Theme.SmallBold, Theme.TextFaint) {
                    AutoSize = false,
                    Location = new Point(Theme.S(12), Theme.S(9)),
                    Size = new Size(w, Theme.S(16)),
                };
                _pill = new StatusPill { Location = new Point(Theme.S(12), Theme.S(29)) };
                _name = new ThemedLabel("", Theme.Small, Theme.TextDim) {
                    AutoSize = false,
                    AutoEllipsis = true,
                    Location = new Point(Theme.S(12), Theme.S(58)),
                    Size = new Size(w, Theme.S(18)),
                };
                Button = new ModernButton("Connect", Glyph.Plug, ButtonKind.Primary) {
                    AutoWidth = false,
                    Location = new Point(Theme.S(12), Theme.S(118 - 10 - 32)),
                    Size = new Size(w, Theme.S(32)),
                };
                Controls.AddRange([_caption, _pill, _name, Button]);
            }

            public ModernButton Button { get; }

            /// <summary>Single PC410: the bed has no connection of its own.</summary>
            public void ShowShared(bool irConnected) {
                _pill.Set("Via Top IR", irConnected ? Theme.Green : Theme.TextFaint, irConnected);
                _name.Text = "IN2 + OUT2 on the Top IR PC410";
                SetButton("Connect", Glyph.Plug, ButtonKind.Primary);
                Button.Enabled = false;
            }

            public void Show(DeviceService device, bool enabled, bool idle, bool simulator, string port, int baud) {
                var st = device.State;
                if (!enabled && st == LinkStatus.Disconnected) {
                    _pill.Set("Off", Theme.TextFaint);
                    _name.Text = "Switch on in Settings";
                    SetButton("Connect", Glyph.Plug, ButtonKind.Primary);
                    Button.Enabled = false;
                    return;
                }
                switch (st) {
                    case LinkStatus.Connected:
                        _pill.Set(device.IsSimulator ? "Simulator" : "Online", device.IsSimulator ? Theme.Purple : Theme.Green, true);
                        _name.Text = $"{device.LinkName}  ·  address {device.Address:00}";
                        SetButton("Disconnect", Glyph.Unplug, ButtonKind.Secondary);
                        break;
                    case LinkStatus.NoResponse:
                        _pill.Set("No response", Theme.Red, true);
                        _name.Text = device.LinkName + "  ·  check cable and power";
                        SetButton("Disconnect", Glyph.Unplug, ButtonKind.Secondary);
                        break;
                    case LinkStatus.Connecting:
                        _pill.Set("Connecting…", Theme.Yellow, true);
                        _name.Text = simulator ? "Simulator" : port;
                        Button.Text = "Connecting…";
                        break;
                    default:
                        _pill.Set("Offline", Theme.TextFaint);
                        _name.Text = simulator ? "Simulator selected" : string.IsNullOrEmpty(port) ? "No port selected" : $"{port}  ·  {baud} baud";
                        SetButton("Connect", Glyph.Plug, ButtonKind.Primary);
                        break;
                }
                Button.Enabled = st != LinkStatus.Connecting && idle;
            }

            private void SetButton(string text, Glyph glyph, ButtonKind kind) {
                Button.Text = text;
                Button.Glyph = glyph;
                Button.Kind = kind;
            }
        }

        [System.ComponentModel.DesignerCategory("Code")]
        private sealed class BrandPanel : Control {
            private string _subtitle = "";

            public BrandPanel() {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            /// <summary>Second line under the product name (the selected station).</summary>
            public string Subtitle { get => _subtitle; set { _subtitle = value ?? ""; Invalidate(); } }

            protected override void OnPaint(PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(Theme.Sidebar);
                int s = Theme.S(38);
                int x = Theme.S(22), y = (Height - s) / 2 + Theme.S(4);
                LogoPainter.Draw(g, new RectangleF(x, y, s, s));
                TextRenderer.DrawText(g, "ReflowLink", Theme.Heading, new Point(x + s + Theme.S(12), y + Theme.S(1)), Theme.Text, TextFormatFlags.NoPadding);
                var sub = new Rectangle(x + s + Theme.S(12), y + Theme.S(21), Width - x - s - Theme.S(20), Theme.Small.Height + 2);
                TextRenderer.DrawText(g, _subtitle, Theme.Small, sub, Theme.TextFaint, Theme.LeftMiddle);
            }
        }

        [System.ComponentModel.DesignerCategory("Code")]
        private sealed class PageHeader : Control {
            private string _title = "", _subtitle = "";

            public PageHeader() {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            public string Title { get => _title; set { _title = value; Invalidate(); } }
            public string Subtitle { get => _subtitle; set { _subtitle = value; Invalidate(); } }

            protected override void OnPaint(PaintEventArgs e) {
                var g = e.Graphics;
                g.Clear(Theme.Window);
                TextRenderer.DrawText(g, _title, Theme.Title, new Point(-Theme.S(1), 0), Theme.Text, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, _subtitle, Theme.Body, new Point(0, Theme.Title.Height + Theme.S(2)), Theme.TextDim, TextFormatFlags.NoPadding);
            }
        }
    }
}
