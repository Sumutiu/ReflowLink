// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Services;
using ReflowLink.UI.Controls;
using ReflowLink.UI.Pages;

namespace ReflowLink.UI {
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class MainForm : Form {
        private readonly AppState _state;
        private readonly DeviceService _device;
        private readonly Panel _pageHost;
        private readonly PageHeader _header;
        private readonly Dictionary<string, PageBase> _pages = [];
        private readonly Dictionary<string, NavButton> _nav = [];
        private readonly DashboardPage _dashboard;
        private readonly ProfilesPage _profiles;
        private readonly StatusPill _linkPill;
        private readonly ThemedLabel _linkName;
        private readonly ModernButton _connect;
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
            _state = new AppState(settings, _device) { Owner = this };

            // ---------------------------------------------------------------- sidebar
            var sidebar = new Panel { Dock = DockStyle.Left, Width = Theme.S(236), BackColor = Theme.Sidebar };
            var brand = new BrandPanel { Dock = DockStyle.Top, Height = Theme.S(84), Subtitle = settings.StationChosen ? settings.Station.Name : "PC410 controller" };

            var navStack = new FlowLayoutPanel {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Height = Theme.S(4 * 46 + 12),
                BackColor = Theme.Sidebar,
                Padding = new Padding(0, Theme.S(4), 0, 0),
            };
            void AddNav(string key, string text, Glyph glyph) {
                var b = new NavButton(text, glyph) { Width = Theme.S(236), Margin = new Padding(0, 0, 0, Theme.S(4)) };
                b.Click += (s, e) => ShowPage(key);
                navStack.Controls.Add(b);
                _nav[key] = b;
            }
            AddNav("dashboard", "Dashboard", Glyph.Dashboard);
            AddNav("profiles", "Profiles", Glyph.Profile);
            AddNav("console", "Console", Glyph.Console);
            AddNav("settings", "Settings", Glyph.Settings);

            // Connection block at the bottom of the sidebar
            var connBox = new CardPanel { Dock = DockStyle.Bottom, Height = Theme.S(150), BackColor = Theme.Surface, Padding = Theme.Pad(14) };
            var connWrap = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(150 + 16), Padding = Theme.Pad(12, 0, 12, 16), BackColor = Theme.Sidebar };
            connWrap.Controls.Add(connBox);
            connBox.Dock = DockStyle.Fill;
            _linkPill = new StatusPill { Location = new Point(Theme.S(14), Theme.S(14)) };
            _linkName = new ThemedLabel("", Theme.Small, Theme.TextDim) {
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(Theme.S(14), Theme.S(48)),
                Size = new Size(Theme.S(236 - 24 - 28), Theme.S(36)),
            };
            _connect = new ModernButton("Connect", Glyph.Plug, ButtonKind.Primary) {
                AutoWidth = false,
                Location = new Point(Theme.S(14), Theme.S(150 - 14 - 36)),
                Size = new Size(Theme.S(236 - 24 - 28), Theme.S(36)),
            };
            _connect.Click += async (s, e) => await ToggleConnection();
            connBox.Controls.Add(_linkPill);
            connBox.Controls.Add(_linkName);
            connBox.Controls.Add(_connect);

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
            AddPage("console", new ConsolePage(_state));
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
            _device.StateChanged += (s, e) => UpdateConnectionUi();
            _statusFade.Tick += (s, e) => { _statusFade.Stop(); _statusText.ForeColor = Theme.TextFaint; };

            ApplySavedBounds(settings);
            UpdateConnectionUi();
            ShowPage("dashboard");
        }

        private void AddPage(string key, PageBase page) {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _pages[key] = page;
            _pageHost.Controls.Add(page);
        }

        private void ShowPage(string key) {
            if (!_pages.TryGetValue(key, out var page)) return;
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
            var st = _device.State;
            switch (st) {
                case LinkStatus.Connected:
                    _linkPill.Set(_device.IsSimulator ? "Simulator" : "Connected", _device.IsSimulator ? Theme.Purple : Theme.Green, true);
                    _linkName.Text = $"{_device.LinkName}\nAddress {_device.Address:00}";
                    _connect.Text = "Disconnect";
                    _connect.Glyph = Glyph.Unplug;
                    _connect.Kind = ButtonKind.Secondary;
                    break;
                case LinkStatus.NoResponse:
                    _linkPill.Set("No response", Theme.Red, true);
                    _linkName.Text = _device.LinkName + "\nCheck cable and power";
                    _connect.Text = "Disconnect";
                    _connect.Glyph = Glyph.Unplug;
                    _connect.Kind = ButtonKind.Secondary;
                    break;
                case LinkStatus.Connecting:
                    _linkPill.Set("Connecting…", Theme.Yellow, true);
                    _linkName.Text = _state.Settings.UseSimulator ? "Simulator" : _state.Settings.PortName;
                    _connect.Text = "Connecting…";
                    break;
                default:
                    _linkPill.Set("Offline", Theme.TextFaint);
                    _linkName.Text = _state.Settings.UseSimulator ? "Simulator selected" :
                        string.IsNullOrEmpty(_state.Settings.PortName) ? "No port selected" : $"{_state.Settings.PortName} · {_state.Settings.BaudRate} baud";
                    _connect.Text = "Connect";
                    _connect.Glyph = Glyph.Plug;
                    _connect.Kind = ButtonKind.Primary;
                    break;
            }
            _connect.Enabled = st != LinkStatus.Connecting && !_state.Busy;
        }

        private async System.Threading.Tasks.Task ToggleConnection() {
            if (_device.IsConnected) {
                if (_dashboard.IsProgramActive &&
                    !DarkDialog.Confirm(this, "Disconnect while running?", "The controller keeps running the program on its own after you disconnect. Monitoring and recording will stop.", "Disconnect", "Cancel"))
                    return;
                _dashboard.Shutdown();
                _device.Disconnect();
                SetStatus("Disconnected", StatusKind.Normal);
                return;
            }
            try {
                SetStatus(_state.Settings.UseSimulator ? "Starting simulator…" : $"Connecting to {_state.Settings.PortName}…", StatusKind.Normal);
                await _device.ConnectAsync(_state.Settings);
                _state.Settings.Save();
                SetStatus($"Connected to {_device.LinkName}", StatusKind.Success);
            } catch (Exception ex) {
                SetStatus("Connection failed", StatusKind.Error);
                int r = DarkDialog.Show(this, "Could not connect", ex.Message, DialogTone.Error, ["Open settings", "Close"], 1);
                if (r == 0) ShowPage("settings");
            }
        }

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
            if (_state.Settings.StationChosen) return;
            // First run: ask which station the PC410 is in (sets units and temperature limit).
            string id = StationPickerDialog.Pick(this, StationCatalog.DefaultId);
            _state.ChangeStation(id);
        }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            Theme.ApplyDarkTitleBar(this);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
            if (_current != null && (keyData & Keys.Control) != 0 && _current.HandleShortcut(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e) {
            if (e.CloseReason == CloseReason.UserClosing) {
                if (_state.Busy &&
                    DarkDialog.Show(this, "An operation is in progress",
                        "ReflowLink is still talking to the controller (for example writing a profile). Closing now can leave a pattern half-written.",
                        DialogTone.Warning, ["Exit anyway", "Cancel"], 1, ButtonKind.Danger) != 0) { e.Cancel = true; return; }

                if (_dashboard.IsProgramActive && _device.IsConnected) {
                    int r = DarkDialog.Show(this, "A program is running", "The controller keeps running the program after ReflowLink closes.",
                        DialogTone.Warning, ["Stop program and exit", "Exit, keep running", "Cancel"], 2);
                    if (r is 2 or < 0) { e.Cancel = true; return; }
                    if (r == 0 && !TryStopBeforeExit()) {
                        int r2 = DarkDialog.Show(this, "Could not stop the program",
                            "The controller did not confirm the stop command. The heater program may still be running — stop it on the station's front panel.",
                            DialogTone.Error, ["Exit anyway", "Cancel"], 1, ButtonKind.Danger);
                        if (r2 != 0) { e.Cancel = true; return; }
                    }
                }
                if (!_profiles.ConfirmClose()) { e.Cancel = true; return; }
            }
            var s = _state.Settings;
            s.WindowMaximized = WindowState == FormWindowState.Maximized;
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            s.WindowX = b.X; s.WindowY = b.Y; s.WindowWidth = b.Width; s.WindowHeight = b.Height;
            s.Save();
            _dashboard.Shutdown();
            _device.Dispose();
            base.OnFormClosing(e);
        }

        /// <summary>Sends Stop and confirms the controller reports idle. Returns false if that could not be confirmed.</summary>
        private bool TryStopBeforeExit() {
            Cursor = Cursors.WaitCursor;
            try {
                var t = _device.RunAsync(d => {
                    d.Stop();
                    return d.ReadState();
                });
                if (!t.Wait(8000)) return false;
                return t.Result == Protocol.ProgramState.Idle;
            } catch { return false; } finally { Cursor = Cursors.Default; }
        }

        // ==================================================================== small painted parts

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
