// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Protocol;
using ReflowLink.Services;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI.Pages {
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class SettingsPage : PageBase {
        /// <summary>The controls of one controller's connection card.</summary>
        private sealed class ConnectionCard {
            public IConnectionSettings Settings;
            public ControllerChannel Channel;
            public CardPanel Card;
            public TableLayoutPanel Form;
            public ModernDropDown Port, Baud, SimSpeed;
            public ThemedLabel Hint;
        }

        /// <summary>The controls of one controller's limit card (Top IR Profiles / Bed Heat Profiles).</summary>
        private sealed class LimitCard {
            public ControllerChannel Channel;
            public CardPanel Card;
            public TableLayoutPanel Form;
            public NumberBox MaxTemp;
            public ThemedLabel ControllerLimit, Warning;
            public bool Committing;
        }

        private readonly ModernDropDown _unused, _bedUnused, _station, _rampUnit, _dwellUnit, _bedRamp, _bedDwell, _bedMode;
        private readonly ThemedLabel _stationControls, _stationNotes;
        private readonly ToggleSwitch _verify, _bedVerify, _autoRecord, _irIn2, _bedIn2;
        private readonly NumberBox _afterRun, _maxWait;
        private readonly ModernTextBox _folder;
        private readonly ConnectionCard _irConn, _bedConn;
        private readonly LimitCard _irLimit, _bedLimit;
        private bool _loading;

        private static readonly double[] SimSpeeds = [1, 2, 5, 10, 20];

        public SettingsPage(AppState state) : base(state, "Settings", "Station, connections, profile limits and logging") {
            var s = State.Settings;
            _loading = true;

            // ---------------------------------------------------------------- station
            _station = new ModernDropDown { Width = Theme.S(280) };
            _station.SetItems(StationCatalog.All.Select(st => st.Name), StationCatalog.IndexOf(s.StationId));
            _station.SelectedIndexChanged += (o, e) => {
                if (_loading || _station.SelectedIndex < 0) return;
                var next = StationCatalog.All[_station.SelectedIndex];
                State.ChangeStation(next.Id, KeepCustomLimit(next));
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
            _bedMode = new ModernDropDown { Width = Theme.S(280) };
            _bedMode.SetItems(BedModeNames, (int)s.Bed.Mode);
            _bedMode.SelectedIndexChanged += (o, e) => {
                if (_loading || _bedMode.SelectedIndex < 0) return;
                var mode = (BedMode)_bedMode.SelectedIndex;
                if (!ConfirmBedMode(mode)) {
                    ShowBedEnabled();
                    return;
                }
                if (!State.SetBedMode(mode, () => DarkDialog.Confirm(FindForm(), "Bed not confirmed off",
                        "The Top IR controller didn't confirm that OUT2 was switched off (A2 = 500). After this change ReflowLink no longer "
                        + "regulates the bed, so check it on the station.", "Change anyway", "Cancel", danger: true)))
                    ShowBedEnabled();
            };

            var station = Card("Station", "Which reflow / rework station the PC410 is in");
            var stf = Form(station);
            Row(stf, "Station", _station, null);
            Row(stf, "Controller", _stationControls, null);
            Row(stf, "Ramp rate unit", _rampUnit, "Profiles are always edited in °C/s.");
            Row(stf, "Hold time unit", _dwellUnit, "Profiles are always edited in seconds.");
            Row(stf, "", _stationNotes, null);
            Row(stf, "Bed heat", _bedMode, "Single PC410: the bed thermocouple on IN2 and the bed controller's output through OUT2, regulated by ReflowLink. "
                + "Second PC410: a controller of its own, with its own serial port.");
            FitCard(station, stf);

            // ---------------------------------------------------------------- connections
            _irIn2 = Toggle("Read IN2 (P2)", s.ReadIn2, v => State.SetIn2Reading(false, v));
            _irConn = BuildConnection("Top IR Connection", "Serial link to the PC410 that controls the top IR heater", s, State.Ir,
                ("IN2 reading", _irIn2, "Shows this controller's second input (IN2, terminals 16 + / 17 −) as “IN2 (Top IR)” on the Dashboard, chart and CSV, "
                    + "for information only. With single PC410 bed control IN2 is the bed temperature, so it's always read. A thermocouple shared with "
                    + "another controller can read a few degrees off: compare it once with that controller's display."));
            _bedRamp = new ModernDropDown { Width = Theme.S(180) };
            _bedRamp.SetItems(["°C per second", "°C per minute"], s.Bed.RampUnit == RampUnit.PerMinute ? 1 : 0);
            _bedRamp.SelectedIndexChanged += (o, e) => {
                if (_loading) return;
                s.Bed.RampUnit = _bedRamp.SelectedIndex == 1 ? RampUnit.PerMinute : RampUnit.PerSecond;
                State.ApplyStation();
            };
            _bedDwell = new ModernDropDown { Width = Theme.S(180) };
            _bedDwell.SetItems(["Seconds", "Minutes"], s.Bed.DwellUnit == DwellUnit.Minutes ? 1 : 0);
            _bedDwell.SelectedIndexChanged += (o, e) => {
                if (_loading) return;
                s.Bed.DwellUnit = _bedDwell.SelectedIndex == 1 ? DwellUnit.Minutes : DwellUnit.Seconds;
                State.ApplyStation();
            };
            _bedIn2 = Toggle("Read IN2 (P2)", s.Bed.ReadIn2, v => State.SetIn2Reading(true, v));
            _bedConn = BuildConnection("Bed Heat Connection", "Serial link to the second PC410 that controls the bottom heater", s.Bed, State.BedChannel,
                ("Ramp rate unit", _bedRamp, "How the bed PC410 reads ramp rates. ACHI-style controllers use °C per second."),
                ("Hold time unit", _bedDwell, "How the bed PC410 reads hold times."),
                ("IN2 reading", _bedIn2, "Shows the second PC410's second input as “IN2 (Bed Heat)” on the Dashboard, chart and CSV, for information only."));

            // ---------------------------------------------------------------- profile limits
            _irLimit = BuildLimit("Top IR Profiles", "Limits and how profiles are written", State.Ir, s.MaxTemperature);
            _unused = new ModernDropDown { Width = Theme.S(260) };
            _unused.SetItems(["Write zeros (recommended)", "Write END marker"], s.UnusedSteps == UnusedStepMode.EndMarker ? 1 : 0);
            _unused.SelectedIndexChanged += (o, e) => { s.UnusedSteps = _unused.SelectedIndex == 1 ? UnusedStepMode.EndMarker : UnusedStepMode.Zeros; Changed(); };
            _verify = Toggle("Read back and compare", s.VerifyAfterWrite, v => s.VerifyAfterWrite = v);
            Row(_irLimit.Form, "Unused steps", _unused, "How segments after the last step are cleared.");
            Row(_irLimit.Form, "Verify after write", _verify, null);
            FitCard(_irLimit.Card, _irLimit.Form);

            _bedLimit = BuildLimit("Bed Heat Profiles", "Bed limit, and how a second PC410's patterns are written", State.BedChannel, s.Bed.MaxTemperature);
            _bedUnused = new ModernDropDown { Width = Theme.S(260) };
            _bedUnused.SetItems(["Write zeros (recommended)", "Write END marker"], s.Bed.UnusedSteps == UnusedStepMode.EndMarker ? 1 : 0);
            _bedUnused.SelectedIndexChanged += (o, e) => { s.Bed.UnusedSteps = _bedUnused.SelectedIndex == 1 ? UnusedStepMode.EndMarker : UnusedStepMode.Zeros; Changed(); };
            _bedVerify = Toggle("Read back and compare", s.Bed.VerifyAfterWrite, v => s.Bed.VerifyAfterWrite = v);
            Row(_bedLimit.Form, "Unused steps", _bedUnused, "How segments after the last step are cleared on the bed controller (second PC410).");
            Row(_bedLimit.Form, "Verify after write", _bedVerify, "Second PC410 only. With a single PC410 the bed targets are kept by ReflowLink.");
            _maxWait = Num(0, 240, 0, 1, s.MaxWaitMinutes, "min", v => { s.MaxWaitMinutes = v; State.Sync.MaxWaitMinutes = v; });
            Row(_bedLimit.Form, "Longest wait", _maxWait, "Wait for Bed / Wait for IR: after this long ReflowLink asks whether to keep waiting or stop (0 = never ask).");
            FitCard(_bedLimit.Card, _bedLimit.Form);

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

            var csvFormat = new ModernDropDown { Width = Theme.S(260) };
            csvFormat.SetItems(["Standard (commas, decimal points)", "This PC's format (Excel opens it here)"], s.CsvLocalFormat ? 1 : 0);
            csvFormat.SelectedIndexChanged += (o, e) => { s.CsvLocalFormat = csvFormat.SelectedIndex == 1; Changed(); };

            var log = Card("Logging", "CSV files with timestamp, temperatures, setpoints, output and state");
            var lf = Form(log);
            Row(lf, "Auto-record", _autoRecord, null);
            Row(lf, "Keep recording after run", _afterRun, "Captures the cool-down after the program ends.");
            Row(lf, "Log folder", folderRow, null);
            Row(lf, "CSV format", csvFormat, "This PC's format uses its list separator and decimal mark, so Excel opens the file in columns where the decimal mark is a comma.");
            FitCard(log, lf);

            // ---------------------------------------------------------------- about
            var about = new AboutPanel { Dock = DockStyle.Fill };
            var aboutCard = Card("About", "");
            var copy = new ModernButton("Copy diagnostics", Glyph.None) { Margin = new Padding(0, 0, Theme.S(8), 0) };
            copy.Click += (o, e) => CopyDiagnostics();
            var events = new ModernButton("Event log", Glyph.Folder) { Margin = Padding.Empty };
            events.Click += (o, e) => DashboardPage.OpenFolder(AppLog.Folder);
            var tip = Theme.CreateToolTip();
            tip.SetToolTip(copy, "Copies the version, setup, connection state and recent events, to paste into a support request");
            tip.SetToolTip(events, "Open the folder with the event logs and crash reports");
            var update = new ModernButton("Check for updates", Glyph.Download) { Margin = new Padding(0, 0, Theme.S(8), 0) };
            update.Click += async (o, e) => await State.Updates.CheckNowAsync();
            tip.SetToolTip(update, "Ask GitHub whether a newer ReflowLink has been released");
            var autoUpdate = Toggle("Check for updates at start-up", s.CheckForUpdates, v => s.CheckForUpdates = v);
            autoUpdate.Margin = new Padding(0, 0, 0, Theme.S(10));
            tip.SetToolTip(autoUpdate, "At most once a day. Only GitHub's public release information is read; nothing is downloaded or installed.");
            var updateRow = Flow();
            updateRow.Dock = DockStyle.Bottom;
            updateRow.Controls.Add(autoUpdate);
            var aboutButtons = Flow();
            aboutButtons.Dock = DockStyle.Bottom;
            aboutButtons.Controls.AddRange([update, copy, events]);
            aboutCard.Controls.Add(about);
            aboutCard.Controls.Add(updateRow);
            aboutCard.Controls.Add(aboutButtons);
            about.BringToFront();
            aboutCard.Height = Theme.S(330);

            // ---------------------------------------------------------------- layout
            var grid = Table(2, 1);
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(Column(true, station, _irConn.Card, _bedConn.Card), 0, 0);
            grid.Controls.Add(Column(false, _irLimit.Card, _bedLimit.Card, log, aboutCard), 1, 0);

            var scroller = new Panel {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Theme.Window,
                Padding = new Padding(0, 0, Theme.S(6), Theme.S(8))
            };
            Theme.ApplyDarkScrollbars(scroller);
            scroller.Controls.Add(grid);
            Controls.Add(scroller);

            State.Device.StateChanged += (o, e) => { UpdateHints(); UpdateLimitInfo(_irLimit); };
            State.Bed.StateChanged += (o, e) => { UpdateHints(); UpdateLimitInfo(_bedLimit); };
            State.StationChanged += ShowStation;
            State.BedEnabledChanged += ShowBedEnabled;
            RefreshPorts(_irConn);
            RefreshPorts(_bedConn);
            ShowStation();
            ShowBedEnabled();
            _loading = false;
            UpdateHints();
        }

        // ---------------------------------------------------------------- builders

        private ConnectionCard BuildConnection(string title, string subtitle, IConnectionSettings cs, ControllerChannel channel,
            params (string Label, Control Control, string Hint)[] extra) {
            var c = new ConnectionCard { Settings = cs, Channel = channel };
            c.Port = new ModernDropDown { Width = Theme.S(200), Margin = Padding.Empty };
            var refresh = new ModernButton("", Glyph.Refresh) { Margin = new Padding(Theme.S(8), 0, 0, 0) };
            refresh.Click += (o, e) => RefreshPorts(c);
            Theme.CreateToolTip().SetToolTip(refresh, "Refresh the list of serial ports");
            var portRow = Flow();
            portRow.Controls.Add(c.Port);
            portRow.Controls.Add(refresh);
            c.Port.SelectedIndexChanged += (o, e) => {
                if (_loading || c.Port.SelectedItem == null || c.Port.SelectedItem.StartsWith("No serial", StringComparison.Ordinal)) return;
                cs.PortName = c.Port.SelectedItem.Split(' ')[0];
                Changed();
            };

            // A rate or speed set outside the list (an edited settings file) is shown as it is, not as the first entry.
            var bauds = Pc410Protocol.StandardBaudRates.ToList();
            if (!bauds.Contains(cs.BaudRate)) bauds.Add(cs.BaudRate);
            c.Baud = new ModernDropDown { Width = Theme.S(200) };
            c.Baud.SetItems(bauds.Select(b => b + " baud"), bauds.IndexOf(cs.BaudRate));
            c.Baud.SelectedIndexChanged += (o, e) => { cs.BaudRate = bauds[c.Baud.SelectedIndex]; Changed(); };

            var address = Num(0, 99, 0, 1, cs.Address, "", v => cs.Address = (int)v);
            var timeout = Num(100, 5000, 0, 50, cs.ResponseTimeoutMs, "ms", v => cs.ResponseTimeoutMs = (int)v);
            var retries = Num(0, 5, 0, 1, cs.Retries, "", v => cs.Retries = (int)v);
            var poll = Num(200, 10000, 0, 100, cs.PollIntervalMs, "ms", v => { cs.PollIntervalMs = (int)v; channel.Device.PollIntervalMs = (int)v; });
            var dtr = Toggle("DTR on", cs.DtrEnable, v => cs.DtrEnable = v);
            var rts = Toggle("RTS on", cs.RtsEnable, v => cs.RtsEnable = v);
            var lines = Flow();
            lines.Controls.Add(dtr);
            lines.Controls.Add(rts);
            dtr.Margin = new Padding(0, 0, Theme.S(20), 0);

            var sim = Toggle("Use built-in simulator", cs.UseSimulator, v => { cs.UseSimulator = v; c.SimSpeed.Enabled = v && c.Form.Enabled; });
            var speeds = SimSpeeds.ToList();
            if (!speeds.Contains(cs.SimulatorSpeed)) speeds.Add(cs.SimulatorSpeed);
            c.SimSpeed = new ModernDropDown { Width = Theme.S(120), Enabled = cs.UseSimulator, Margin = new Padding(Theme.S(16), 0, 0, 0) };
            c.SimSpeed.SetItems(speeds.Select(v => v + "× speed"), speeds.IndexOf(cs.SimulatorSpeed));
            c.SimSpeed.SelectedIndexChanged += (o, e) => { cs.SimulatorSpeed = speeds[c.SimSpeed.SelectedIndex]; Changed(); };
            var simRow = Flow();
            simRow.Controls.Add(sim);
            simRow.Controls.Add(c.SimSpeed);
            sim.Margin = new Padding(0, Theme.S(3), 0, 0);

            c.Hint = new ThemedLabel("", Theme.Small, Theme.Yellow) { AutoSize = true, MaximumSize = new Size(Theme.S(320), 0) };

            c.Card = Card(title, subtitle);
            c.Form = Form(c.Card);
            Row(c.Form, "Serial port", portRow, "USB-to-RS232 adapters appear as COM ports. Each PC410 needs its own port.");
            Row(c.Form, "Baud rate", c.Baud, "Must match the controller's communication setting.");
            Row(c.Form, "Data format", new ThemedLabel("7 data bits · even parity · 1 stop bit", Theme.Body, Theme.Text), "Fixed: required by the PC410 protocol.");
            Row(c.Form, "Device address", address, "Controller address 0–99 (often 1).");
            Row(c.Form, "Response timeout", timeout, null);
            Row(c.Form, "Retries", retries, null);
            Row(c.Form, "Poll interval", poll, "How often PV / SP / output are read.");
            Row(c.Form, "Control lines", lines, "Some isolated RS232 adapters are powered from DTR/RTS.");
            Row(c.Form, "Simulator", simRow, "Try the software without the station connected.");
            foreach (var (label, control, hint) in extra) Row(c.Form, label, control, hint);
            Row(c.Form, "", c.Hint, null);
            FitCard(c.Card, c.Form);
            return c;
        }

        private LimitCard BuildLimit(string title, string subtitle, ControllerChannel channel, double value) {
            var l = new LimitCard { Channel = channel };
            l.MaxTemp = new NumberBox(50, 400, 0, 5) {
                Suffix = "°C",
                Width = Theme.S(130),
                Margin = Padding.Empty,
                Value = value,
            };
            l.MaxTemp.Committed += (o, e) => CommitMaxTemperature(l);
            l.ControllerLimit = WrapLabel(Theme.Body, Theme.TextDim, 1);
            l.Warning = WrapLabel(Theme.Small, Theme.Yellow, 2);
            l.Card = Card(title, subtitle);
            l.Form = Form(l.Card);
            Row(l.Form, "Maximum temperature", l.MaxTemp,
                (channel.IsBed ? "Highest bed target allowed." : "Highest IR target allowed.") + " Defaults to the controller's factory setpoint limit (SPH). Raising it here doesn't change the controller.");
            Row(l.Form, "Controller limit", l.ControllerLimit, null);
            Row(l.Form, "", l.Warning, null);
            return l;
        }

        /// <summary>One settings column: cards stacked from the top, with the column height set to fit them.</summary>
        private static TableLayoutPanel Column(bool left, params CardPanel[] cards) {
            var col = Table(1, cards.Length);
            col.Dock = DockStyle.Fill;
            col.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int height = 0;
            for (int i = 0; i < cards.Length; i++) {
                col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var card = cards[i];
                card.Dock = DockStyle.Top;
                int bottom = i < cards.Length - 1 ? Theme.S(16) : 0;
                card.Margin = left ? new Padding(0, 0, Theme.S(8), bottom) : new Padding(Theme.S(8), 0, 0, bottom);
                col.Controls.Add(card, 0, i);
                height += card.Height + bottom;
            }
            col.Height = height;
            return col;
        }

        private static ThemedLabel WrapLabel(Font font, Color color, int lines) => new("", font, color) {
            AutoSize = false,
            Size = new Size(Theme.S(330), font.Height * lines + Theme.S(4)),
        };

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

        // ---------------------------------------------------------------- behaviour

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
            _irLimit.MaxTemp.Value = s.MaxTemperature;
            _bedLimit.MaxTemp.Value = s.Bed.MaxTemperature;
            _loading = wasLoading;
            UpdateLimitInfo(_irLimit);
            UpdateLimitInfo(_bedLimit);
            UpdateHints();
        }

        private static readonly string[] BedModeNames = ["Off (top IR only)", "Single PC410 (IN2 + OUT2)", "Second PC410"];

        /// <summary>
        /// A station change normally sets its default temperature limit. If the user had set a limit of their own, ask
        /// whether to keep it. True keeps the current limit.
        /// </summary>
        private bool KeepCustomLimit(StationModel next) {
            var s = State.Settings;
            if (!s.CustomLimit || Math.Abs(next.MaxTemperature - s.MaxTemperature) < 0.01) return false;
            int r = DarkDialog.Show(FindForm(), "Keep your temperature limit?",
                $"The Top IR limit is set to {s.MaxTemperature:0.#} °C (the {s.Station.Name} default is {s.Station.MaxTemperature:0.#} °C). "
                + $"The {next.Name} default is {next.MaxTemperature:0.#} °C.",
                DialogTone.Question, [$"Use {next.MaxTemperature:0.#} °C", $"Keep {s.MaxTemperature:0.#} °C"], 0);
            return r == 1;
        }

        /// <summary>Copies what helps with a support request: version, setup, connections, limits and recent events.</summary>
        private void CopyDiagnostics() {
            var s = State.Settings;
            var sb = new System.Text.StringBuilder();
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            sb.AppendLine($"ReflowLink {ver}  ·  Windows {Environment.OSVersion.Version}  ·  .NET {Environment.Version}");
            sb.AppendLine($"Station: {s.Station.Name}  ·  units: ramp {s.Units.Ramp}, hold {s.Units.Dwell}  ·  limit {s.MaxTemperature:0.#} °C");
            sb.AppendLine($"Bed heat: {State.BedMode}  ·  bed limit {s.Bed.MaxTemperature:0.#} °C  ·  IN2 readings: Top IR {s.ReadIn2}, Bed {s.Bed.ReadIn2}");
            if (State.SingleBed) {
                var w = s.Bed.Switching;
                sb.AppendLine($"Bed switching: band {w.ProportionalBand:0.#} °C, integral {w.IntegralSeconds:0} s, cycle {w.CycleSeconds:0} s, A2 writes {w.TotalWrites}");
            }
            foreach (var ch in new[] { State.Ir, State.BedChannel }) {
                var d = ch.Device;
                IConnectionSettings cs = ch.IsBed ? s.Bed : s;
                string link = d.IsConnected ? $"{d.State}, {d.LinkName}, address {d.Address:00}" : "not connected";
                string limits = d.ControllerLimits.Known ? $", HS {d.ControllerLimits.High?.ToString("0.#") ?? "?"} / LS {d.ControllerLimits.Low?.ToString("0.#") ?? "?"}" : "";
                sb.AppendLine($"{ch.Name}: {link}{limits}  ·  settings {(cs.UseSimulator ? "simulator" : cs.PortName)} {cs.BaudRate} baud, timeout {cs.ResponseTimeoutMs} ms, retries {cs.Retries}, poll {cs.PollIntervalMs} ms");
                if (!string.IsNullOrEmpty(d.LastError)) sb.AppendLine($"  last error: {d.LastError}");
            }
            sb.AppendLine($"Blocked patterns: {(s.BlockedPatterns.Count == 0 ? "none" : string.Join(", ", s.BlockedPatterns))}  ·  segments paired: {s.SegmentsPaired}");
            sb.AppendLine();
            sb.AppendLine("Recent events:");
            sb.AppendLine(AppLog.Tail(80));
            try {
                Clipboard.SetText(sb.ToString());
                State.SetStatus("Diagnostics copied to the clipboard", StatusKind.Success);
            } catch (Exception ex) {
                DarkDialog.Error(FindForm(), "Couldn't copy", ex.Message);
            }
        }

        /// <summary>Asks before changing the bed hardware when it matters. False keeps the current choice.</summary>
        private bool ConfirmBedMode(BedMode mode) {
            var form = FindForm();
            bool bedRunning = State.DualBed && State.Bed.IsConnected && State.Bed.Last?.State is ProgramState.Running or ProgramState.Hold;
            bool running = bedRunning || (State.Device.IsConnected && State.Device.Last?.State is ProgramState.Running or ProgramState.Hold);
            if (running && !DarkDialog.Confirm(form, "Change the bed heat hardware during a run?",
                    bedRunning
                        ? "The bed controller is running a program. Changing this disconnects it: it keeps running on its own, but ReflowLink can no longer keep the heaters in step or stop it."
                        : State.SingleBed
                            ? "ReflowLink regulates the bed during this run. Changing this switches the bed off (OUT2)."
                            : "A program is running. The new bed setup applies from the next run.",
                    "Change", "Cancel", danger: true))
                return false;
            if (mode != BedMode.SinglePc410) return true;
            return DarkDialog.Confirm(form, "Single PC410 bed control",
                "ReflowLink regulates the bed through the Top IR PC410:\n\n"
                + "• The bed thermocouple is wired to IN2 (terminals 16 + / 17 −) and read as P2.\n"
                + "• OUT2 (terminals 6–7) is wired in series with the bed controller's output to the bed SSR.\n"
                + "• On the PC410 (LOC 808): 0P2 = AL02 and AL02 = HIAL. ReflowLink then switches OUT2 by writing A2 = 001 (on) and A2 = 500 (off).\n\n"
                + "The bed controller behind OUT2 (or a thermostat) must stay as a hardware limit: if ReflowLink closes or the cable is pulled "
                + "while OUT2 is on, nothing else stops the bed.",
                "Use single PC410", "Cancel", danger: true);
        }

        /// <summary>Shows the bed hardware and greys out the bed cards that don't apply to it.</summary>
        private void ShowBedEnabled() {
            bool on = State.BedEnabled, dual = State.DualBed;
            bool wasLoading = _loading;
            _loading = true;
            _bedMode.SelectedIndex = (int)State.BedMode;
            // Single PC410: IN2 is the bed temperature, so it's read whatever the setting says.
            _irIn2.Checked = State.SingleBed || State.Settings.ReadIn2;
            _irIn2.Enabled = !State.SingleBed;
            _bedIn2.Checked = State.Settings.Bed.ReadIn2;
            _loading = wasLoading;
            _bedConn.Form.Enabled = dual;
            _bedConn.SimSpeed.Enabled = dual && State.Settings.Bed.UseSimulator;
            _bedLimit.Form.Enabled = on;
            _bedUnused.Enabled = _bedVerify.Enabled = dual;
            _bedConn.Card.Invalidate(true);
            _bedLimit.Card.Invalidate(true);
            UpdateLimitInfo(_bedLimit);
            UpdateHints();
        }

        private void RefreshPorts(ConnectionCard c) {
            var cs = c.Settings;
            var ports = SerialTransport.GetPortNames().ToList();
            if (!string.IsNullOrEmpty(cs.PortName) && !ports.Contains(cs.PortName)) ports.Add(cs.PortName + "  (not present)");
            bool wasLoading = _loading;
            _loading = true;
            if (ports.Count == 0) c.Port.SetItems(["No serial ports found"], 0);
            else {
                int idx = ports.FindIndex(p => p.Split(' ')[0] == cs.PortName);
                if (idx < 0 && string.IsNullOrEmpty(cs.PortName)) {
                    // Suggest a port the other controller doesn't use.
                    string other = c.Channel.IsBed ? State.Settings.PortName : State.Settings.Bed.PortName;
                    idx = Math.Max(0, ports.FindIndex(p => !string.Equals(p.Split(' ')[0], other, StringComparison.OrdinalIgnoreCase)));
                    cs.PortName = ports[idx].Split(' ')[0];
                }
                c.Port.SetItems(ports, Math.Max(0, idx));
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

        private double GetMax(LimitCard l) => l.Channel.IsBed ? State.Settings.Bed.MaxTemperature : State.Settings.MaxTemperature;

        private void SetMax(LimitCard l, double value) {
            if (l.Channel.IsBed) State.Settings.Bed.MaxTemperature = value;
            else State.Settings.MaxTemperature = value;
        }

        /// <summary>
        /// Applies a new maximum temperature. Raising it above the factory setpoint limit asks for confirmation, because the
        /// controller's own setpoint limit (HS) still applies.
        /// </summary>
        private void CommitMaxTemperature(LimitCard l) {
            if (_loading || l.Committing) return;
            if (l.MaxTemp.Value is not double value || !l.MaxTemp.IsValid) {
                ShowMaxTemperature(l);
                return;
            }
            double current = GetMax(l);
            if (Math.Abs(value - current) < 0.01) return;

            double factory = l.Channel.IsBed ? StationModel.DefaultMaxTemperature : State.Settings.Station.MaxTemperature;
            // With a single PC410 the bed limit is only ReflowLink's own cut-off: no controller setpoint limit applies to it.
            if (value > factory && value > current && !(l.Channel.IsBed && State.SingleBed)) {
                string hs = l.Channel.Device.ControllerLimits.High is double h
                    ? $"\n\nThis controller currently reports HS = {h:0.#} °C."
                    : "";
                string who = l.Channel.IsBed ? "A PC410" : $"The {State.Settings.Station.Name}'s controller";
                string message =
                    $"{who} ships with its setpoint limit (SPH) at {factory:0.#} °C.\n\n" +
                    $"Raising the limit in ReflowLink doesn't change the {l.Channel.Name} controller. Unless you also raise its own " +
                    "setpoint limit (HS), it will reject targets above it, and ReflowLink will refuse to write or start those profiles." + hs;
                l.Committing = true; // the dialog takes focus, which would commit again
                bool raise = DarkDialog.Confirm(FindForm(), $"Raise the {l.Channel.Name} limit to {value:0.#} °C?", message, "Raise limit", "Cancel", danger: true);
                l.Committing = false;
                if (!raise) {
                    ShowMaxTemperature(l);
                    return;
                }
            }
            SetMax(l, value);
            State.ApplyStation(); // saves and refreshes the pages that validate against the limit
            UpdateLimitInfo(l);
        }

        private void ShowMaxTemperature(LimitCard l) {
            bool wasLoading = _loading;
            _loading = true;
            l.MaxTemp.Value = GetMax(l);
            _loading = wasLoading;
        }

        /// <summary>Shows the controller's setpoint limits and warns when ReflowLink's limit is above HS.</summary>
        private void UpdateLimitInfo(LimitCard l) {
            var device = l.Channel.Device;
            var limits = device.ControllerLimits;
            if (l.Channel.IsBed && !State.BedEnabled) {
                l.ControllerLimit.Text = "Bed heat control is off.";
            } else if (l.Channel.IsBed && State.SingleBed) {
                l.ControllerLimit.Text = "Single PC410: the bed is switched off above it.";
            } else if (!device.IsConnected) {
                l.ControllerLimit.Text = "Read from the controller when you connect.";
            } else if (limits.High is double high) {
                l.ControllerLimit.Text = $"HS {high:0.#} °C" + (limits.Low is double low ? $"  ·  LS {low:0.#} °C" : "") + "  (read from the controller)";
            } else {
                l.ControllerLimit.Text = "Not reported by this controller.";
            }
            l.Warning.Text = device.IsConnected && limits.High is double hs && GetMax(l) > hs + 0.05
                ? $"ReflowLink's limit is above the controller's HS: targets above {hs:0.#} °C will be refused."
                : "";
        }

        private void Changed() {
            if (_loading) return;
            State.Settings.Save();
            UpdateHints();
        }

        private void UpdateHints() {
            foreach (var c in new[] { _irConn, _bedConn }) {
                if (c == null) continue;
                var device = c.Channel.Device;
                c.Hint.Text = device.IsConnected
                    ? device.IsSimulator
                        ? "Connected — connection and simulator unit changes take effect the next time you connect."
                        : "Connected — connection changes take effect the next time you connect."
                    : "";
            }
        }

        public override void OnShown() {
            if (!State.Device.IsConnected) RefreshPorts(_irConn);
            if (!State.Bed.IsConnected) RefreshPorts(_bedConn);
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
