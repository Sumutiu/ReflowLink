// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Protocol;
using ReflowLink.Services;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI.Pages {
    /// <summary>Key/value line used in side panels.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class InfoRow : ThemedControl {
        private string _value = "—";
        private Color _valueColor = Theme.Text;

        public InfoRow(string label) {
            Text = label;
            Height = Theme.S(28);
        }

        public string Value { get => _value; set { if (_value == value) return; _value = value; Invalidate(); } }
        public Color ValueColor { get => _valueColor; set { _valueColor = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e) {
            TextRenderer.DrawText(e.Graphics, Text, Theme.Body, new Rectangle(0, 0, Width / 2, Height), Theme.TextDim, Theme.LeftMiddle);
            TextRenderer.DrawText(e.Graphics, _value, Theme.BodyBold, new Rectangle(Width / 3, 0, Width - Width / 3, Height), _valueColor, Theme.RightMiddle);
        }
    }

    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class DashboardPage : PageBase {
        private static readonly Color BedPvColor = Theme.Green;
        private static readonly Color BedSpColor = Color.FromArgb(45, 212, 191);
        private static readonly Color BedOpColor = Color.FromArgb(120, 200, 140);

        private readonly StatCard _pvCard, _spCard, _opCard, _stateCard;
        private readonly ChartView _chart;
        private readonly ChartSeries _pv, _sp, _op, _plan, _bedPv, _bedSp, _bedOp, _bedPlan;
        private readonly ModernDropDown _window, _slot;
        private readonly ModernButton _run, _hold, _stop, _openLogs;
        private readonly ToggleSwitch _record;
        private readonly InfoRow _rowState, _rowStep, _rowIr, _rowBed, _rowElapsed, _rowPlanned, _rowRemaining;
        private readonly ThemedLabel _recordInfo;
        private readonly CardHeader _chartHeader;

        private DateTime _sessionStart = DateTime.Now;
        private double? _runStart;
        private PlannedCurve _runPlan, _runBedPlan;
        private int? _runPattern;
        private ProgramState _state = ProgramState.Unknown, _bedState = ProgramState.Unknown;
        private CsvRecorder _recorder;
        private bool _autoRecording, _settingToggle, _userStopped, _wasActive;
        private DateTime? _autoStopAt;
        private double _lastX;

        private static readonly string[] WindowNames = ["Current run", "Last 2 min", "Last 5 min", "Last 15 min", "Whole session"];
        private static readonly double[] WindowSeconds = [0, 120, 300, 900, 0];

        public DashboardPage(AppState state) : base(state, "Dashboard", "Live readings and program control") {
            // ---------------------------------------------------------------- stat cards
            _pvCard = new StatCard("Temperature", "°C", Theme.SeriesPv) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(8), 0), Label1 = "Top IR", Label2 = "Bed heat", Accent2 = BedPvColor };
            _spCard = new StatCard("Setpoint", "°C", Theme.SeriesSp) { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4), 0, Theme.S(4), 0), Label1 = "Top IR", Label2 = "Bed heat", Accent2 = BedSpColor };
            _opCard = new StatCard("Heater output", "%", Theme.SeriesOp) { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4), 0, Theme.S(4), 0), Label1 = "Top IR", Label2 = "Bed heat", Accent2 = BedOpColor };
            _stateCard = new StatCard("Program", "", Theme.TextFaint) { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(8), 0, 0, 0) };

            var stats = Table(4, 1);
            stats.Dock = DockStyle.Fill;
            for (int i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stats.Controls.Add(_pvCard, 0, 0);
            stats.Controls.Add(_spCard, 1, 0);
            stats.Controls.Add(_opCard, 2, 0);
            stats.Controls.Add(_stateCard, 3, 0);

            // ---------------------------------------------------------------- chart card
            _pv = new ChartSeries { Name = "Temperature (PV)", Color = Theme.SeriesPv, Width = 2.2f };
            _sp = new ChartSeries { Name = "Setpoint (SP)", Color = Theme.SeriesSp, Width = 1.6f };
            _op = new ChartSeries { Name = "Output", Color = Theme.SeriesOp, Width = 1.2f, FillArea = true, Secondary = true, Unit = "%", Format = "0" };
            _plan = new ChartSeries { Name = "Planned", Color = Theme.SeriesPlan, Width = 1.4f, Dashed = true };
            _bedPv = new ChartSeries { Name = "Bed temp", Color = BedPvColor, Width = 2.0f };
            _bedSp = new ChartSeries { Name = "Bed setpoint", Color = BedSpColor, Width = 1.4f };
            _bedOp = new ChartSeries { Name = "Bed output", Color = BedOpColor, Width = 1.1f, Secondary = true, Unit = "%", Format = "0" };
            _bedPlan = new ChartSeries { Name = "Planned bed", Color = Theme.WithAlpha(BedPvColor, 150), Width = 1.2f, Dashed = true, ShowInLegend = false };
            _chart = new ChartView { Dock = DockStyle.Fill, EmptyText = "Connect to the controller to see live data" };
            // Drawn first to last; the legend lists them last to first (IR before bed).
            _chart.Series.AddRange([_bedOp, _op, _bedPlan, _plan, _bedSp, _sp, _bedPv, _pv]);

            _window = new ModernDropDown { Width = Theme.S(150), Margin = new Padding(0, 0, Theme.S(8), 0) };
            _window.SetItems(WindowNames, 2);
            _window.SelectedIndexChanged += (s, e) => UpdateWindow();
            var export = new ModernButton("", Glyph.Image) { Margin = new Padding(0, 0, Theme.S(8), 0) };
            export.Click += (s, e) => ExportPng();
            var clear = new ModernButton("", Glyph.Trash) { Margin = Padding.Empty };
            clear.Click += (s, e) => ClearChart();
            var tip = Theme.CreateToolTip();
            tip.SetToolTip(export, "Export chart as PNG");
            tip.SetToolTip(clear, "Clear chart");

            _chartHeader = new CardHeader("Live temperature", "Board temperature, setpoint and heater output");
            _chartHeader.Tools.Controls.AddRange([_window, export, clear]);
            var chartCard = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(8), 0) };
            chartCard.Controls.Add(_chart);
            chartCard.Controls.Add(_chartHeader);
            _chart.BringToFront();

            // ---------------------------------------------------------------- control card
            var side = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(8), 0, 0, 0), AutoScroll = true };
            Theme.ApplyDarkScrollbars(side);
            var stack = Table(1, 0);
            stack.Dock = DockStyle.Top;
            stack.AutoSize = true;
            stack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            stack.AutoScroll = false;

            void Add(Control c, int top = 0) {
                c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
                c.Margin = new Padding(0, Theme.S(top), 0, 0);
                stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                stack.Controls.Add(c, 0, stack.RowCount++);
            }

            Add(new CardHeader("Program control"));
            Add(new ThemedLabel("Controller pattern", Theme.Small, Theme.TextDim), 2);
            _slot = new ModernDropDown();
            _slot.SetItems(State.SlotLabels(), State.Settings.LastSlot);
            _slot.SelectedIndexChanged += (s, e) => { State.Settings.LastSlot = _slot.SelectedIndex; ShowPlannedPreview(); };
            Add(_slot, 6);

            _run = new ModernButton("Start program", Glyph.Play, ButtonKind.Success) { AutoWidth = false, Height = Theme.S(42) };
            _run.Click += async (s, e) => await StartProgram();
            Add(_run, 14);

            var row = Table(2, 1);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(38)));
            row.Height = Theme.S(38);
            _hold = new ModernButton("Hold", Glyph.Pause) { AutoWidth = false, Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(4), 0) };
            _hold.Click += async (s, e) => await HoldOrResume();
            _stop = new ModernButton("Stop", Glyph.Stop, ButtonKind.Danger) { AutoWidth = false, Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4), 0, 0, 0) };
            _stop.Click += async (s, e) => await StopProgram();
            row.Controls.Add(_hold, 0, 0);
            row.Controls.Add(_stop, 1, 0);
            Add(row, 8);

            Add(new Divider(), 12);
            _rowState = new InfoRow("State");
            _rowStep = new InfoRow("Segment");
            _rowIr = new InfoRow("Top IR");
            _rowBed = new InfoRow("Bed heat");
            _rowElapsed = new InfoRow("Elapsed");
            _rowPlanned = new InfoRow("Planned duration");
            _rowRemaining = new InfoRow("Remaining (est.)");
            Add(_rowState);
            Add(_rowStep);
            Add(_rowIr);
            Add(_rowBed);
            Add(_rowElapsed);
            Add(_rowPlanned);
            Add(_rowRemaining);

            Add(new Divider(), 8);
            _record = new ToggleSwitch("Record to CSV");
            _record.CheckedChanged += (s, e) => { if (!_settingToggle) { if (_record.Checked) StartRecording(false); else StopRecording(); } };
            Add(_record, 2);
            _recordInfo = new ThemedLabel(State.Settings.AutoRecordRuns ? "Runs are recorded automatically." : "Recording is off.", Theme.Small, Theme.TextDim) {
                AutoSize = false,
                Height = Theme.S(36),
                AutoEllipsis = true,
            };
            Add(_recordInfo, 4);
            _openLogs = new ModernButton("Open logs folder", Glyph.Folder, ButtonKind.Ghost);
            _openLogs.Click += (s, e) => OpenFolder(State.Settings.EffectiveLogFolder);
            _openLogs.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(_openLogs, 0, stack.RowCount++);
            _openLogs.Margin = new Padding(-Theme.S(8), Theme.S(4), 0, 0);

            side.Controls.Add(stack);

            // ---------------------------------------------------------------- root layout
            var main = Table(2, 1);
            main.Dock = DockStyle.Fill;
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(310)));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.Controls.Add(chartCard, 0, 0);
            main.Controls.Add(side, 1, 0);

            var root = Table(1, 2);
            root.Dock = DockStyle.Fill;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(118 + 16)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stats.Margin = new Padding(0, 0, 0, Theme.S(16));
            root.Controls.Add(stats, 0, 0);
            root.Controls.Add(main, 0, 1);
            Controls.Add(root);

            // ---------------------------------------------------------------- events
            State.Device.TelemetryReceived += OnTelemetry;
            State.Device.StateChanged += (s, e) => OnLinkChanged();
            State.Device.ProgramStateChanged += OnProgramStateChanged;
            State.Bed.TelemetryReceived += OnBedTelemetry;
            State.Bed.StateChanged += (s, e) => OnBedLinkChanged();
            State.Bed.ProgramStateChanged += (prev, now) => { _bedState = now; OnCombinedStateChanged(); UpdateProgramInfo(State.Device.Last); UpdateButtons(); };
            State.BedEnabledChanged += ApplyBedMode;
            State.Sync.Changed += () => { UpdateProgramInfo(State.Device.Last); UpdateButtons(); };
            State.Sync.CommandFailed += msg => State.SetStatus(msg, StatusKind.Error);
            State.Sync.BedLost += async () => await OnBedLost();
            State.Sync.IrLost += () => {
                State.SetStatus("Top IR controller stopped answering", StatusKind.Error);
                DarkDialog.Error(FindForm(), "Top IR controller not answering",
                    "The Top IR controller stopped answering during the run. It keeps running its program on its own, and the bed carries on with its program too. "
                    + "Check the cable, or stop both programs on the station's front panels.");
            };
            State.SlotProfilesChanged += () => { _slot.SetItems(State.SlotLabels(), _slot.SelectedIndex); ShowPlannedPreview(); };
            State.BusyChanged += UpdateButtons;
            ApplyBedMode();
            OnLinkChanged();
            ShowPlannedPreview();
        }

        private bool Bed => State.BedEnabled;

        /// <summary>Shows or hides everything about the bed heat controller.</summary>
        private void ApplyBedMode() {
            bool bed = Bed;
            _pvCard.Split = _spCard.Split = _opCard.Split = bed;
            foreach (var s in new[] { _bedPv, _bedSp, _bedOp, _bedPlan }) s.Visible = bed;
            _bedPv.ShowInLegend = _bedSp.ShowInLegend = _bedOp.ShowInLegend = bed;
            _pv.Name = bed ? "IR temp" : "Temperature (PV)";
            _sp.Name = bed ? "IR setpoint" : "Setpoint (SP)";
            _op.Name = bed ? "IR output" : "Output";
            _plan.Name = bed ? "Planned IR" : "Planned";
            _chartHeader.Subtitle = bed ? "Top IR and bed heat: temperature, setpoint and heater output" : "Board temperature, setpoint and heater output";
            _rowStep.Visible = !bed;
            _rowIr.Visible = _rowBed.Visible = bed;
            if (!bed) _bedState = ProgramState.Unknown;
            OnBedLinkChanged();
            UpdateProgramInfo(State.Device.Last);
            UpdateButtons();
            _chart.Invalidate();
        }

        // ==================================================================== link / telemetry

        private LinkStatus _lastLink = LinkStatus.Disconnected;

        private void OnLinkChanged() {
            var ls = State.Device.State;
            if (ls == LinkStatus.Connected && (_lastLink == LinkStatus.Disconnected || _lastLink == LinkStatus.Connecting)) {
                ClearChart();
                if (State.Device.ActivePattern.HasValue) _slot.SelectedIndex = State.Device.ActivePattern.Value;
            }
            if (ls == LinkStatus.Disconnected) {
                _state = ProgramState.Unknown;
                _pvCard.Value = _spCard.Value = _opCard.Value = "—";
                _pvCard.Detail = _spCard.Detail = _opCard.Detail = "Not connected";
                _opCard.Bar = null;
                _stateCard.Value = "Offline";
                _stateCard.Accent = Theme.TextFaint;
                _stateCard.Detail = "Connect from the sidebar";
                _rowState.Value = "—";
                _chart.EmptyText = "Connect to the controller to see live data";
                if (_recorder != null) StopRecording();
            } else if (ls == LinkStatus.NoResponse) {
                _stateCard.Detail = "No response from controller";
                _stateCard.Accent = Theme.Red;
            } else if (ls == LinkStatus.Connected) {
                _chart.EmptyText = "Waiting for data…";
            }
            _lastLink = ls;
            UpdateButtons();
            _chart.Invalidate();
        }

        private void OnBedLinkChanged() {
            var ls = State.Bed.State;
            if (ls == LinkStatus.Disconnected || !Bed) {
                _bedState = ProgramState.Unknown;
                _pvCard.Value2 = _spCard.Value2 = _opCard.Value2 = "—";
                _pvCard.Detail2 = _spCard.Detail2 = _opCard.Detail2 = Bed ? "Not connected" : "Off";
                _opCard.Bar2 = null;
            } else if (ls == LinkStatus.NoResponse) {
                _pvCard.Detail2 = "No response";
            }
            UpdateProgramInfo(State.Device.Last);
            UpdateButtons();
        }

        private double X(DateTime time, DeviceService device) => (time - _sessionStart).TotalSeconds * device.TimeScale;

        private void OnTelemetry(Telemetry t) {
            double x = X(t.Time, State.Device);
            _lastX = x;
            if (t.Pv.HasValue) _pv.Points.Add(new PointD(x, t.Pv.Value));
            if (t.Sp.HasValue) _sp.Points.Add(new PointD(x, t.Sp.Value));
            if (t.Op.HasValue) _op.Points.Add(new PointD(x, t.Op.Value));
            Trim(_pv, _sp, _op);

            if (t.State != ProgramState.Unknown) _state = t.State;

            // Cards
            _pvCard.Value = t.Pv.HasValue ? t.Pv.Value.ToString("0.0") : "—";
            double? rate = Rate(_pv, 10);
            _pvCard.Detail = rate.HasValue ? $"Rate {(rate.Value >= 0 ? "+" : "")}{rate.Value:0.00} °C/s" : "";
            _spCard.Value = t.Sp.HasValue ? t.Sp.Value.ToString("0.0") : "—";
            _spCard.Detail = Deviation(t, _state);
            _opCard.Value = t.Op.HasValue ? t.Op.Value.ToString("0") : "—";
            _opCard.Bar = t.Op.HasValue ? t.Op.Value / 100.0 : (double?)null;
            _opCard.Detail = t.StatusWord.HasValue ? $"Status word {Pc410Protocol.FormatHexWord(t.StatusWord.Value)}" : (_opCard.Detail ?? "");
            if (string.IsNullOrEmpty(_opCard.Detail) || _opCard.Detail == "Not connected") _opCard.Detail = "Heater power";

            UpdateProgramInfo(t);

            if (_recorder != null) {
                try { _recorder.Write(t, Bed ? State.Bed.Last : null, State.Sync.WaitingText); } catch (Exception ex) { State.SetStatus("Recording stopped: " + ex.Message, StatusKind.Error); StopRecording(); }
                if (_autoStopAt.HasValue && DateTime.Now >= _autoStopAt.Value) StopRecording();
                else if (_recorder != null) _recordInfo.Text = $"Recording · {_recorder.Rows} rows\n{Path.GetFileName(_recorder.FilePath)}";
            }

            UpdateWindow();
            UpdateButtons();
        }

        private void OnBedTelemetry(Telemetry t) {
            if (!Bed) return;
            double x = X(t.Time, State.Bed);
            if (t.Pv.HasValue) _bedPv.Points.Add(new PointD(x, t.Pv.Value));
            if (t.Sp.HasValue) _bedSp.Points.Add(new PointD(x, t.Sp.Value));
            if (t.Op.HasValue) _bedOp.Points.Add(new PointD(x, t.Op.Value));
            Trim(_bedPv, _bedSp, _bedOp);
            if (t.State != ProgramState.Unknown) _bedState = t.State;

            _pvCard.Value2 = t.Pv.HasValue ? t.Pv.Value.ToString("0.0") : "—";
            double? rate = Rate(_bedPv, 10);
            _pvCard.Detail2 = rate.HasValue ? $"Rate {(rate.Value >= 0 ? "+" : "")}{rate.Value:0.00} °C/s" : "";
            _spCard.Value2 = t.Sp.HasValue ? t.Sp.Value.ToString("0.0") : "—";
            _spCard.Detail2 = Deviation(t, _bedState);
            _opCard.Value2 = t.Op.HasValue ? t.Op.Value.ToString("0") : "—";
            _opCard.Bar2 = t.Op.HasValue ? t.Op.Value / 100.0 : (double?)null;
            _opCard.Detail2 = "Heater power";
            if (!State.Device.IsConnected) UpdateWindow();
        }

        private static string Deviation(Telemetry t, ProgramState state) {
            if (t.Pv.HasValue && t.Sp.HasValue && state != ProgramState.Idle) {
                double d = t.Pv.Value - t.Sp.Value;
                return $"Deviation {(d >= 0 ? "+" : "")}{d:0.0} °C";
            }
            return state == ProgramState.Idle ? "Program not running" : "";
        }

        private static void Trim(params ChartSeries[] series) {
            const int cap = 250000;
            foreach (var s in series)
                if (s.Points.Count > cap) s.Points.RemoveRange(0, s.Points.Count - cap);
        }

        private static double? Rate(ChartSeries series, double seconds) {
            var pts = series.Points;
            if (pts.Count < 3) return null;
            var last = pts[pts.Count - 1];
            int i = pts.Count - 1;
            while (i > 0 && last.X - pts[i].X < seconds) i--;
            var first = pts[i];
            if (last.X - first.X < 2) return null;
            return (last.Y - first.Y) / (last.X - first.X);
        }

        private static (string Text, Color Color) Describe(ProgramState state) => state switch {
            ProgramState.Running => ("Running", Theme.Green),
            ProgramState.Hold => ("On hold", Theme.Yellow),
            ProgramState.Idle => ("Idle", Theme.TextDim),
            _ => ("Unknown", Theme.TextFaint),
        };

        /// <summary>State of the whole program: either controller running counts as running.</summary>
        private ProgramState Combined() {
            if (!Bed || !State.Bed.IsConnected) return _state;
            if (_state == ProgramState.Running || _bedState == ProgramState.Running) return ProgramState.Running;
            if (_state == ProgramState.Hold || _bedState == ProgramState.Hold) return ProgramState.Hold;
            if (_state == ProgramState.Idle && _bedState == ProgramState.Idle) return ProgramState.Idle;
            return _state != ProgramState.Unknown ? _state : _bedState;
        }

        private string StepText(int? segment, int steps) {
            if (segment is not int seg || seg <= 0) return "";
            int step = SyncCoordinator.StepOf(seg, steps, State.Sync.PairedSegments) ?? seg;
            return steps > 0 ? $"step {step} of {steps}" : $"step {step}";
        }

        private void UpdateProgramInfo(Telemetry t) {
            var combined = Combined();
            var (st, c) = Describe(combined);
            string waiting = Bed ? State.Sync.WaitingText : "";
            if (State.Sync.UserHold && combined != ProgramState.Idle) (st, c) = ("On hold", Theme.Yellow);
            else if (waiting.Length > 0) (st, c) = (waiting, Theme.Yellow);
            if (State.Device.State == LinkStatus.NoResponse || (Bed && State.Bed.State == LinkStatus.NoResponse)) c = Theme.Red;
            _stateCard.Value = waiting.Length > 0 && !State.Sync.UserHold ? "Waiting" : Describe(combined).Text;
            _stateCard.Accent = c;
            _rowState.Value = st;
            _rowState.ValueColor = c;

            int? pattern = _runPattern ?? State.Device.ActivePattern;
            bool active = combined is ProgramState.Running or ProgramState.Hold;
            int steps = _runPlan != null ? _runPlan.StepStarts.Count : 0;
            string seg = t?.Segment.HasValue == true && t.Segment.Value > 0
                ? (steps > 0 ? $"{t.Segment.Value} of {steps}" : t.Segment.Value.ToString())
                : "—";
            _rowStep.Value = active ? seg : "—";
            if (Bed) {
                var (irText, irColor) = State.Device.IsConnected ? Describe(_state) : ("Offline", Theme.TextFaint);
                var (bedText, bedColor) = State.Bed.IsConnected ? Describe(_bedState) : ("Offline", Theme.TextFaint);
                string irStep = active && _state != ProgramState.Idle ? StepText(t?.Segment, steps) : "";
                string bedStep = active && _bedState != ProgramState.Idle ? StepText(State.Bed.Last?.Segment, steps) : "";
                if (State.Sync.IrWaiting) (irText, irColor) = ("Waiting for bed", Theme.Yellow);
                if (State.Sync.BedWaiting) (bedText, bedColor) = ("Waiting for IR", Theme.Yellow);
                _rowIr.Value = irStep.Length > 0 ? $"{irText} · {irStep}" : irText;
                _rowIr.ValueColor = irColor;
                _rowBed.Value = bedStep.Length > 0 ? $"{bedText} · {bedStep}" : bedText;
                _rowBed.ValueColor = bedColor;
            }

            if (active && _runStart.HasValue) {
                double el = _lastX - _runStart.Value;
                double planned = PlannedTotal();
                _rowElapsed.Value = ProfileMath.FormatDuration(el);
                _rowRemaining.Value = planned > 0 ? ProfileMath.FormatDuration(Math.Max(0, planned - el)) : "—";
                _stateCard.Detail = State.Sync.StepUnknown
                    ? "Steps not reported: can't wait"
                    : $"Pattern {pattern?.ToString() ?? "?"} · {ProfileMath.FormatDuration(el)} elapsed";
            } else {
                _rowElapsed.Value = "—";
                _rowRemaining.Value = "—";
                _stateCard.Detail = State.Device.State == LinkStatus.NoResponse ? "No response from controller"
                    : pattern.HasValue ? $"Active pattern {pattern}" : "Ready";
            }
        }

        /// <summary>Planned duration of the run, ignoring waits (the longer of the two controllers).</summary>
        private double PlannedTotal() {
            double ir = _runPlan?.TotalSeconds ?? 0;
            double bed = Bed ? _runBedPlan?.TotalSeconds ?? 0 : 0;
            return Math.Max(ir, bed);
        }

        private void OnProgramStateChanged(ProgramState prev, ProgramState now) {
            bool wasActive = prev is ProgramState.Running or ProgramState.Hold;
            if (now == ProgramState.Running && !wasActive) {
                // A run has started (from this app or the front panel).
                _runStart = _lastX;
                _runPattern = State.Device.ActivePattern;
                _plan.Points.Clear();
                _bedPlan.Points.Clear();
                _chart.Markers.Clear();
                _runPlan = _runBedPlan = null;
                var prof = _runPattern.HasValue ? State.SlotProfiles[_runPattern.Value] : null;
                if (_runPattern.HasValue && Bed) {
                    // Started here or on the front panel: use the choices last written to this pattern, if they still fit.
                    var sync = State.Settings.SyncFor(_runPattern.Value);
                    State.Sync.Sync = sync != null && prof != null && sync.Steps == prof.Steps.Count ? sync : null;
                }
                if (prof != null && prev != ProgramState.Unknown) {
                    double start = _pv.Points.Count > 0 ? _pv.Points[_pv.Points.Count - 1].Y : State.Settings.PreviewStartTemperature;
                    _runPlan = ProfileMath.BuildPlannedCurve(prof, start);
                    foreach (var p in _runPlan.Points) _plan.Points.Add(new PointD(_runStart.Value + p.Time, p.Temp));
                    if (Bed) {
                        double bedStart = _bedPv.Points.Count > 0 ? _bedPv.Points[_bedPv.Points.Count - 1].Y : start;
                        _runBedPlan = ProfileMath.BuildPlannedCurve(prof.ForBed(), bedStart);
                        foreach (var p in _runBedPlan.Points) _bedPlan.Points.Add(new PointD(_runStart.Value + p.Time, p.Temp));
                    }
                    for (int i = 0; i < _runPlan.StepStarts.Count; i++)
                        _chart.Markers.Add(new ChartMarker { X = _runStart.Value + _runPlan.StepStarts[i], Label = "S" + (i + 1) });
                    _rowPlanned.Value = ProfileMath.FormatDuration(PlannedTotal());
                } else {
                    _chart.Markers.Add(new ChartMarker { X = _runStart.Value, Label = prev == ProgramState.Unknown ? "Run in progress" : "Start" });
                    _rowPlanned.Value = "—";
                }
                _window.SelectedIndex = 0;
                State.SetStatus($"Program started (pattern {_runPattern?.ToString() ?? "?"})", StatusKind.Success);
                if (State.Settings.AutoRecordRuns && _recorder == null) StartRecording(true);
                else if (_autoRecording) _autoStopAt = null;
            } else if (now == ProgramState.Idle && wasActive) {
                bool bedStillRunning = Bed && State.Bed.IsConnected && _bedState is ProgramState.Running or ProgramState.Hold && !_userStopped;
                _chart.Markers.Add(new ChartMarker { X = _lastX, Label = bedStillRunning ? "IR end" : "End", Color = Theme.Green });
                if (bedStillRunning) State.SetStatus("Top IR program finished; bed still running", StatusKind.Success);
            }
            OnCombinedStateChanged();
            UpdateProgramInfo(State.Device.Last);
            UpdateButtons();
        }

        /// <summary>When neither controller runs any more: report the end, forget the run's waits and finish recording.</summary>
        private void OnCombinedStateChanged() {
            bool active = Combined() is ProgramState.Running or ProgramState.Hold;
            if (active) {
                if (!_wasActive) State.Sync.RunStarted();
                _wasActive = true;
                return;
            }
            if (!_wasActive || Combined() != ProgramState.Idle) return;
            _wasActive = false;
            State.SetStatus(_userStopped ? "Program stopped" : "Program finished", StatusKind.Success);
            _userStopped = false;
            State.Sync.Reset();
            State.Sync.Sync = null;
            if (_autoRecording && _recorder != null)
                _autoStopAt = DateTime.Now.AddSeconds(State.Settings.RecordAfterRunSeconds);
            _runPattern = null;
        }

        private void ShowPlannedPreview() {
            if (Combined() is ProgramState.Running or ProgramState.Hold) return;
            int slot = _slot.SelectedIndex;
            var p = slot >= 0 ? State.SlotProfiles[slot] : null;
            if (p == null) {
                _rowPlanned.Value = "—";
                return;
            }
            double start = State.Settings.PreviewStartTemperature;
            double total = ProfileMath.BuildPlannedCurve(p, start).TotalSeconds;
            if (Bed) total = Math.Max(total, ProfileMath.BuildPlannedCurve(p.ForBed(), start).TotalSeconds);
            _rowPlanned.Value = ProfileMath.FormatDuration(total);
        }

        // ==================================================================== chart window

        private void UpdateWindow() {
            int w = _window.SelectedIndex;
            double now = _lastX;
            if (w == 0) {
                if (_runStart.HasValue) {
                    _chart.XMin = Math.Max(0, _runStart.Value - 10);
                    double end = _runPlan != null ? _runStart.Value + PlannedTotal() + 30 : now;
                    _chart.XMax = Math.Max(now + 5, end);
                    _chart.MinXSpan = 60;
                } else { _chart.XMin = Math.Max(0, now - 300); _chart.XMax = Math.Max(now, 300); _chart.MinXSpan = 300; }
            } else if (w is >= 1 and <= 3) {
                double span = WindowSeconds[w];
                _chart.XMin = Math.Max(0, now - span);
                _chart.XMax = Math.Max(now, span);
                _chart.MinXSpan = span;
            } else {
                _chart.XMin = 0;
                _chart.XMax = Math.Max(now, 60);
                _chart.MinXSpan = 60;
            }
            _chart.LimitLine = State.Settings.MaxTemperature;
            _chart.LimitLabel = $"Limit {State.Settings.MaxTemperature:0} °C";
            _chart.Invalidate();
        }

        private void ClearChart() {
            _sessionStart = DateTime.Now;
            _lastX = 0;
            foreach (var s in _chart.Series) s.Points.Clear();
            _chart.Markers.Clear();
            _runStart = null;
            _runPlan = _runBedPlan = null;
            UpdateWindow();
        }

        private void ExportPng() {
            using var d = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = $"ReflowLink_{DateTime.Now:yyyy-MM-dd_HHmmss}.png" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try {
                using (var bmp = _chart.Snapshot()) bmp.Save(d.FileName, ImageFormat.Png);
                State.SetStatus("Chart exported to " + d.FileName, StatusKind.Success);
            } catch (Exception ex) { DarkDialog.Error(FindForm(), "Cannot export chart", ex.Message); }
        }

        // ==================================================================== program control

        private void UpdateButtons() {
            bool conn = State.Device.IsConnected && (!Bed || State.Bed.IsConnected);
            bool any = State.Device.IsConnected || (Bed && State.Bed.IsConnected);
            bool busy = State.Busy;
            var combined = Combined();
            bool active = combined is ProgramState.Running or ProgramState.Hold;
            _run.Enabled = conn && !busy && !active;
            _slot.Enabled = !busy && !active;
            _stop.Enabled = any && !busy;
            _hold.Enabled = conn && !busy && active;
            // With bed heat control the coordinator may hold one controller; Resume is about the user's own hold.
            bool resume = Bed ? State.Sync.UserHold : _state == ProgramState.Hold;
            if (_hold.Text != (resume ? "Resume" : "Hold")) {
                _hold.Text = resume ? "Resume" : "Hold";
                _hold.Glyph = resume ? Glyph.Play : Glyph.Pause;
            }
        }

        private async Task StartProgram() {
            int slot = _slot.SelectedIndex;
            if (slot < 0) return;
            if (State.IsSlotBlocked(slot)) {
                DarkDialog.Error(FindForm(), $"Pattern {slot} can't be started",
                    $"A controller rejected part of the last profile written to pattern {slot}. Write a valid profile to it on the Profiles page before starting it.");
                return;
            }
            // Read the pattern back and check it against ReflowLink's limits and the controllers' setpoint limits right
            // before starting, so a pattern changed elsewhere (front panel, other software) can't run out of range.
            double limit = State.Settings.MaxTemperature, bedLimit = State.Settings.Bed.MaxTemperature;
            bool both = Bed;
            var sync = both ? State.Settings.SyncFor(slot) : null;
            State.Sync.Reset();
            State.Sync.Sync = null; // set once the pattern has been checked
            var (ok, prof) = await State.RunPairAsync(both ? $"Starting pattern {slot} on both controllers" : $"Starting pattern {slot}", (ir, bed, progress, ct) => {
                var p = PatternPair.Check(ir, bed, slot, limit, bedLimit, progress, ct);
                PatternPair.Start(ir, bed, slot);
                return p;
            });
            if (!ok) return;
            var known = State.SlotProfiles[slot];
            if (known != null && known.Steps.Count == prof.Steps.Count) {
                prof.Name = known.Name;
                prof.Notes = known.Notes;
            }
            if (sync != null && sync.ApplyTo(prof)) {
                State.Sync.Sync = sync;
            } else if (sync != null) {
                State.SetStatus("The Wait for Bed / Wait for IR choices saved for this pattern don't match it any more, so they aren't used. Write the profile again.", StatusKind.Warning);
            }
            State.SetSlotProfile(slot, prof);
            State.Device.NotePattern(slot);
            if (both) State.Bed.NotePattern(slot);
        }

        private async Task HoldOrResume() {
            if (!Bed) {
                if (_state == ProgramState.Hold) await State.RunAsync("Resuming program", d => d.ResumeIfHeld());
                else await State.RunAsync("Holding program", d => d.Hold());
                return;
            }
            if (State.Sync.UserHold) {
                var (ok, _) = await State.RunPairAsync("Resuming both programs", (ir, bed, p, ct) => { PatternPair.Resume(ir, bed); return true; });
                if (ok) State.Sync.UserHold = false;
            } else {
                // Stop the coordinator first, so it can't resume anything meanwhile. If the hold only partly worked, the user
                // hold stays on, so Resume puts back whatever was held.
                State.Sync.UserHold = true;
                await State.RunPairAsync("Holding both programs", (ir, bed, p, ct) => { PatternPair.Hold(ir, bed); return true; });
            }
            UpdateButtons();
        }

        private async Task StopProgram() {
            _userStopped = true;
            if (!Bed) {
                var (stopped, _) = await State.RunAsync("Stopping program", d => d.Stop());
                if (!stopped) _userStopped = false;
                return;
            }
            State.Sync.UserHold = true; // cancels queued holds and resumes, and blocks new ones while stopping
            var (ok, _) = await State.StopAllAsync();
            if (ok) State.Sync.Halt();
            else _userStopped = false; // the user hold stays on: nothing is resumed automatically after a failed stop
        }

        /// <summary>The bed controller stopped answering during a run: stop the IR and tell the user.</summary>
        private async Task OnBedLost() {
            string result;
            try {
                await State.Device.RunAsync(d => d.Stop());
                result = "The Top IR program was stopped. The bed controller keeps running its own program: stop it on its front panel if it's still heating.";
            } catch (Exception ex) {
                result = $"The Top IR program couldn't be stopped ({ex.Message}). Stop both programs on the station's front panels.";
            }
            State.SetStatus("Bed Heat controller stopped answering: Top IR program stopped", StatusKind.Error);
            DarkDialog.Error(FindForm(), "Bed Heat controller not answering", "The Bed Heat controller stopped answering during the run.\n\n" + result);
        }

        // ==================================================================== recording

        private void StartRecording(bool auto) {
            if (_recorder != null) return;
            try {
                int? pattern = _runPattern ?? State.Device.ActivePattern;
                var prof = pattern.HasValue ? State.SlotProfiles[pattern.Value] : null;
                string label = prof != null && !string.IsNullOrWhiteSpace(prof.Name) ? prof.Name
                    : pattern.HasValue ? "pattern" + pattern.Value : "session";
                _recorder = new CsvRecorder(State.Settings.EffectiveLogFolder, label, Bed);
                _autoRecording = auto;
                _autoStopAt = null;
                _recordInfo.Text = "Recording · 0 rows\n" + Path.GetFileName(_recorder.FilePath);
                SetToggle(true);
                State.SetStatus("Recording to " + _recorder.FilePath);
            } catch (Exception ex) {
                _recorder = null;
                SetToggle(false);
                DarkDialog.Error(FindForm(), "Cannot start recording", ex.Message);
            }
        }

        private void StopRecording() {
            if (_recorder == null) { SetToggle(false); return; }
            var r = _recorder;
            _recorder = null;
            r.Dispose();
            _autoRecording = false;
            _autoStopAt = null;
            _recordInfo.Text = $"Saved {r.Rows} rows\n{Path.GetFileName(r.FilePath)}";
            SetToggle(false);
            State.SetStatus("Recording saved: " + r.FilePath, StatusKind.Success);
        }

        private void SetToggle(bool on) {
            _settingToggle = true;
            _record.Checked = on;
            _settingToggle = false;
        }

        public static void OpenFolder(string folder) {
            try {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            } catch { }
        }

        public bool IsProgramActive => Combined() is ProgramState.Running or ProgramState.Hold;

        public void Shutdown() {
            if (_recorder != null) StopRecording();
        }
    }
}
