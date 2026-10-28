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
        private static readonly Color IrIn2Color = Color.FromArgb(250, 204, 21);
        private static readonly Color BedIn2Color = Color.FromArgb(244, 114, 182);

        private readonly StatCard _pvCard, _spCard, _opCard, _stateCard;
        private readonly ChartView _chart;
        private readonly ChartSeries _pv, _sp, _op, _plan, _bedPv, _bedSp, _bedOp, _bedPlan, _irIn2, _bedIn2;
        private readonly ModernDropDown _window, _slot;
        private readonly ModernButton _run, _hold, _stop, _openLogs;
        private readonly ToggleSwitch _record;
        private readonly InfoRow _rowState, _rowStep, _rowIr, _rowBed, _rowIrIn2, _rowBedIn2, _rowElapsed, _rowPlanned, _rowRemaining;
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
        // Why ReflowLink stopped the run itself (a controller or the bed reading lost); null for a normal end or Stop.
        private string _abortReason;

        /// <summary>Raised when a program starts or ends (either controller running counts).</summary>
        public event Action<bool> ActiveChanged;

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
            // Informative second inputs: shown, never used for control.
            _irIn2 = new ChartSeries { Name = "IN2 (Top IR)", Color = IrIn2Color, Width = 1.5f };
            _bedIn2 = new ChartSeries { Name = "IN2 (Bed Heat)", Color = BedIn2Color, Width = 1.5f };
            _chart = new ChartView { Dock = DockStyle.Fill, EmptyText = "Connect to the controller to see live data" };
            // Drawn first to last; the legend lists them last to first (IR before bed).
            _chart.Series.AddRange([_bedOp, _op, _bedPlan, _plan, _bedSp, _sp, _bedIn2, _irIn2, _bedPv, _pv]);

            _window = new ModernDropDown { Width = Theme.S(150), Margin = new Padding(0, 0, Theme.S(8), 0) };
            _window.SetItems(WindowNames, 2);
            _window.SelectedIndexChanged += (s, e) => UpdateWindow();
            var export = new ModernButton("", Glyph.Image) { Margin = new Padding(0, 0, Theme.S(8), 0) };
            export.Click += (s, e) => ExportPng();
            var clear = new ModernButton("", Glyph.Trash) { Margin = Padding.Empty };
            clear.Click += (s, e) => ClearChart(true);
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
            tip.SetToolTip(_run, "Start the selected pattern (F5)");
            tip.SetToolTip(_hold, "Hold or resume the program (F6)");
            tip.SetToolTip(_stop, "Stop the program (F12, from any page)");
            Add(row, 8);

            Add(new Divider(), 12);
            _rowState = new InfoRow("State");
            _rowStep = new InfoRow("Segment");
            _rowIr = new InfoRow("Top IR");
            _rowBed = new InfoRow("Bed heat");
            _rowIrIn2 = new InfoRow("IN2 (Top IR)");
            _rowBedIn2 = new InfoRow("IN2 (Bed Heat)");
            _rowElapsed = new InfoRow("Elapsed");
            _rowPlanned = new InfoRow("Planned duration");
            _rowRemaining = new InfoRow("Remaining (est.)");
            Add(_rowState);
            Add(_rowStep);
            Add(_rowIr);
            Add(_rowBed);
            Add(_rowIrIn2);
            Add(_rowBedIn2);
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
            State.Bed.TelemetryReceived += t => { if (Dual) OnBedTelemetry(t); };
            State.BedSwitch.TelemetryReady += t => { if (State.SingleBed) OnBedTelemetry(t); };
            State.BedSwitch.SensorLost += async () => await OnBedSensorLost();
            State.Bed.StateChanged += (s, e) => OnBedLinkChanged();
            State.Bed.ProgramStateChanged += (prev, now) => { _bedState = now; OnCombinedStateChanged(); UpdateProgramInfo(State.Device.Last); UpdateButtons(); };
            State.BedEnabledChanged += ApplyBedMode;
            State.Sync.Changed += () => { UpdateProgramInfo(State.Device.Last); UpdateButtons(); };
            State.Sync.CommandFailed += msg => State.SetStatus(msg, StatusKind.Error);
            State.Sync.BedLost += async () => await OnBedLost();
            State.Sync.WaitTimeout += async msg => await OnWaitTimeout(msg);
            State.Sync.IrLost += () => {
                _abortReason = "the Top IR controller stopped answering";
                State.SetStatus("Top IR controller stopped answering", StatusKind.Error);
                DarkDialog.Error(FindForm(), "Top IR controller not answering", State.SingleBed
                    ? "The Top IR controller stopped answering during the run. It keeps running its program on its own, but ReflowLink can no longer "
                        + "switch the bed: OUT2 stays as it was, and only the bed controller behind it (or the thermostat) limits the bed. "
                        + "Check the cable, or stop the program on the front panel and switch the station off."
                    : "The Top IR controller stopped answering during the run. It keeps running its program on its own, and the bed carries on with its program too. "
                        + "Check the cable, or stop both programs on the station's front panels.");
            };
            State.SlotProfilesChanged += () => { _slot.SetItems(State.SlotLabels(), _slot.SelectedIndex); ShowPlannedPreview(); };
            State.BusyChanged += UpdateButtons;
            ApplyBedMode();
            OnLinkChanged();
            ShowPlannedPreview();
        }

        /// <summary>A bed heater is shown (second PC410 or single PC410).</summary>
        private bool Bed => State.BedEnabled;

        /// <summary>The bed is a second PC410 with a program of its own.</summary>
        private bool Dual => State.DualBed;

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
            _bedOp.Name = State.SingleBed ? "Bed demand" : "Bed output";
            // With a single PC410 the bed temperature is the Top IR controller's IN2; on a second PC410 it's its main input.
            _bedPv.Name = State.SingleBed ? "Bed temp (IN2)" : "Bed temp";
            bool irIn2 = State.IrIn2Shown, bedIn2 = State.BedIn2Shown;
            _irIn2.Visible = _irIn2.ShowInLegend = irIn2;
            _bedIn2.Visible = _bedIn2.ShowInLegend = bedIn2;
            // Top IR only: IN2 takes the second half of the Temperature tile. With a bed, it gets a row of its own.
            _pvCard.Split = bed || irIn2;
            _pvCard.Label2 = bed ? "Bed heat" : "IN2 (Top IR)";
            _pvCard.Accent2 = bed ? BedPvColor : IrIn2Color;
            _rowIrIn2.Visible = irIn2 && bed;
            _rowBedIn2.Visible = bedIn2;
            if (!irIn2) _irIn2.Points.Clear();
            if (!bedIn2) _bedIn2.Points.Clear();
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
                _rowIrIn2.Value = "—";
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
            if (State.SingleBed) OnBedLinkChanged();
            UpdateButtons();
            _chart.Invalidate();
        }

        private void OnBedLinkChanged() {
            // A single PC410's bed is read over the Top IR link.
            var ls = Dual ? State.Bed.State : State.Device.State;
            if (ls == LinkStatus.Disconnected || !Bed) {
                _bedState = ProgramState.Unknown;
                _pvCard.Value2 = _spCard.Value2 = _opCard.Value2 = "—";
                _pvCard.Detail2 = _spCard.Detail2 = _opCard.Detail2 = Bed ? "Not connected" : "Off";
                _opCard.Bar2 = null;
                _rowBedIn2.Value = "—";
                if (!Bed && State.IrIn2Shown) _pvCard.Detail2 = State.Device.IsConnected ? "Information only" : "Not connected";
            } else if (ls == LinkStatus.NoResponse) {
                _pvCard.Detail2 = "No response";
            }
            UpdateProgramInfo(State.Device.Last);
            UpdateButtons();
        }

        /// <summary>
        /// Chart time of a reading: one time base for both controllers, so their curves line up even when two simulators
        /// run at different speeds (the Top IR's speed counts while it's connected).
        /// </summary>
        private double X(DateTime time) {
            double scale = State.Device.IsConnected ? State.Device.TimeScale : Dual && State.Bed.IsConnected ? State.Bed.TimeScale : 1;
            return (time - _sessionStart).TotalSeconds * scale;
        }

        private void OnTelemetry(Telemetry t) {
            double x = X(t.Time);
            _lastX = x;
            if (t.Pv.HasValue) _pv.Points.Add(new PointD(x, t.Pv.Value));
            if (t.Sp.HasValue) _sp.Points.Add(new PointD(x, t.Sp.Value));
            if (t.Op.HasValue) _op.Points.Add(new PointD(x, t.Op.Value));
            if (State.IrIn2Shown) {
                if (t.Pv2.HasValue) _irIn2.Points.Add(new PointD(x, t.Pv2.Value));
                string in2 = t.Pv2.HasValue ? t.Pv2.Value.ToString("0.0") : "—";
                if (!Bed) {
                    _pvCard.Value2 = in2;
                    _pvCard.Detail2 = "Information only";
                } else {
                    _rowIrIn2.Value = t.Pv2.HasValue ? in2 + " °C" : "—";
                }
            }
            Trim(_pv, _sp, _op, _irIn2);

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
                try {
                    var bedReading = Dual ? State.Bed.Last : State.SingleBed ? State.BedSwitch.Last : null;
                    _recorder.Write(t, bedReading, State.Sync.WaitingText, State.SingleBed ? State.BedSwitch.Out2 : null);
                } catch (Exception ex) {
                    State.SetStatus("Recording stopped: " + ex.Message, StatusKind.Error);
                    StopRecording();
                }
                if (_autoStopAt.HasValue && DateTime.Now >= _autoStopAt.Value) StopRecording();
                else if (_recorder != null) _recordInfo.Text = $"Recording · {_recorder.Rows} rows\n{Path.GetFileName(_recorder.FilePath)}";
            }

            UpdateWindow();
            UpdateButtons();
        }

        private void OnBedTelemetry(Telemetry t) {
            if (!Bed) return;
            double x = X(t.Time);
            // With the Top IR offline the bed's readings move the chart on.
            if (!State.Device.IsConnected && x > _lastX) _lastX = x;
            if (t.Pv.HasValue) _bedPv.Points.Add(new PointD(x, t.Pv.Value));
            if (t.Sp.HasValue) _bedSp.Points.Add(new PointD(x, t.Sp.Value));
            if (t.Op.HasValue) _bedOp.Points.Add(new PointD(x, t.Op.Value));
            if (Dual && State.BedIn2Shown) {
                if (t.Pv2.HasValue) _bedIn2.Points.Add(new PointD(x, t.Pv2.Value));
                _rowBedIn2.Value = t.Pv2.HasValue ? t.Pv2.Value.ToString("0.0") + " °C" : "—";
            }
            Trim(_bedPv, _bedSp, _bedOp, _bedIn2);
            if (t.State != ProgramState.Unknown) _bedState = t.State;

            _pvCard.Value2 = t.Pv.HasValue ? t.Pv.Value.ToString("0.0") : "—";
            double? rate = Rate(_bedPv, 10);
            _pvCard.Detail2 = rate.HasValue ? $"Rate {(rate.Value >= 0 ? "+" : "")}{rate.Value:0.00} °C/s" : "";
            _spCard.Value2 = t.Sp.HasValue ? t.Sp.Value.ToString("0.0") : "—";
            _spCard.Detail2 = Deviation(t, _bedState);
            _opCard.Value2 = t.Op.HasValue ? t.Op.Value.ToString("0") : "—";
            _opCard.Bar2 = t.Op.HasValue ? t.Op.Value / 100.0 : (double?)null;
            _opCard.Detail2 = State.SingleBed
                ? State.BedSwitch.Out2 switch { true => "OUT2 on", false => "OUT2 off", _ => "OUT2 unknown" }
                : "Heater power";
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
            // Drop the oldest tenth at once rather than one point per poll, which would move the whole list every time.
            foreach (var s in series)
                if (s.Points.Count > cap) s.Points.RemoveRange(0, s.Points.Count - cap + cap / 10);
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
            if (!Dual || !State.Bed.IsConnected) return _state;
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
            if (State.Device.State == LinkStatus.NoResponse || (Dual && State.Bed.State == LinkStatus.NoResponse)) c = Theme.Red;
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
                var (bedText, bedColor) = Dual
                    ? State.Bed.IsConnected ? Describe(_bedState) : ("Offline", Theme.TextFaint)
                    : State.Device.IsConnected ? DescribeSwitch() : ("Offline", Theme.TextFaint);
                string irStep = active && _state != ProgramState.Idle ? StepText(t?.Segment, steps) : "";
                string bedStep = !Dual ? "" : active && _bedState != ProgramState.Idle ? StepText(State.Bed.Last?.Segment, steps) : "";
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

        /// <summary>What the single PC410 bed control is doing, for the Bed row.</summary>
        private (string Text, Color Color) DescribeSwitch() {
            var b = State.BedSwitch;
            string out2 = b.Out2 switch { true => "OUT2 on", false => "OUT2 off", _ => "OUT2 ?" };
            if (b.Reason is "Heating" or "Regulating") return ($"{b.Demand:0}% · {out2}", b.WantOn ? Theme.Green : Theme.TextDim);
            bool problem = _state is ProgramState.Running or ProgramState.Hold && b.Reason != "Stopped";
            return ($"{b.Reason} · {out2}", problem ? Theme.Yellow : Theme.TextDim);
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
                _userStopped = false;
                _abortReason = null;
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
                        _runBedPlan = ProfileMath.BuildBedCurve(prof, start, bedStart, State.SingleBed);
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
                bool bedStillRunning = Dual && State.Bed.IsConnected && _bedState is ProgramState.Running or ProgramState.Hold && !_userStopped;
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
                if (!_wasActive) {
                    State.Sync.RunStarted();
                    ActiveChanged?.Invoke(true);
                }
                _wasActive = true;
                return;
            }
            if (!_wasActive || Combined() != ProgramState.Idle) return;
            _wasActive = false;
            ActiveChanged?.Invoke(false);
            if (_abortReason != null) State.SetStatus("Program aborted: " + _abortReason, StatusKind.Error);
            else State.SetStatus(_userStopped ? "Program stopped" : "Program finished", StatusKind.Success);
            _userStopped = false;
            _abortReason = null;
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
            if (Bed) total = Math.Max(total, ProfileMath.BuildBedCurve(p, start, start, State.SingleBed).TotalSeconds);
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

        /// <param name="ask">From the Clear button: during a run, ask first.</param>
        private void ClearChart(bool ask = false) {
            if (IsProgramActive) {
                // Keep the run's time base, planned curves and step markers; only the readings so far go.
                if (ask && !DarkDialog.Confirm(FindForm(), "Clear the chart during a run?",
                        "The readings so far are removed from the chart (a CSV recording isn't affected). The planned curves and step markers stay.",
                        "Clear", "Cancel"))
                    return;
                foreach (var s in new[] { _pv, _sp, _op, _bedPv, _bedSp, _bedOp, _irIn2, _bedIn2 }) s.Points.Clear();
                UpdateWindow();
                return;
            }
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
            bool conn = State.Device.IsConnected && (!Dual || State.Bed.IsConnected);
            bool any = State.Device.IsConnected || (Dual && State.Bed.IsConnected);
            bool busy = State.Busy;
            var combined = Combined();
            bool active = combined is ProgramState.Running or ProgramState.Hold;
            _run.Enabled = conn && !busy && !active;
            _slot.Enabled = !busy && !active;
            // Stop is never blocked by another operation: it cancels it and is sent as soon as the bus is free.
            _stop.Enabled = any;
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
            bool both = Dual, single = State.SingleBed;
            var sync = Bed ? State.Settings.SyncFor(slot) : null;
            if (single && !CanStartSingleBed(slot, sync)) return;
            State.Sync.Reset();
            State.Sync.Sync = null; // set once the pattern has been checked
            int stops = State.StopCount;
            var (ok, prof) = await State.RunPairAsync(both ? $"Starting pattern {slot} on both controllers" : $"Starting pattern {slot}", (ir, bed, progress, ct) => {
                var p = PatternPair.Check(ir, bed, slot, limit, bedLimit, progress, ct);
                // The bed targets kept by ReflowLink must belong to the profile the pattern holds now.
                if (single && !sync.Matches(p))
                    throw new ProfileRejectedException(slot, [$"Pattern {slot} was changed since ReflowLink saved its bed targets. Write the profile to it again."], false, false);
                ct.ThrowIfCancellationRequested(); // Stop pressed while checking: don't start
                PatternPair.Start(ir, bed, slot);
                return p;
            });
            // Stop pressed while starting: the stop has gone out after the start, so don't set this run up.
            if (!ok || State.StopCount != stops) return;
            State.Sync.CheckedRun = true;
            if (single) {
                State.BedSwitch.Begin(BedPlan.From(sync, State.Device.Last?.Pv ?? sync.IrTargets[0], State.BedSwitch.BedTemperature ?? sync.BedTargets[0]));
                sync.ApplyTo(prof, true);
            }
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

        /// <summary>Single PC410: the pattern needs saved bed targets and the bed has to be readable before a start.</summary>
        private bool CanStartSingleBed(int slot, PatternSync sync) {
            if (sync == null || !sync.HasTargets) {
                DarkDialog.Error(FindForm(), $"No bed targets for pattern {slot}",
                    $"ReflowLink keeps the bed targets of each pattern, and has none for pattern {slot}. Write the profile to pattern {slot} on the Profiles page first.");
                return false;
            }
            double bedLimit = State.Settings.Bed.MaxTemperature;
            if (sync.BedTargets.Any(b => b > bedLimit)) {
                DarkDialog.Error(FindForm(), $"Bed target above the limit in pattern {slot}",
                    $"A bed target saved for pattern {slot} is above the {bedLimit:0} °C bed limit. Correct it and write the profile again.");
                return false;
            }
            if (State.BedSwitch.BedTemperature == null) {
                DarkDialog.Error(FindForm(), "No bed temperature",
                    "The Top IR controller doesn't report a bed temperature on IN2 (P2). Check the bed thermocouple wiring to terminals 16 (+) and 17 (−).");
                return false;
            }
            return true;
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

        /// <summary>
        /// Stops the program on every connected controller (Stop button and F12). Works while another operation runs: that
        /// operation is cancelled and the stop goes out as soon as the bus is free.
        /// </summary>
        public async Task StopProgram() {
            if (!State.Device.IsConnected && !(Dual && State.Bed.IsConnected)) {
                State.EnsureConnected();
                return;
            }
            _userStopped = true;
            if (State.SingleBed) State.BedSwitch.Stop(); // the bed goes off first, whatever happens to the stop
            if (Bed) State.Sync.UserHold = true; // cancels queued holds and resumes, and blocks new ones while stopping
            bool ok = await State.StopAllAsync();
            if (ok) {
                if (Bed) State.Sync.Halt();
            } else {
                _userStopped = false; // the user hold stays on: nothing is resumed automatically after a failed stop
            }
            UpdateButtons();
        }

        /// <summary>F12 from any page. Never throws: a failure is reported by the stop itself.</summary>
        public async void StopFromShortcut() {
            try {
                await StopProgram();
            } catch (Exception ex) {
                State.SetStatus("Stop failed: " + ex.Message, StatusKind.Error);
            }
        }

        /// <summary>F5 on the Dashboard: starts the selected pattern when Start is available.</summary>
        public void StartFromShortcut() {
            if (_run.Enabled) _run.PerformClick();
        }

        /// <summary>F6 on the Dashboard: holds or resumes when that's available.</summary>
        public void HoldFromShortcut() {
            if (_hold.Enabled) _hold.PerformClick();
        }

        /// <summary>A Wait for Bed / Wait for IR hold has lasted longer than the limit in Settings: ask what to do.</summary>
        private async Task OnWaitTimeout(string message) {
            // Raised while a reading is being handed out: let that finish before a dialog opens (readings keep arriving
            // while it's open, and must not be overtaken by the one that raised it).
            await Task.Yield();
            int r = DarkDialog.Show(FindForm(), "Still waiting", message
                + "\n\nThe other heater isn't reaching its setpoint. Check that it's heating (its controller, the bed SSR, the thermocouple), "
                + "or stop the program.", DialogTone.Warning, ["Keep waiting", "Stop program"], 0);
            if (r == 1) {
                await StopProgram();
            } else {
                State.Sync.KeepWaiting();
            }
        }

        /// <summary>The bed controller stopped answering during a run: stop the IR and tell the user.</summary>
        private async Task OnBedLost() {
            _abortReason = "the Bed Heat controller stopped answering";
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

        /// <summary>Single PC410: the bed reading (P2) was lost during the run. The bed is already off; stop the IR too.</summary>
        private async Task OnBedSensorLost() {
            _abortReason = "the bed temperature (IN2) was lost";
            string result;
            try {
                await State.Device.RunAsync(d => d.Stop());
                result = "The bed was switched off (OUT2) and the Top IR program was stopped.";
            } catch (Exception ex) {
                result = $"The bed was switched off (OUT2), but the Top IR program couldn't be stopped ({ex.Message}). Stop it on the front panel.";
            }
            State.SetStatus("Bed temperature lost: program stopped", StatusKind.Error);
            DarkDialog.Error(FindForm(), "Bed temperature lost",
                "The Top IR controller stopped reporting the bed temperature on IN2 (P2) during the run.\n\n" + result
                + "\n\nCheck the bed thermocouple wiring to terminals 16 (+) and 17 (−).");
        }

        // ==================================================================== recording

        private void StartRecording(bool auto) {
            if (_recorder != null) return;
            try {
                int? pattern = _runPattern ?? State.Device.ActivePattern;
                var prof = pattern.HasValue ? State.SlotProfiles[pattern.Value] : null;
                string label = prof != null && !string.IsNullOrWhiteSpace(prof.Name) ? prof.Name
                    : pattern.HasValue ? "pattern" + pattern.Value : "session";
                _recorder = new CsvRecorder(State.Settings.EffectiveLogFolder, label, Bed, State.SingleBed, State.IrIn2Shown, State.BedIn2Shown,
                    State.Settings.CsvLocalFormat, RunMetadata(pattern, prof));
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

        /// <summary>Describes the run at the top of the CSV file, so a recording can be understood (and overlaid) later.</summary>
        private System.Collections.Generic.List<string> RunMetadata(int? pattern, ReflowProfile prof) {
            var s = State.Settings;
            var units = s.Units;
            var lines = new System.Collections.Generic.List<string> {
                $"ReflowLink {typeof(DashboardPage).Assembly.GetName().Version.ToString(3)} recording",
                $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Station: {s.Station.Name}",
                $"Controller units: ramp {(units.Ramp == RampUnit.PerMinute ? "°C/min" : "°C/s")}, hold {(units.Dwell == DwellUnit.Minutes ? "min" : "s")}",
                "Bed heat: " + State.BedMode switch { BedMode.SinglePc410 => "single PC410 (IN2 + OUT2)", BedMode.SecondPc410 => "second PC410", _ => "off" },
                "Pattern: " + (pattern?.ToString() ?? "unknown"),
            };
            if (prof != null) {
                if (!string.IsNullOrWhiteSpace(prof.Name)) lines.Add("Profile: " + prof.Name);
                var c = System.Globalization.CultureInfo.InvariantCulture;
                for (int i = 0; i < prof.Steps.Count; i++) {
                    var st = prof.Steps[i];
                    string bed = Bed ? $", bed {st.BedTarget.ToString("0.#", c)} °C" : "";
                    string waits = (st.WaitForBed ? ", wait for bed" : "") + (st.WaitForIr ? ", wait for IR" : "");
                    lines.Add($"Step {i + 1}: {st.RampRate.ToString("0.##", c)} °C/s to {st.Target.ToString("0.#", c)} °C, hold {st.HoldSeconds.ToString("0", c)} s{bed}{waits}");
                }
            }
            return lines;
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
