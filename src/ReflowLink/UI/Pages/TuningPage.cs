// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Protocol;
using ReflowLink.Services;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI.Pages {
    /// <summary>
    /// Control Tuning: the controller's PID, output and alarm settings with explanations, a simulated response graph
    /// for P, I and D, and the controller's auto-tune.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class TuningPage : PageBase {
        private sealed class SettingRow {
            public TuningParameter Parameter;
            public NumberBox Box;
            public ThemedLabel Controller;
        }

        private sealed class WriteReport {
            /// <summary>Settings written and confirmed by reading them back.</summary>
            public readonly List<TuningParameter> Written = [];
            public TuningParameter Failed;
            public string Error;
            public TuningSnapshot Snapshot;
        }

        private const int ColumnWidth = 536, CardWidth = 500, TextWidth = 456;

        private readonly List<SettingRow> _rows = [];
        private readonly ThemedLabel _source, _lockNote, _tuneStatus, _tuneSetpoint, _focusText, _nowStats, _disclaimer;
        private readonly CardPanel _tuneCard;
        private readonly ModernButton _readAll, _discard, _writeChanges, _tuneButton, _restore;
        private readonly ChartView _chart;
        private readonly ChartSeries _spSeries, _nowSeries, _editSeries;
        private readonly MiniStat _statLag, _statOver, _statSettle, _statSwings;
        private readonly NumberBox _simRate, _simTarget;
        private readonly Timer _previewTimer = new() { Interval = 150 };
        private readonly Timer _tunePoll = new() { Interval = 3000 };
        private readonly ToolTip _tip = Theme.CreateToolTip();

        private TuningSnapshot _shown;
        private TuningParameter _focus;
        private TuningSnapshot _beforeTune;
        private bool _wasConnected, _tunePollBusy, _tuneStopRequested, _finishing;
        /// <summary>Counts connections, so results from an earlier connection are never shown as current.</summary>
        private int _connection, _readConnection = -1;

        private readonly ControllerChannel _ch;

        private DeviceService Dev => _ch.Device;

        /// <summary>"factory values of an ACHI IR6500" for the top IR; plain defaults for the separately bought bed PC410.</summary>
        private string DefaultsName => _ch.IsBed ? "default values" : "factory values of an ACHI IR6500";

        public TuningPage(AppState state, ControllerChannel channel)
            : base(state, $"{channel.Name} Control Tuning", $"How the {channel.Name} controller drives its heater: PID, output limits, alarms and auto-tune") {
            _ch = channel;
            // ---------------------------------------------------------------- toolbar
            _source = new ThemedLabel("", Theme.Body, Theme.TextDim) { AutoSize = true, Margin = new Padding(0, Theme.S(2), 0, 0) };
            _lockNote = new ThemedLabel("", Theme.Small, Theme.Yellow) { AutoSize = true, Margin = new Padding(0, Theme.S(4), 0, 0) };
            var sourceStack = Flow(FlowDirection.TopDown);
            sourceStack.Controls.AddRange([_source, _lockNote]);

            _readAll = new ModernButton("Read from controller", Glyph.Download) { Margin = new Padding(0, 0, Theme.S(6), 0) };
            _readAll.Click += async (s, e) => await ReadAll();
            _discard = new ModernButton("Discard changes", Glyph.Refresh) { Margin = new Padding(0, 0, Theme.S(6), 0) };
            _discard.Click += (s, e) => DiscardChanges();
            _writeChanges = new ModernButton("Write changes", Glyph.Upload, ButtonKind.Primary) { Margin = Padding.Empty };
            _writeChanges.Click += async (s, e) => await WriteChanges();
            var tip = _tip;
            tip.SetToolTip(_readAll, "Read every control setting from the controller");
            tip.SetToolTip(_discard, "Put back the values shown for the controller");
            tip.SetToolTip(_writeChanges, "Write the changed settings to the controller (each one is read back)");
            var buttons = Flow();
            buttons.Controls.AddRange([_readAll, _discard, _writeChanges]);

            var bar = Table(2, 1);
            bar.Dock = DockStyle.Fill;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sourceStack.Anchor = AnchorStyles.Left;
            buttons.Anchor = AnchorStyles.Right;
            bar.Controls.Add(sourceStack, 0, 0);
            bar.Controls.Add(buttons, 1, 0);
            var toolbar = new CardPanel { Dock = DockStyle.Fill, Padding = Theme.Pad(16, 10, 14, 10), Margin = new Padding(0, 0, 0, Theme.S(16)) };
            toolbar.Controls.Add(bar);

            // ---------------------------------------------------------------- settings column
            var column = new FlowLayoutPanel {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.Window,
                Margin = new Padding(0, 0, Theme.S(8), 0),
                Padding = Padding.Empty,
            };
            Theme.ApplyDarkScrollbars(column);

            var pid = Card("PID control", "How the heater is driven towards the setpoint");
            AddRows(pid, TuningGroup.Pid);
            column.Controls.Add(FitCard(pid));

            var output = Card("Heater output", "Power limit, switching and what happens if the thermocouple fails");
            AddRows(output, TuningGroup.Output);
            column.Controls.Add(FitCard(output));

            // Auto-tune
            _tuneCard = Card("Auto-tune", "Let the controller find P, I and D by itself");
            var tuneBody = Body(_tuneCard);
            _tuneStatus = new ThemedLabel("", Theme.BodyBold, Theme.Text) { AutoSize = true, Margin = new Padding(0, Theme.S(8), Theme.S(16), 0) };
            _tuneButton = new ModernButton("Start auto-tune", Glyph.Spark) { Margin = Padding.Empty };
            _tuneButton.Click += async (s, e) => await ToggleAutoTune();
            var tuneLine = Flow();
            tuneLine.Controls.AddRange([new ThemedLabel("Status", Theme.Body, Theme.TextDim) { AutoSize = true, Margin = new Padding(0, Theme.S(8), Theme.S(10), 0) }, _tuneStatus, _tuneButton]);
            tuneBody.Controls.Add(tuneLine);
            _restore = new ModernButton("Restore P, I, D from before auto-tune", Glyph.Refresh) { Margin = new Padding(0, Theme.S(8), 0, 0) };
            _restore.Click += async (s, e) => await RestoreBackup();
            tuneBody.Controls.Add(_restore);
            _tuneSetpoint = Para("", Theme.Small, Theme.TextDim, 6);
            tuneBody.Controls.Add(_tuneSetpoint);
            tuneBody.Controls.Add(Para(
                "The controller switches the heater fully on and off a few times around the setpoint and measures how quickly your station "
                + "heats up and cools down. From that it calculates new P, I and D and replaces the current ones.",
                Theme.Small, Theme.TextDim, 8));
            tuneBody.Controls.Add(Para(
                "⚠ The temperature swings above the setpoint while it tunes.\n"
                + "⚠ Set the station up as for a real job: board in place and the thermocouple attached as you normally use it. "
                + "Tuning with nothing on the station gives values that don't suit real boards.\n"
                + "⚠ Stay with the station until it finishes. It can take several minutes, and programs can't be started meanwhile.\n"
                + "ReflowLink keeps the P, I and D from before the tune, so you can put them back with the button above.",
                Theme.Small, Theme.Yellow, 8));
            column.Controls.Add(FitCard(_tuneCard));

            // Advanced
            var adv = Card("Advanced", "Cutback, alarms and program settings");
            Body(adv).Controls.Add(Para(
                "⚠ ACHI hides these settings on the controller's front panel, and what they mean comes from the AL808 protocol, not from ACHI. "
                + "Changing them can stop the station heating properly or switch on the alarm output. Only change one if you know why, "
                + "and note the old value first.",
                Theme.Small, Theme.Yellow, 0));
            AddRows(adv, TuningGroup.Advanced);
            column.Controls.Add(FitCard(adv));

            // ---------------------------------------------------------------- graph card
            _spSeries = new ChartSeries { Name = "Setpoint", Color = Theme.SeriesSp, Width = 1.6f, Dashed = true };
            _nowSeries = new ChartSeries { Name = "Controller now", Color = Theme.SeriesPlan, Width = 1.8f, Dashed = true };
            _editSeries = new ChartSeries { Name = "Your values", Color = Theme.Accent, Width = 2.4f };
            _chart = new ChartView { Dock = DockStyle.Fill, ShowSecondaryAxis = false, ShowLegend = true, EmptyText = "" };
            _chart.Series.AddRange([_spSeries, _nowSeries, _editSeries]);

            _simRate = new NumberBox(0.1, 5, 2, 0.1) { Suffix = "°C/s", Width = Theme.S(96), Margin = Padding.Empty, Value = 1.0 };
            _simTarget = new NumberBox(50, 400, 0, 5) { Suffix = "°C", Width = Theme.S(84), Margin = Padding.Empty, Value = 150 };
            _simRate.TextChanged += (s, e) => SchedulePreview();
            _simTarget.TextChanged += (s, e) => SchedulePreview();
            var graphHeader = new CardHeader("Response", "Simulated heat-up on a generic IR heater");
            graphHeader.Tools.Controls.Add(new ThemedLabel("Ramp", Theme.Small, Theme.TextDim) { Margin = new Padding(0, Theme.S(9), Theme.S(6), 0) });
            graphHeader.Tools.Controls.Add(_simRate);
            graphHeader.Tools.Controls.Add(new ThemedLabel("to", Theme.Small, Theme.TextDim) { Margin = new Padding(Theme.S(6), Theme.S(9), Theme.S(6), 0) });
            graphHeader.Tools.Controls.Add(_simTarget);

            _statLag = new MiniStat("Ramp lag") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(6), 0) };
            _statOver = new MiniStat("Overshoot") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(3), 0, Theme.S(3), 0) };
            _statSettle = new MiniStat("Settles in") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(3), 0, Theme.S(3), 0) };
            _statSwings = new MiniStat("Swings") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(6), 0, 0, 0) };
            var stats = Table(4, 1);
            stats.Dock = DockStyle.Bottom;
            stats.Height = Theme.S(54 + 12);
            stats.Padding = new Padding(0, Theme.S(12), 0, 0);
            for (int i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stats.Controls.Add(_statLag, 0, 0);
            stats.Controls.Add(_statOver, 1, 0);
            stats.Controls.Add(_statSettle, 2, 0);
            stats.Controls.Add(_statSwings, 3, 0);

            _focusText = new ThemedLabel("", Theme.Small, Theme.TextDim) {
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = Theme.Small.Height * 3 + Theme.S(14),
                Padding = new Padding(0, Theme.S(10), 0, 0),
            };
            _nowStats = new ThemedLabel("", Theme.Small, Theme.SeriesPlan) {
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = Theme.Small.Height * 2 + Theme.S(10),
                Padding = new Padding(0, Theme.S(8), 0, 0),
            };
            _disclaimer = new ThemedLabel(
                "Simulated on a generic IR heater model, not measured on your station. Use it to see which way a change moves the curve "
                + "and roughly how far, then check with a real run. \"Settles in\" is the time to stay within ±1 °C of the target. "
                + "Cycle time and the alarms aren't part of the simulation.",
                Theme.Small, Theme.TextFaint) {
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = Theme.Small.Height * 4 + Theme.S(10),
                Padding = new Padding(0, Theme.S(8), 0, 0),
            };

            var graph = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(8), 0, 0, 0) };
            // Bottom-docked controls stack upwards from the last one added.
            graph.Controls.Add(_chart);
            graph.Controls.Add(stats);
            graph.Controls.Add(_nowStats);
            graph.Controls.Add(_focusText);
            graph.Controls.Add(_disclaimer);
            graph.Controls.Add(graphHeader);
            _chart.BringToFront();
            graph.Resize += (s, e) => FitLabels();

            // ---------------------------------------------------------------- root
            var main = Table(2, 1);
            main.Dock = DockStyle.Fill;
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(ColumnWidth)));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.Controls.Add(column, 0, 0);
            main.Controls.Add(graph, 1, 0);

            var root = Table(1, 2);
            root.Dock = DockStyle.Fill;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(62 + 16)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(toolbar, 0, 0);
            root.Controls.Add(main, 0, 1);
            Controls.Add(root);

            // ---------------------------------------------------------------- wiring
            _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); UpdatePreview(); };
            _tunePoll.Tick += async (s, e) => await PollAutoTune();
            _ch.TuningChanged += () => ShowSnapshot(_ch.Tuning);
            State.BusyChanged += UpdateState;
            State.BedEnabledChanged += UpdateState;
            Dev.StateChanged += (s, e) => OnConnectionChanged();
            Dev.ProgramStateChanged += (a, b) => UpdateState();
            Dev.TelemetryReceived += t => UpdateSetpointText();

            ShowSnapshot(_ch.Tuning);
            SetFocus(null);
        }

        // ==================================================================== building blocks

        private static CardPanel Card(string title, string subtitle) {
            var c = new CardPanel { Width = Theme.S(CardWidth), Padding = Theme.Pad(20, 16, 20, 14), Margin = new Padding(0, 0, 0, Theme.S(16)) };
            c.Controls.Add(new CardHeader(title, subtitle));
            return c;
        }

        /// <summary>Vertical stack inside a card, below its header.</summary>
        private static FlowLayoutPanel Body(CardPanel card) {
            foreach (Control c in card.Controls)
                if (c is FlowLayoutPanel existing) return existing;
            var body = new FlowLayoutPanel {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = new Padding(0, Theme.S(4), 0, 0),
            };
            card.Controls.Add(body);
            body.BringToFront();
            return body;
        }

        private static CardPanel FitCard(CardPanel card) {
            int h = card.Padding.Vertical;
            foreach (Control c in card.Controls)
                h += c is FlowLayoutPanel body ? body.GetPreferredSize(new Size(Theme.S(TextWidth), 0)).Height : c.Height;
            card.Height = h + Theme.S(4);
            return card;
        }

        private static ThemedLabel Para(string text, Font font, Color color, int top) => new(text, font, color) {
            AutoSize = true,
            MaximumSize = new Size(Theme.S(TextWidth), 0),
            Margin = new Padding(0, Theme.S(top), 0, 0),
        };

        private void AddRows(CardPanel card, TuningGroup group) {
            var body = Body(card);
            bool first = true;
            foreach (var p in TuningCatalog.InGroup(group)) {
                if (!first || group == TuningGroup.Advanced)
                    body.Controls.Add(new Divider { Dock = DockStyle.None, Width = Theme.S(TextWidth), Margin = new Padding(0, Theme.S(14), 0, Theme.S(12)) });
                first = false;
                body.Controls.Add(BuildRow(p));
            }
        }

        private Control BuildRow(TuningParameter p) {
            var row = new SettingRow { Parameter = p };

            var name = new ThemedLabel(p.Name, Theme.BodyBold, Theme.Text) { AutoSize = true, Margin = Padding.Empty };
            string where = p.FrontPanel.Length > 0 ? $"Front panel: {p.FrontPanel}  ·  code {p.Mnemonic}" : $"Hidden on the front panel  ·  code {p.Mnemonic}";
            var sub = new ThemedLabel(where, Theme.Small, Theme.TextFaint) { AutoSize = true, Margin = new Padding(0, Theme.S(3), 0, 0) };
            var nameStack = Flow(FlowDirection.TopDown);
            nameStack.Controls.AddRange([name, sub]);
            nameStack.Margin = Padding.Empty;

            row.Box = new NumberBox(p.Min, p.Max, p.Decimals, Math.Pow(10, -p.Decimals)) {
                Suffix = p.Unit,
                Width = Theme.S(118),
                Margin = Padding.Empty,
            };
            row.Box.ValueChanged += (s, e) => { UpdateChanges(); SchedulePreview(); };
            row.Controller = new ThemedLabel("", Theme.Small, Theme.TextFaint) { AutoSize = true, Margin = new Padding(0, Theme.S(3), 0, 0) };
            var valueStack = Flow(FlowDirection.TopDown);
            valueStack.Controls.AddRange([row.Box, row.Controller]);
            valueStack.Margin = Padding.Empty;

            var head = Table(2, 1);
            head.AutoSize = true;
            head.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            head.Width = Theme.S(TextWidth);
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(TextWidth - 150)));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(150)));
            head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            head.Controls.Add(nameStack, 0, 0);
            head.Controls.Add(valueStack, 1, 0);

            var stack = Flow(FlowDirection.TopDown);
            stack.Controls.Add(head);
            stack.Controls.Add(Para(p.What, Theme.Small, Theme.TextDim, 8));
            stack.Controls.Add(Para("↑  " + p.Higher, Theme.Small, Theme.TextDim, 6));
            stack.Controls.Add(Para("↓  " + p.Lower, Theme.Small, Theme.TextDim, 3));
            if (p.Caution.Length > 0) stack.Controls.Add(Para("⚠  " + p.Caution, Theme.Small, Theme.Yellow, 6));

            if (p.Group != TuningGroup.Advanced) {
                // Hovering a setting, or editing it, points at the part of the curve it affects.
                void Hook(Control c) {
                    c.MouseEnter += (s, e) => SetFocus(p);
                    foreach (Control child in c.Controls) Hook(child);
                }
                Hook(stack);
                row.Box.Enter += (s, e) => SetFocus(p);
            }
            _rows.Add(row);
            return stack;
        }

        // ==================================================================== values

        private SettingRow RowFor(TuningParameter p) => _rows.First(r => r.Parameter == p);

        /// <summary>Shows a snapshot. Rows the user has edited keep their edited value.</summary>
        private void ShowSnapshot(TuningSnapshot snap) {
            var previous = _shown;
            _shown = snap;
            foreach (var row in _rows) {
                var p = row.Parameter;
                double? now = snap[p];
                bool edited = previous != null && IsEdited(row, previous);
                if (!edited) row.Box.Value = now;
                row.Box.Enabled = now.HasValue;
                row.Controller.Text = now.HasValue
                    ? (snap.FromController ? "Controller: " : _ch.IsBed ? "Default: " : "Factory: ") + p.Format(now.Value)
                    : "Not reported";
            }
            UpdateChanges();
            UpdatePreview();
        }

        /// <summary>
        /// True when the box differs from the snapshot. A box showing the snapshot's own value is never "edited", even if
        /// that value is outside the range ReflowLink allows (for example 0 meaning "off").
        /// </summary>
        private static bool IsEdited(SettingRow row, TuningSnapshot snap) {
            double? now = snap[row.Parameter];
            if (!now.HasValue) return false;
            if (row.Box.Value is not double v) return true;
            return !row.Parameter.Same(row.Parameter.Round(v), now.Value);
        }

        /// <summary>Edited rows with a valid value, rounded to the resolution they're written with.</summary>
        private List<(TuningParameter Parameter, double Old, double New)> Changes() =>
            [.. _rows.Where(r => r.Box.IsValid && IsEdited(r, _shown)).Select(r => (r.Parameter, _shown[r.Parameter].Value, r.Parameter.Round(r.Box.Value.Value)))];

        private void UpdateChanges() {
            if (_shown == null) return;
            foreach (var row in _rows) row.Controller.ForeColor = IsEdited(row, _shown) ? Theme.Accent : Theme.TextFaint;
            int n = Changes().Count;
            _writeChanges.Text = n > 0 ? $"Write changes ({n})" : "Write changes";
            UpdateState();
        }

        private void DiscardChanges() {
            foreach (var row in _rows) row.Box.Value = _shown[row.Parameter];
            UpdateChanges();
            UpdatePreview();
        }

        private bool ProgramActive => Dev.IsConnected && Dev.Last?.State is ProgramState.Running or ProgramState.Hold;

        private bool IsTuning => Dev.IsConnected && _shown?.AutoTune == true;

        /// <summary>True when the values shown were read from the controller on the current connection.</summary>
        private bool ReadThisConnection => Dev.IsConnected && _shown != null && _shown.FromController && _readConnection == _connection;

        private void UpdateState() {
            if (_shown == null) return;
            bool conn = Dev.IsConnected, busy = State.Busy;
            bool locked = ProgramActive || IsTuning;
            bool fresh = ReadThisConnection;
            _ch.AutoTuneRunning = IsTuning;

            _readAll.Enabled = conn && !busy && !ProgramActive;
            _writeChanges.Enabled = conn && !busy && fresh && !locked && Changes().Count > 0;
            _discard.Enabled = _rows.Any(r => IsEdited(r, _shown));

            string when = _shown.ReadAt?.ToString("HH:mm:ss") ?? "";
            int missing = _shown.Values.Count(v => v.Value == null);
            _source.Text = !_shown.FromController
                    ? conn ? $"Connected  ·  {DefaultsName} shown until the controller is read" : $"Not connected  ·  {DefaultsName}"
                : fresh ? $"Read from the controller at {when}" + (missing > 0 ? $"  ·  {missing} not reported" : "")
                : conn ? $"Showing values read at {when} on an earlier connection"
                : $"Disconnected  ·  values read at {when}";
            _lockNote.Text = !conn && _ch.IsBed && !State.DualBed ? "This page is for a second PC410 on the bed (Settings → Bed heat). You can still try changes in the graph."
                : !conn ? $"Try changes in the graph. Connect the {_ch.Name} controller to read and write it."
                : !fresh ? "Read the controller before writing (Read from controller)."
                : ProgramActive ? "A program is running: settings can't be changed until it stops."
                : IsTuning ? "Auto-tune is running: settings can't be changed until it finishes."
                : "";

            // Auto-tune
            if (!conn) {
                _tuneStatus.Text = "Not connected";
                _tuneStatus.ForeColor = Theme.TextDim;
            } else if (!fresh) {
                _tuneStatus.Text = "Unknown until the controller is read";
                _tuneStatus.ForeColor = Theme.TextDim;
            } else if (_shown.AutoTune == null) {
                _tuneStatus.Text = "Not reported by this controller";
                _tuneStatus.ForeColor = Theme.TextDim;
            } else if (_shown.AutoTune == true) {
                _tuneStatus.Text = "Tuning…";
                _tuneStatus.ForeColor = Theme.Yellow;
            } else {
                _tuneStatus.Text = "Off";
                _tuneStatus.ForeColor = Theme.Text;
            }
            _tuneButton.Text = IsTuning ? "Stop auto-tune" : "Start auto-tune";
            _tuneButton.Glyph = IsTuning ? Glyph.Stop : Glyph.Spark;
            _tuneButton.Kind = IsTuning ? ButtonKind.Danger : ButtonKind.Secondary;
            _tuneButton.Enabled = conn && !busy && fresh && !_finishing && _shown.AutoTune != null && (IsTuning || !ProgramActive);
            var backup = Backup;
            _restore.Enabled = backup != null && conn && !busy && fresh && !locked && !_finishing;
            _tip.SetToolTip(_restore, backup == null ? "Nothing saved yet: ReflowLink saves P, I and D when it starts an auto-tune." : BackupText(backup));
            UpdateSetpointText();
        }

        private void UpdateSetpointText() {
            string text = Dev.IsConnected && Dev.Last?.Sp is double sp
                ? $"Controller setpoint now: {sp:0.#} °C. Auto-tune works around this temperature, so a setpoint near room temperature won't give useful values."
                : "Auto-tune works around the controller's current setpoint.";
            if (_tuneSetpoint.Text == text) return;
            _tuneSetpoint.Text = text;
            FitCard(_tuneCard);
        }

        // ==================================================================== connection and reading

        private void OnConnectionChanged() {
            bool conn = Dev.IsConnected;
            if (conn && !_wasConnected) {
                _wasConnected = true;
                _connection++;
                _ = ReadOnConnect(_connection);
            } else if (!conn && _wasConnected) {
                _wasConnected = false;
                _tunePoll.Stop();
            }
            UpdateState();
        }

        /// <summary>Shows values read on the current connection.</summary>
        private void ApplyRead(TuningSnapshot snap, bool replaceEdits) {
            _readConnection = _connection;
            if (replaceEdits) _shown = null;
            _ch.SetTuning(snap);
            if (snap.AutoTune == true && !_tunePoll.Enabled && !_finishing) StartTunePolling();
        }

        /// <summary>Reads every setting right after connecting, without dialogs (a station may not report them all).</summary>
        private async Task ReadOnConnect(int connection) {
            if (!Dev.IsConnected) return;
            State.SetStatus($"Reading {_ch.Name} control settings…");
            try {
                var snap = await Dev.RunAsync(d => d.ReadTuning(null, System.Threading.CancellationToken.None));
                if (connection != _connection || !Dev.IsConnected) return;
                ApplyRead(snap, replaceEdits: false);
                State.SetStatus($"{_ch.Name} control settings read", StatusKind.Success);
            } catch (Exception ex) {
                if (connection != _connection || !Dev.IsConnected) return;
                State.SetStatus($"Couldn't read the {_ch.Name} control settings: " + ex.Message, StatusKind.Warning);
            }
        }

        private async Task ReadAll() {
            if (_rows.Any(r => IsEdited(r, _shown)) &&
                !DarkDialog.Confirm(FindForm(), "Discard your changes?", "Reading the controller replaces the values you've changed but not written.", "Read", "Cancel"))
                return;
            int connection = _connection;
            var (ok, snap) = await State.RunOnAsync(_ch, $"Reading {_ch.Name} control settings", (d, progress, ct) => d.ReadTuning(progress, ct));
            if (!ok || connection != _connection) return;
            ApplyRead(snap, replaceEdits: true);
            State.SetStatus($"{_ch.Name} control settings read", StatusKind.Success);
        }

        // ==================================================================== writing

        private async Task WriteChanges() {
            var invalid = _rows.Where(r => r.Box.Enabled && IsEdited(r, _shown) && !r.Box.IsValid).Select(r => r.Parameter.RangeText).ToList();
            if (invalid.Count > 0) {
                DarkDialog.Error(FindForm(), "Check the values", string.Join("\n", invalid));
                return;
            }
            var changes = Changes();
            if (changes.Count == 0) return;

            var msg = new StringBuilder();
            foreach (var (p, old, value) in changes) msg.AppendLine($"• {p.Name}: {p.Format(old)} → {p.Format(value)}");
            msg.AppendLine();
            msg.Append("The controller uses the new values straight away, and keeps them after it's switched off.");
            bool risky = false;
            if (changes.Any(c => c.Parameter.Group == TuningGroup.Advanced)) {
                risky = true;
                msg.Append("\n\nSome of these are advanced settings that ACHI hides on the front panel. Make sure you know what they do.");
            }
            if (changes.Any(c => c.Parameter == TuningCatalog.SensorBreakPower && c.New > 0)) {
                risky = true;
                msg.Append("\n\nSensor break power above 0 % keeps the heater on if the thermocouple fails.");
            }
            if (changes.Any(c => c.Parameter == TuningCatalog.CycleTime && c.New < c.Old)) {
                risky = true;
                msg.Append("\n\nA shorter cycle time switches the heater more often. Only do this if it's switched by a solid-state relay.");
            }
            if (!DarkDialog.Confirm(FindForm(), changes.Count == 1 ? "Write 1 change?" : $"Write {changes.Count} changes?", msg.ToString(), "Write", "Cancel", risky))
                return;

            int connection = _connection;
            var (ok, report) = await State.RunOnAsync(_ch, $"Writing {_ch.Name} control settings", (d, progress, ct) => {
                var rep = new WriteReport();
                for (int i = 0; i < changes.Count; i++) {
                    var (p, _, value) = changes[i];
                    progress.Report(new OperationProgress(i, changes.Count + 1, $"Writing {p.Name}…"));
                    try {
                        d.WriteSetting(p, value);
                        rep.Written.Add(p);
                    } catch (Exception ex) {
                        rep.Failed = p;
                        rep.Error = ex is Pc410TimeoutException ? DeviceService.Describe(ex) : ex.Message;
                        break;
                    }
                }
                // Read everything back, so the report shows what the controller really has now.
                try { rep.Snapshot = d.ReadTuning(progress, ct); } catch (Exception) { }
                return rep;
            });
            if (!ok) return;
            if (report.Snapshot != null && connection == _connection) ApplyRead(report.Snapshot, replaceEdits: false);

            if (report.Error == null) {
                State.SetStatus(report.Written.Count == 1 ? $"{report.Written[0].Name} written" : $"{report.Written.Count} control settings written", StatusKind.Success);
                return;
            }
            var lines = new StringBuilder();
            if (report.Snapshot != null) {
                lines.AppendLine("What the controller has now:");
                foreach (var (p, old, value) in changes) {
                    double? now = report.Snapshot[p];
                    string state = now is not double v ? "couldn't be read"
                        : p.Same(v, value) ? "changed"
                        : p.Same(v, old) ? "not changed"
                        : "changed to a different value";
                    lines.AppendLine($"• {p.Name}: {(now is double n ? p.Format(n) : "?")} ({state})");
                }
            } else {
                lines.AppendLine("The settings couldn't be read back, so it isn't known whether "
                    + $"{report.Failed?.Name ?? "the last setting"} changed. Read the controller again before continuing.");
                if (report.Written.Count > 0) lines.AppendLine("Written and confirmed before that: " + string.Join(", ", report.Written.Select(w => w.Name)) + ".");
            }
            DarkDialog.Error(FindForm(), "Not all settings were written", $"{report.Error}\n\n{lines.ToString().TrimEnd()}");
        }

        // ==================================================================== auto-tune

        private async Task ToggleAutoTune() {
            if (IsTuning) {
                var (stopped, _) = await State.RunOnAsync(_ch, $"Stopping {_ch.Name} auto-tune", d => d.SetAutoTune(false));
                if (stopped) {
                    _tuneStopRequested = true;
                    await FinishAutoTune();
                }
                return;
            }
            double? setpoint = Dev.Last?.Sp;
            string sp = setpoint is double v ? $"{v:0.#} °C" : "unknown";
            string message =
                "The controller will drive the heater fully on and off around the current setpoint (" + sp + ") and then replace P, I and D "
                + "with the values it calculates.\n\n"
                + "• The temperature will swing above the setpoint.\n"
                + "• The station must be set up as for a real job, with the board in place and the thermocouple attached.\n"
                + "• Stay with the station until it finishes. You can stop it here at any time, but not after disconnecting.\n\n"
                + $"Current values: P {Value(TuningCatalog.Proportional)}, I {Value(TuningCatalog.Integral)}, D {Value(TuningCatalog.Derivative)}. Note them down first.";
            if (setpoint is double low && low < 80)
                message += $"\n\n⚠ The setpoint is only {low:0.#} °C. Tuning this close to room temperature gives values that don't suit soldering temperatures.";
            if (!DarkDialog.Confirm(FindForm(), $"Start {_ch.Name} auto-tune?", message, "Start auto-tune", "Cancel", danger: true)) return;

            var before = _shown;
            var (ok, _) = await State.RunOnAsync(_ch, $"Starting {_ch.Name} auto-tune", d => d.SetAutoTune(true));
            if (!ok) {
                // The start may have reached the controller even though the reply was lost: check before giving up.
                bool? running = null;
                try { running = await Dev.RunAsync(d => d.ReadAutoTune()); } catch (Exception) { }
                if (running != true) {
                    if (running == null)
                        State.SetStatus("Couldn't confirm whether auto-tune started. Check the controller's display.", StatusKind.Warning);
                    return;
                }
            }
            _beforeTune = before;
            SaveBackup(before);
            _shown.AutoTune = true;
            UpdateState();
            StartTunePolling();
            State.SetStatus("Auto-tune started: stay with the station until it finishes", StatusKind.Warning);
        }

        private string Value(TuningParameter p) => _shown[p] is double v ? p.Format(v) : "not reported";

        /// <summary>P, I and D from before this controller's last auto-tune (kept in the settings).</summary>
        private TuningBackup Backup {
            get => _ch.IsBed ? State.Settings.BedTuningBackup : State.Settings.IrTuningBackup;
            set {
                if (_ch.IsBed) State.Settings.BedTuningBackup = value;
                else State.Settings.IrTuningBackup = value;
                State.Settings.Save();
            }
        }

        /// <summary>Keeps the values an auto-tune is about to replace (only when all three were read).</summary>
        private void SaveBackup(TuningSnapshot before) {
            if (before?[TuningCatalog.Proportional] is not double p || before[TuningCatalog.Integral] is not double i
                || before[TuningCatalog.Derivative] is not double d || !before.FromController)
                return;
            Backup = new TuningBackup { P = p, I = i, D = d, Saved = DateTime.Now };
            UpdateState();
        }

        private string BackupText(TuningBackup b) =>
            $"P {TuningCatalog.Proportional.Format(b.P)}, I {TuningCatalog.Integral.Format(b.I)}, D {TuningCatalog.Derivative.Format(b.D)} (saved {b.Saved:yyyy-MM-dd HH:mm})";

        /// <summary>Writes back the P, I and D saved before the last auto-tune, and reads the controller again.</summary>
        private async Task RestoreBackup() {
            var b = Backup;
            if (b == null) return;
            if (!DarkDialog.Confirm(FindForm(), "Put the old values back?",
                    $"The {_ch.Name} controller gets the values from before its last auto-tune:\n\n{BackupText(b)}\n\n"
                    + "Now it has: " + $"P {Value(TuningCatalog.Proportional)}, I {Value(TuningCatalog.Integral)}, D {Value(TuningCatalog.Derivative)}.",
                    "Restore", "Cancel"))
                return;
            int connection = _connection;
            var (ok, snap) = await State.RunOnAsync(_ch, $"Restoring {_ch.Name} P, I and D", (d, progress, ct) => {
                d.WriteSetting(TuningCatalog.Proportional, b.P);
                d.WriteSetting(TuningCatalog.Integral, b.I);
                d.WriteSetting(TuningCatalog.Derivative, b.D);
                return d.ReadTuning(progress, ct);
            });
            if (!ok && connection == _connection && Dev.IsConnected) {
                // Some of the three may have been written: show what the controller has now.
                var (read, now) = await State.RunOnAsync(_ch, $"Reading {_ch.Name} control settings", (d, progress, ct) => d.ReadTuning(progress, ct));
                if (read && connection == _connection) ApplyRead(now, replaceEdits: true);
                return;
            }
            if (!ok || connection != _connection) return;
            ApplyRead(snap, replaceEdits: true);
            State.SetStatus($"{_ch.Name} P, I and D restored", StatusKind.Success);
        }

        private void StartTunePolling() {
            _beforeTune ??= _shown;
            _tunePoll.Start();
        }

        private async Task PollAutoTune() {
            if (_tunePollBusy || _finishing) return;
            if (!Dev.IsConnected) { _tunePoll.Stop(); return; }
            if (State.Busy) return;
            _tunePollBusy = true;
            try {
                bool? on = await Dev.RunAsync(d => d.ReadAutoTune());
                // The tune may have been stopped from the button while this read was on its way.
                if (on == false && _tunePoll.Enabled) await FinishAutoTune();
            } catch (Exception) {
                // A missed poll is harmless; try again on the next tick.
            } finally {
                _tunePollBusy = false;
            }
        }

        /// <summary>Auto-tune has ended: read the new values and show what changed.</summary>
        private async Task FinishAutoTune() {
            if (_finishing) return;
            _finishing = true;
            _tunePoll.Stop();
            try {
                var before = _beforeTune ?? _shown;
                int connection = _connection;
                var (ok, snap) = await State.RunOnAsync(_ch, $"Reading the new {_ch.Name} control settings", (d, progress, ct) => d.ReadTuning(progress, ct));
                if (connection != _connection) return;
                if (!ok) {
                    // Couldn't read (busy or a glitch): keep watching and try again on the next poll.
                    if (Dev.IsConnected) _tunePoll.Start();
                    return;
                }
                _beforeTune = null;
                ApplyRead(snap, replaceEdits: true);

                var pid = new[] { TuningCatalog.Proportional, TuningCatalog.Integral, TuningCatalog.Derivative };
                bool changed = pid.Any(p => before[p] is double a && snap[p] is double b && !p.Same(a, b));
                string lines = string.Join("\n", pid.Select(p =>
                    $"• {p.Name}: {(before[p] is double a ? p.Format(a) : "?")} → {(snap[p] is double b ? p.Format(b) : "?")}"));
                string title = _tuneStopRequested ? "Auto-tune stopped" : changed ? "Auto-tune finished" : "Auto-tune ended";
                string body = changed
                    ? $"The controller is now using new values:\n\n{lines}\n\nCheck them with a real run. You can put the old values back here if the result is worse."
                    : $"P, I and D didn't change:\n\n{lines}\n\n" + (_tuneStopRequested ? "It was stopped before it finished." : "The controller may have stopped the tune before it finished.");
                _tuneStopRequested = false;
                DarkDialog.Show(FindForm(), title, body, DialogTone.Info, ["OK"]);
            } finally {
                _finishing = false;
                UpdateState();
            }
        }

        // ==================================================================== graph

        private void SchedulePreview() {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        /// <summary>The settings the simulation uses.</summary>
        private static readonly TuningParameter[] Simulated = [TuningCatalog.Proportional, TuningCatalog.Integral, TuningCatalog.Derivative, TuningCatalog.OutputLimit];

        /// <summary>Value for the simulation; settings that are 0 ("off") or not reported fall back sensibly.</summary>
        private double Current(TuningParameter p) => _shown?[p] ?? p.Factory;

        private double Edited(TuningParameter p) {
            var box = RowFor(p).Box;
            return box.Enabled && box.IsValid ? p.Round(box.Value.Value) : Current(p);
        }

        private void UpdatePreview() {
            if (_shown == null) return;
            double rate = _simRate.IsValid ? _simRate.Value.Value : 1;
            double target = _simTarget.IsValid ? _simTarget.Value.Value : 150;
            var now = PidSimulator.Run(Current(TuningCatalog.Proportional), Current(TuningCatalog.Integral), Current(TuningCatalog.Derivative),
                Current(TuningCatalog.OutputLimit), rate, target);
            var mine = PidSimulator.Run(Edited(TuningCatalog.Proportional), Edited(TuningCatalog.Integral), Edited(TuningCatalog.Derivative),
                Edited(TuningCatalog.OutputLimit), rate, target);
            bool differs = Simulated.Any(p => !p.Same(Current(p), Edited(p)));

            Fill(_spSeries, now, pt => pt.Setpoint);
            Fill(_nowSeries, now, pt => pt.Temperature);
            Fill(_editSeries, mine, pt => pt.Temperature);
            _nowSeries.Visible = _nowSeries.ShowInLegend = differs;
            _editSeries.Name = differs ? "Your values" : "Temperature";
            _chart.XMin = 0;
            _chart.XMax = now.Duration;
            _chart.MinXSpan = 60;
            UpdateBand(mine);
            _chart.Invalidate();

            _statLag.Value = $"{mine.RampLag:0.0} °C";
            _statOver.Value = Overshoot(mine);
            _statSettle.Value = Settle(mine);
            _statSwings.Value = mine.Swings == 0 ? "None" : mine.Swings.ToString();
            _nowStats.Text = differs
                ? $"With the controller's current values: ramp lag {now.RampLag:0.0} °C, overshoot {(now.Overshoot < 0.05 ? "none" : $"{now.Overshoot:0.0} °C")}, "
                  + $"settles in {(now.SettleSeconds is double t ? ChartView.FormatTime(t) : "never")}, swings {(now.Swings == 0 ? "none" : now.Swings.ToString())}."
                : "The tiles show the controller's current values. Change a setting to compare.";
            FitLabels();
        }

        private static string Overshoot(PidSimulation s) => s.Overshoot < 0.05 ? "None" : $"{s.Overshoot:0.0} °C";

        /// <summary>"Never" means it doesn't stay within ±1 °C of the target during the simulated hold.</summary>
        private static string Settle(PidSimulation s) => s.SettleSeconds is double t ? ChartView.FormatTime(t) : "Never";

        private static void Fill(ChartSeries series, PidSimulation sim, Func<SimPoint, double> value) {
            series.Points.Clear();
            foreach (var pt in sim.Points) series.Points.Add(new PointD(pt.Time, value(pt)));
        }

        /// <summary>Sizes the text under the graph to its content, so nothing is cut off on a narrow window.</summary>
        private void FitLabels() {
            foreach (var label in new[] { _nowStats, _focusText, _disclaimer }) {
                int width = Math.Max(Theme.S(120), label.Width - label.Padding.Horizontal);
                var size = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                int height = size.Height + label.Padding.Vertical + Theme.S(2);
                if (label.Height != height) label.Height = height;
            }
        }

        private void SetFocus(TuningParameter p) {
            if (_focus == p && p != null) return;
            _focus = p;
            _focusText.Text = p == null
                ? "Change P, I, D or the output limit on the left: the orange curve shows your values and the grey one the controller's current values. "
                  + "Hover a setting to see which part of the curve it affects."
                : p == TuningCatalog.Proportional
                    ? "P: watch the ramp. A smaller P keeps the temperature closer to the setpoint on the way up but makes it swing around the target; "
                      + "a larger P lets it trail further behind."
                : p == TuningCatalog.Integral
                    ? "I: watch the hold. A shorter I pulls the temperature onto the target sooner but can overshoot and swing; "
                      + "a longer I leaves it below the target for longer."
                : p == TuningCatalog.Derivative
                    ? "D: watch the end of the ramp. More D brakes harder there and trims the overshoot; too much makes the response slow and restless."
                : p == TuningCatalog.OutputLimit
                    ? "Output limit: watch the ramp. If the limit is too low, the heater can't keep up and the temperature falls behind the setpoint."
                : $"{p.Name} isn't part of the simulation.";
            if (_shown != null) UpdatePreview();
        }

        private void UpdateBand(PidSimulation sim) {
            _chart.Bands.Clear();
            if (_focus == null) return;
            double end = sim.RampEnd, total = sim.Duration;
            ChartBand band = _focus == TuningCatalog.Proportional || _focus == TuningCatalog.OutputLimit
                ? new ChartBand { XFrom = 0, XTo = end, Label = "Ramp" }
                : _focus == TuningCatalog.Integral
                    ? new ChartBand { XFrom = end, XTo = total, Label = "Hold" }
                : _focus == TuningCatalog.Derivative
                    ? new ChartBand { XFrom = Math.Max(0, end - Math.Min(20, end / 4)), XTo = Math.Min(total, end + 60), Label = "End of ramp" }
                : null;
            if (band != null) _chart.Bands.Add(band);
        }

        public override void OnShown() {
            UpdateState();
            UpdatePreview();
        }
    }
}
