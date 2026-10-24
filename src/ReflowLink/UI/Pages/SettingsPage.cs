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

        private readonly ModernDropDown _unused, _bedUnused, _station, _rampUnit, _dwellUnit, _bedRamp, _bedDwell;
        private readonly ThemedLabel _stationControls, _stationNotes;
        private readonly ToggleSwitch _useBed, _verify, _bedVerify, _autoRecord;
        private readonly NumberBox _afterRun;
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
            _useBed = new ToggleSwitch("Use Bed Heat Control") { Checked = s.Bed.Enabled, Margin = Padding.Empty };
            _useBed.CheckedChanged += (o, e) => {
                if (_loading) return;
                bool bedRunning = State.Bed.IsConnected && State.Bed.Last?.State is ProgramState.Running or ProgramState.Hold;
                if (!_useBed.Checked && bedRunning &&
                    !DarkDialog.Confirm(FindForm(), "Switch off bed heat control?",
                        "The bed controller is running a program. Switching off disconnects it: it keeps running on its own, but ReflowLink can no longer keep the heaters in step or stop it.",
                        "Switch off", "Cancel", danger: true)) {
                    _loading = true;
                    _useBed.Checked = true;
                    _loading = false;
                    return;
                }
                State.SetBedEnabled(_useBed.Checked);
            };

            var station = Card("Station", "Which reflow / rework station the PC410 is in");
            var stf = Form(station);
            Row(stf, "Station", _station, null);
            Row(stf, "Controller", _stationControls, null);
            Row(stf, "Ramp rate unit", _rampUnit, "Profiles are always edited in °C/s.");
            Row(stf, "Hold time unit", _dwellUnit, "Profiles are always edited in seconds.");
            Row(stf, "", _stationNotes, null);
            Row(stf, "Bed heat", _useBed, "A second PC410 controls the bottom heater. Switches on the Bed Heat connection, profile columns, console and control tuning.");
            FitCard(station, stf);

            // ---------------------------------------------------------------- connections
            _irConn = BuildConnection("Top IR Connection", "Serial link to the PC410 that controls the top IR heater", s, State.Ir);
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
            _bedConn = BuildConnection("Bed Heat Connection", "Serial link to the second PC410 that controls the bottom heater", s.Bed, State.BedChannel,
                ("Ramp rate unit", _bedRamp, "How the bed PC410 reads ramp rates. ACHI-style controllers use °C per second."),
                ("Hold time unit", _bedDwell, "How the bed PC410 reads hold times."));

            // ---------------------------------------------------------------- profile limits
            _irLimit = BuildLimit("Top IR Profiles", "Limits and how profiles are written", State.Ir, s.MaxTemperature);
            _unused = new ModernDropDown { Width = Theme.S(260) };
            _unused.SetItems(["Write zeros (recommended)", "Write END marker"], s.UnusedSteps == UnusedStepMode.EndMarker ? 1 : 0);
            _unused.SelectedIndexChanged += (o, e) => { s.UnusedSteps = _unused.SelectedIndex == 1 ? UnusedStepMode.EndMarker : UnusedStepMode.Zeros; Changed(); };
            _verify = Toggle("Read back and compare", s.VerifyAfterWrite, v => s.VerifyAfterWrite = v);
            Row(_irLimit.Form, "Unused steps", _unused, "How segments after the last step are cleared.");
            Row(_irLimit.Form, "Verify after write", _verify, null);
            FitCard(_irLimit.Card, _irLimit.Form);

            _bedLimit = BuildLimit("Bed Heat Profiles", "Limits and how bed profiles are written", State.BedChannel, s.Bed.MaxTemperature);
            _bedUnused = new ModernDropDown { Width = Theme.S(260) };
            _bedUnused.SetItems(["Write zeros (recommended)", "Write END marker"], s.Bed.UnusedSteps == UnusedStepMode.EndMarker ? 1 : 0);
            _bedUnused.SelectedIndexChanged += (o, e) => { s.Bed.UnusedSteps = _bedUnused.SelectedIndex == 1 ? UnusedStepMode.EndMarker : UnusedStepMode.Zeros; Changed(); };
            _bedVerify = Toggle("Read back and compare", s.Bed.VerifyAfterWrite, v => s.Bed.VerifyAfterWrite = v);
            Row(_bedLimit.Form, "Unused steps", _bedUnused, "How segments after the last step are cleared on the bed controller.");
            Row(_bedLimit.Form, "Verify after write", _bedVerify, null);
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

            var log = Card("Logging", "CSV files with timestamp, temperatures, setpoints, output and state");
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
            aboutCard.Height = Theme.S(250);

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

            c.Baud = new ModernDropDown { Width = Theme.S(200) };
            c.Baud.SetItems(Pc410Protocol.StandardBaudRates.Select(b => b + " baud"), Math.Max(0, Array.IndexOf(Pc410Protocol.StandardBaudRates, cs.BaudRate)));
            c.Baud.SelectedIndexChanged += (o, e) => { cs.BaudRate = Pc410Protocol.StandardBaudRates[c.Baud.SelectedIndex]; Changed(); };

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
            c.SimSpeed = new ModernDropDown { Width = Theme.S(120), Enabled = cs.UseSimulator, Margin = new Padding(Theme.S(16), 0, 0, 0) };
            c.SimSpeed.SetItems(SimSpeeds.Select(v => v + "× speed"), Math.Max(0, Array.IndexOf(SimSpeeds, cs.SimulatorSpeed)));
            c.SimSpeed.SelectedIndexChanged += (o, e) => { cs.SimulatorSpeed = SimSpeeds[c.SimSpeed.SelectedIndex]; Changed(); };
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

        /// <summary>Greys out the bed heat cards while "Use Bed Heat Control" is off.</summary>
        private void ShowBedEnabled() {
            bool on = State.BedEnabled;
            bool wasLoading = _loading;
            _loading = true;
            _useBed.Checked = on;
            _loading = wasLoading;
            _bedConn.Form.Enabled = on;
            _bedConn.SimSpeed.Enabled = on && State.Settings.Bed.UseSimulator;
            _bedLimit.Form.Enabled = on;
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
            if (value > factory && value > current) {
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
