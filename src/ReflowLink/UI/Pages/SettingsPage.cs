// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Protocol;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI.Pages {
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class SettingsPage : PageBase {
        private readonly ModernDropDown _port, _baud, _simSpeed, _unused, _station, _rampUnit, _dwellUnit;
        private readonly ThemedLabel _stationControls, _stationNotes;
        private readonly NumberBox _address, _timeout, _retries, _poll, _maxTemp, _afterRun;
        private readonly ToggleSwitch _dtr, _rts, _sim, _verify, _autoRecord;
        private readonly ModernTextBox _folder;
        private readonly ThemedLabel _connHint;
        private bool _loading;

        private static readonly double[] SimSpeeds = [1, 2, 5, 10, 20];

        public SettingsPage(AppState state) : base(state, "Settings", "Connection, profile and logging options") {
            var s = State.Settings;
            _loading = true;

            // ---------------------------------------------------------------- station
            _station = new ModernDropDown { Width = Theme.S(280) };
            _station.SetItems(StationCatalog.All.Select(st => st.Name), StationCatalog.IndexOf(s.StationId));
            _station.SelectedIndexChanged += (o, e) => {
                if (_loading || _station.SelectedIndex < 0) return;
                State.ChangeStation(StationCatalog.All[_station.SelectedIndex].Id);
            };
            _stationControls = WrapLabel(Theme.Body, Theme.Text, 2);
            _stationNotes = WrapLabel(Theme.Small, Theme.TextDim, 3);
            _rampUnit = new ModernDropDown { Width = Theme.S(180) };
            _rampUnit.SetItems(["°C per second", "°C per minute"]);
            _rampUnit.SelectedIndexChanged += (o, e) => {
                if (_loading) return;
                s.GenericRampUnit = _rampUnit.SelectedIndex == 1 ? RampUnit.PerMinute : RampUnit.PerSecond;
                State.ApplyStation();
            };
            _dwellUnit = new ModernDropDown { Width = Theme.S(180) };
            _dwellUnit.SetItems(["Seconds", "Minutes"]);
            _dwellUnit.SelectedIndexChanged += (o, e) => {
                if (_loading) return;
                s.GenericDwellUnit = _dwellUnit.SelectedIndex == 1 ? DwellUnit.Minutes : DwellUnit.Seconds;
                State.ApplyStation();
            };

            var station = Card("Station", "Which reflow / rework station the PC410 is in");
            var stf = Form(station);
            Row(stf, "Station", _station, null);
            Row(stf, "Controller", _stationControls, null);
            Row(stf, "Ramp rate unit", _rampUnit, "Profiles are always edited in °C/s.");
            Row(stf, "Hold time unit", _dwellUnit, "Profiles are always edited in seconds.");
            Row(stf, "", _stationNotes, null);
            FitCard(station, stf);

            // ---------------------------------------------------------------- connection
            _port = new ModernDropDown { Width = Theme.S(200) };
            var refresh = new ModernButton("", Glyph.Refresh) { Margin = new Padding(Theme.S(8), 0, 0, 0) };
            refresh.Click += (o, e) => RefreshPorts();
            Theme.CreateToolTip().SetToolTip(refresh, "Refresh the list of serial ports");
            var portRow = Flow();
            portRow.Controls.Add(_port);
            portRow.Controls.Add(refresh);
            _port.Margin = Padding.Empty;
            _port.SelectedIndexChanged += (o, e) => {
                if (_loading || _port.SelectedItem == null || _port.SelectedItem.StartsWith("No serial", StringComparison.Ordinal)) return;
                s.PortName = _port.SelectedItem.Split(' ')[0];
                Changed();
            };

            _baud = new ModernDropDown { Width = Theme.S(200) };
            _baud.SetItems(Pc410Protocol.StandardBaudRates.Select(b => b + " baud"), Math.Max(0, Array.IndexOf(Pc410Protocol.StandardBaudRates, s.BaudRate)));
            _baud.SelectedIndexChanged += (o, e) => { s.BaudRate = Pc410Protocol.StandardBaudRates[_baud.SelectedIndex]; Changed(); };

            _address = Num(0, 99, 0, 1, s.Address, "", v => s.Address = (int)v);
            _timeout = Num(100, 5000, 0, 50, s.ResponseTimeoutMs, "ms", v => s.ResponseTimeoutMs = (int)v);
            _retries = Num(0, 5, 0, 1, s.Retries, "", v => s.Retries = (int)v);
            _poll = Num(200, 10000, 0, 100, s.PollIntervalMs, "ms", v => { s.PollIntervalMs = (int)v; State.Device.PollIntervalMs = (int)v; });
            _dtr = Toggle("DTR on", s.DtrEnable, v => s.DtrEnable = v);
            _rts = Toggle("RTS on", s.RtsEnable, v => s.RtsEnable = v);
            var lines = Flow();
            lines.Controls.Add(_dtr);
            lines.Controls.Add(_rts);
            _dtr.Margin = new Padding(0, 0, Theme.S(20), 0);

            _sim = Toggle("Use built-in simulator", s.UseSimulator, v => { s.UseSimulator = v; _simSpeed.Enabled = v; });
            _simSpeed = new ModernDropDown { Width = Theme.S(120), Enabled = s.UseSimulator, Margin = new Padding(Theme.S(16), 0, 0, 0) };
            _simSpeed.SetItems(SimSpeeds.Select(v => v + "× speed"), Math.Max(0, Array.IndexOf(SimSpeeds, s.SimulatorSpeed)));
            _simSpeed.SelectedIndexChanged += (o, e) => { s.SimulatorSpeed = SimSpeeds[_simSpeed.SelectedIndex]; Changed(); };
            var simRow = Flow();
            simRow.Controls.Add(_sim);
            simRow.Controls.Add(_simSpeed);
            _sim.Margin = new Padding(0, Theme.S(3), 0, 0);

            _connHint = new ThemedLabel("", Theme.Small, Theme.Yellow) { AutoSize = true, MaximumSize = new Size(Theme.S(320), 0) };

            var conn = Card("Connection", "Serial link to the PC410 controller");
            var cf = Form(conn);
            Row(cf, "Serial port", portRow, "USB-to-RS232 adapters appear as COM ports.");
            Row(cf, "Baud rate", _baud, "Must match the controller's communication setting.");
            Row(cf, "Data format", new ThemedLabel("7 data bits · even parity · 1 stop bit", Theme.Body, Theme.Text), "Fixed: required by the PC410 protocol.");
            Row(cf, "Device address", _address, "Controller address 0–99 (often 1).");
            Row(cf, "Response timeout", _timeout, null);
            Row(cf, "Retries", _retries, null);
            Row(cf, "Poll interval", _poll, "How often PV / SP / output are read.");
            Row(cf, "Control lines", lines, "Some isolated RS232 adapters are powered from DTR/RTS.");
            Row(cf, "Simulator", simRow, "Try the software without the station connected.");
            Row(cf, "", _connHint, null);
            FitCard(conn, cf);

            // ---------------------------------------------------------------- profiles
            _maxTemp = Num(50, 400, 0, 5, s.MaxTemperature, "°C", v => s.MaxTemperature = v);
            _unused = new ModernDropDown { Width = Theme.S(260) };
            _unused.SetItems(["Write zeros (recommended)", "Write END marker"], s.UnusedSteps == UnusedStepMode.EndMarker ? 1 : 0);
            _unused.SelectedIndexChanged += (o, e) => { s.UnusedSteps = _unused.SelectedIndex == 1 ? UnusedStepMode.EndMarker : UnusedStepMode.Zeros; Changed(); };
            _verify = Toggle("Read back and compare", s.VerifyAfterWrite, v => s.VerifyAfterWrite = v);

            var prof = Card("Profiles", "Limits and how profiles are written");
            var pf = Form(prof);
            Row(pf, "Maximum temperature", _maxTemp, "Set from the station; higher targets are rejected.");
            Row(pf, "Unused steps", _unused, "How segments after the last step are cleared.");
            Row(pf, "Verify after write", _verify, null);
            FitCard(prof, pf);

            // ---------------------------------------------------------------- logging
            _autoRecord = Toggle("Record every run automatically", s.AutoRecordRuns, v => s.AutoRecordRuns = v);
            _afterRun = Num(0, 3600, 0, 10, s.RecordAfterRunSeconds, "s", v => s.RecordAfterRunSeconds = (int)v);
            _folder = new ModernTextBox {
                Width = Theme.S(196),
                ReadOnly = true,
                Margin = Padding.Empty,
                Text = s.EffectiveLogFolder
            };
            var browse = new ModernButton("Browse…") { Margin = new Padding(Theme.S(8), 0, 0, 0) };
            browse.Click += (o, e) => BrowseFolder();
            var open = new ModernButton("", Glyph.Folder) { Margin = new Padding(Theme.S(6), 0, 0, 0) };
            open.Click += (o, e) => DashboardPage.OpenFolder(s.EffectiveLogFolder);
            var folderRow = Flow();
            folderRow.Controls.AddRange([_folder, browse, open]);

            var log = Card("Logging", "CSV files with timestamp, PV, SP, output and state");
            var lf = Form(log);
            Row(lf, "Auto-record", _autoRecord, null);
            Row(lf, "Keep recording after run", _afterRun, "Captures the cool-down after the program ends.");
            Row(lf, "Log folder", folderRow, null);
            FitCard(log, lf);

            // ---------------------------------------------------------------- about
            var about = new AboutPanel { Dock = DockStyle.Fill };
            var aboutCard = Card("About", "");
            aboutCard.Controls.Add(about);
            about.BringToFront();

            // ---------------------------------------------------------------- layout
            var grid = Table(2, 2);
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var rightCol = Table(1, 3);
            rightCol.Dock = DockStyle.Fill;
            rightCol.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) rightCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            prof.Dock = DockStyle.Top; log.Dock = DockStyle.Top; aboutCard.Dock = DockStyle.Top;
            prof.Margin = new Padding(Theme.S(8), 0, 0, Theme.S(16));
            log.Margin = new Padding(Theme.S(8), 0, 0, Theme.S(16));
            aboutCard.Margin = new Padding(Theme.S(8), 0, 0, 0);
            aboutCard.Height = Theme.S(250);
            rightCol.Controls.Add(prof, 0, 0);
            rightCol.Controls.Add(log, 0, 1);
            rightCol.Controls.Add(aboutCard, 0, 2);
            var leftCol = Table(1, 2);
            leftCol.Dock = DockStyle.Fill;
            leftCol.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 2; i++) leftCol.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            station.Dock = DockStyle.Top;
            station.Margin = new Padding(0, 0, Theme.S(8), Theme.S(16));
            conn.Dock = DockStyle.Top;
            conn.Margin = new Padding(0, 0, Theme.S(8), 0);
            leftCol.Controls.Add(station, 0, 0);
            leftCol.Controls.Add(conn, 0, 1);
            leftCol.Height = station.Height + conn.Height + Theme.S(16);
            grid.Controls.Add(leftCol, 0, 0);
            grid.Controls.Add(rightCol, 1, 0);
            rightCol.Height = prof.Height + log.Height + aboutCard.Height + Theme.S(32);

            var scroller = new Panel {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Theme.Window,
                Padding = new Padding(0, 0, Theme.S(6), Theme.S(8))
            };
            Theme.ApplyDarkScrollbars(scroller);
            scroller.Controls.Add(grid);
            Controls.Add(scroller);

            State.Device.StateChanged += (o, e) => UpdateHint();
            State.StationChanged += ShowStation;
            RefreshPorts();
            ShowStation();
            _loading = false;
            UpdateHint();
        }

        // ---------------------------------------------------------------- helpers

        private static ThemedLabel WrapLabel(Font font, Color color, int lines) => new("", font, color) {
            AutoSize = false,
            Size = new Size(Theme.S(330), font.Height * lines + Theme.S(4)),
        };

        /// <summary>Shows the selected station's details, units and temperature limit.</summary>
        private void ShowStation() {
            var s = State.Settings;
            var station = s.Station;
            var units = s.Units;
            bool wasLoading = _loading;
            _loading = true;
            _station.SelectedIndex = StationCatalog.IndexOf(station.Id);
            _stationControls.Text = station.Controls;
            _stationNotes.Text = station.Notes;
            _rampUnit.SelectedIndex = units.Ramp == RampUnit.PerMinute ? 1 : 0;
            _dwellUnit.SelectedIndex = units.Dwell == DwellUnit.Minutes ? 1 : 0;
            _rampUnit.Enabled = _dwellUnit.Enabled = station.CustomUnits;
            _maxTemp.Value = s.MaxTemperature;
            _loading = wasLoading;
            UpdateHint();
        }

        private NumberBox Num(double min, double max, int decimals, double step, double value, string suffix, Action<double> apply) {
            var n = new NumberBox(min, max, decimals, step) {
                Suffix = suffix,
                Width = Theme.S(130),
                Margin = Padding.Empty,
                Value = value
            };
            n.TextChanged += (o, e) => { if (!_loading && n.IsValid) { apply(n.Value.Value); Changed(); } };
            return n;
        }

        private ToggleSwitch Toggle(string text, bool value, Action<bool> apply) {
            var t = new ToggleSwitch(text) {
                Checked = value,
                Margin = Padding.Empty
            };
            t.CheckedChanged += (o, e) => { if (!_loading) { apply(t.Checked); Changed(); } };
            return t;
        }

        private static CardPanel Card(string title, string subtitle) {
            var c = new CardPanel { Padding = Theme.Pad(20, 16, 20, 18) };
            c.Controls.Add(new CardHeader(title, subtitle));
            return c;
        }

        private static TableLayoutPanel Form(CardPanel card) {
            var t = new TableLayoutPanel {
                ColumnCount = 2,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = new Padding(0, Theme.S(4), 0, 0),
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(158)));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.Controls.Add(t);
            t.BringToFront();
            return t;
        }

        private static void FitCard(CardPanel card, TableLayoutPanel form) {
            int h = card.Padding.Vertical;
            foreach (Control c in card.Controls)
                h += c == form ? form.GetPreferredSize(new Size(Theme.S(560), 0)).Height : c.Height;
            card.Height = h + Theme.S(6);
        }

        private static void Row(TableLayoutPanel t, string label, Control control, string hint) {
            int r = t.RowCount;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new ThemedLabel(label, Theme.Body, Theme.TextDim) { Margin = new Padding(0, Theme.S(9), Theme.S(8), 0) };
            t.Controls.Add(l, 0, r);
            var cell = new FlowLayoutPanel {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, Theme.S(12)),
            };
            control.Margin = Padding.Empty;
            cell.Controls.Add(control);
            if (!string.IsNullOrEmpty(hint))
                cell.Controls.Add(new ThemedLabel(hint, Theme.Small, Theme.TextFaint) { MaximumSize = new Size(Theme.S(320), 0), Margin = new Padding(0, Theme.S(4), 0, 0) });
            t.Controls.Add(cell, 1, r);
            t.RowCount = r + 1;
        }

        private void RefreshPorts() {
            var s = State.Settings;
            var ports = SerialTransport.GetPortNames().ToList();
            if (!string.IsNullOrEmpty(s.PortName) && !ports.Contains(s.PortName)) ports.Add(s.PortName + "  (not present)");
            bool wasLoading = _loading;
            _loading = true;
            if (ports.Count == 0) _port.SetItems(["No serial ports found"], 0);
            else {
                int idx = ports.FindIndex(p => p.Split(' ')[0] == s.PortName);
                _port.SetItems(ports, Math.Max(0, idx));
                if (idx < 0 && string.IsNullOrEmpty(s.PortName)) s.PortName = ports[0].Split(' ')[0];
            }
            _loading = wasLoading;
        }

        private void BrowseFolder() {
            using var d = new FolderBrowserDialog { SelectedPath = State.Settings.EffectiveLogFolder, Description = "Folder for CSV recordings" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            State.Settings.LogFolder = d.SelectedPath;
            _folder.Text = d.SelectedPath;
            Changed();
        }

        private void Changed() {
            if (_loading) return;
            State.Settings.Save();
            UpdateHint();
        }

        private void UpdateHint() {
            _connHint.Text = State.Device.IsConnected
                ? State.Device.IsSimulator
                    ? "Connected — connection and simulator unit changes take effect the next time you connect."
                    : "Connected — connection changes take effect the next time you connect."
                : "";
        }

        public override void OnShown() {
            if (!State.Device.IsConnected) RefreshPorts();
        }
    }

    /// <summary>About box content.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class AboutPanel : ThemedControl {
        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Theme.HighQuality(g);
            int logo = Theme.S(56);
            LogoPainter.Draw(g, new RectangleF(0, Theme.S(6), logo, logo));
            var asm = Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version;
            int x = logo + Theme.S(16);
            TextRenderer.DrawText(g, "ReflowLink", Theme.Heading, new Point(x, Theme.S(8)), Theme.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, $"Version {ver.Major}.{ver.Minor}.{ver.Build}  ·  .NET Framework 4.8", Theme.Small, new Point(x, Theme.S(32)), Theme.TextDim, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "© 2026 Marius Sumutiu. All rights reserved.", Theme.Small, new Point(x, Theme.S(50)), Theme.TextDim, TextFormatFlags.NoPadding);
            string note = "Control software for reflow and BGA rework stations with the ALTEC PC410 temperature controller " +
                          "(AL808 / PC900 serial protocol): ACHI IR6500, IR6000, IR-PRO-SC and other PC410 stations.\n\n" +
                          "The station heats to soldering temperatures: never leave a running program unattended.";
            TextRenderer.DrawText(g, note, Theme.Small, new Rectangle(0, Theme.S(80), Width, Height - Theme.S(80)), Theme.TextFaint,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }
}
