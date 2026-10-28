// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Drawing;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Protocol;
using ReflowLink.Services;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI.Pages {
    /// <summary>
    /// Bed Heat Switching: settings and live state of single PC410 bed control, where ReflowLink reads the bed on IN2 (P2)
    /// and switches it through OUT2 by writing alarm 2 (A2).
    /// </summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class BedSwitchPage : PageBase {
        private readonly NumberBox _band, _integral, _cycle;
        private readonly InfoRow _rowBed, _rowSp, _rowDemand, _rowOut2, _rowA2, _rowState, _rowWrites, _rowLastWrite;
        private readonly ThemedLabel _error;
        private readonly ModernButton _off;
        private bool _loading;

        public BedSwitchPage(AppState state)
            : base(state, "Bed Heat Switching", "Single PC410: ReflowLink reads the bed on IN2 and switches it through OUT2") {
            var s = State.Settings.Bed.Switching;
            _loading = true;

            // ---------------------------------------------------------------- regulation
            _band = Num(1, 200, 1, 1, s.ProportionalBand, "°C", v => s.ProportionalBand = v);
            _integral = Num(0, 3600, 0, 10, s.IntegralSeconds, "s", v => s.IntegralSeconds = v);
            _cycle = Num(4, 120, 0, 1, s.CycleSeconds, "s", v => s.CycleSeconds = v);
            var regulation = Card("Regulation", "How ReflowLink turns the bed's setpoint into OUT2 on and off");
            var rf = Form(regulation);
            Row(rf, "Proportional band", _band, "Full power when the bed is this far below its setpoint, less as it gets closer. Wider is gentler.");
            Row(rf, "Integral time", _integral, "Removes the offset that's left at the setpoint. Shorter acts faster; 0 switches it off.");
            Row(rf, "Cycle time", _cycle, "OUT2 is on for the demanded share of each cycle (a 10 s cycle at 40 % is 4 s on, 6 s off). "
                + "Longer cycles switch less often, which spares a relay on OUT2.");
            Row(rf, "", new ThemedLabel(
                "The bed setpoint follows the IR program: during a step's ramp it moves towards the step's Bed Heat Target as the IR setpoint "
                + "moves towards its own target, and it holds the bed target during the step's hold. Wait for Bed holds the IR program while "
                + "the bed lags by more than the bed holdback.", Theme.Small, Theme.TextFaint) { MaximumSize = new Size(Theme.S(320), 0) }, null);
            FitCard(regulation, rf);

            // ---------------------------------------------------------------- wiring checklist
            var wiring = Card("Wiring and controller setup", "What single PC410 bed control expects");
            var wf = Form(wiring);
            Row(wf, "Bed thermocouple", Info("To the Top IR PC410's IN2: terminal 16 (+) and 17 (−). Read as P2."), null);
            Row(wf, "OUT2", Info("Terminals 6–7, in series with the bed controller's output to the bed SSR. A relay (R) or logic (L) output."), null);
            Row(wf, "PC410 setup", Info("Configuration menu (LOC 808): 0P2 = AL02, AL02 = HIAL. ReflowLink writes A2 = 001 to switch OUT2 on and A2 = 500 to switch it off."), null);
            Row(wf, "Hardware limit", Info("Keep the bed controller behind OUT2 (REX-C100) set to the highest bed temperature you allow, or fit a thermostat. "
                + "If ReflowLink closes or the USB cable is pulled while OUT2 is on, that's all that stops the bed.", Theme.Yellow), null);
            FitCard(wiring, wf);

            // ---------------------------------------------------------------- live
            _rowBed = new InfoRow("Bed temperature (P2)") { Dock = DockStyle.Top };
            _rowSp = new InfoRow("Bed setpoint") { Dock = DockStyle.Top };
            _rowDemand = new InfoRow("Demand") { Dock = DockStyle.Top };
            _rowOut2 = new InfoRow("OUT2") { Dock = DockStyle.Top };
            _rowA2 = new InfoRow("Alarm 2 (A2)") { Dock = DockStyle.Top };
            _rowState = new InfoRow("State") { Dock = DockStyle.Top };
            _rowWrites = new InfoRow("A2 writes") { Dock = DockStyle.Top };
            _rowLastWrite = new InfoRow("Last write") { Dock = DockStyle.Top };
            _error = new ThemedLabel("", Theme.Small, Theme.Red) { AutoSize = true, MaximumSize = new Size(Theme.S(330), 0), Margin = new Padding(0, Theme.S(6), 0, 0) };
            _off = new ModernButton("Bed off for this run", Glyph.Stop) { Margin = new Padding(0, Theme.S(10), 0, 0) };
            _off.Click += (o, e) => {
                State.BedSwitch.Stop();
                State.SetStatus("Bed switched off (A2 = 500) until the next run", StatusKind.Normal);
            };
            var hint = new ThemedLabel("Each switch is one A2 write. ReflowLink only writes when OUT2 has to change, "
                + "and switches the bed off on Stop, at the end of a run, when the bed reading is lost, above the bed limit, before disconnecting and when closing.",
                Theme.Small, Theme.TextFaint) { AutoSize = true, MaximumSize = new Size(Theme.S(330), 0), Margin = new Padding(0, Theme.S(10), 0, 0) };

            var live = Card("Live", "Updated with every poll of the Top IR controller");
            var stack = new TableLayoutPanel {
                ColumnCount = 1,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = new Padding(0, Theme.S(4), 0, 0),
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (Control c in new Control[] { _rowBed, _rowSp, _rowDemand, _rowOut2, _rowA2, _rowState, _rowWrites, _rowLastWrite, _error, _off, hint }) {
                stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                if (c is InfoRow r) {
                    r.Dock = DockStyle.None;
                    r.Width = Theme.S(360);
                    r.Margin = Padding.Empty;
                }
                stack.Controls.Add(c, 0, stack.RowCount++);
            }
            live.Controls.Add(stack);
            stack.BringToFront();
            int h = live.Padding.Vertical + Theme.S(50) + stack.GetPreferredSize(new Size(Theme.S(400), 0)).Height + Theme.S(10);
            live.Height = h;

            // ---------------------------------------------------------------- layout
            var grid = Table(2, 1);
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(Column(true, regulation, wiring), 0, 0);
            grid.Controls.Add(Column(false, live), 1, 0);

            var scroller = new Panel {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Theme.Window,
                Padding = new Padding(0, 0, Theme.S(6), Theme.S(8)),
            };
            Theme.ApplyDarkScrollbars(scroller);
            scroller.Controls.Add(grid);
            Controls.Add(scroller);

            State.BedSwitch.Changed += () => { if (Visible) ShowLive(); };
            State.Device.StateChanged += (o, e) => ShowLive();
            State.BedEnabledChanged += ShowLive;
            _loading = false;
            ShowLive();
        }

        public override void OnShown() => ShowLive();

        private void ShowLive() {
            var b = State.BedSwitch;
            bool on = State.SingleBed, conn = State.Device.IsConnected;
            _rowBed.Value = b.BedTemperature is double t ? $"{t:0.0} °C" : "—";
            _rowSp.Value = b.Setpoint is double sp ? $"{sp:0.0} °C" : "—";
            _rowDemand.Value = b.Setpoint.HasValue ? $"{b.Demand:0} %" : "—";
            (_rowOut2.Value, _rowOut2.ValueColor) = b.Out2 switch {
                true => ("On", Theme.Green),
                false => ("Off", Theme.TextDim),
                _ => (conn && on ? "Unknown" : "—", conn && on ? Theme.Yellow : Theme.TextDim),
            };
            _rowA2.Value = b.Alarm2 is double a ? Pc410Protocol.FormatNumber(a, 1) : "—";
            _rowState.Value = on ? b.Reason : "Not in use";
            _rowState.ValueColor = b.Reason == "Heating" ? Theme.Green : Theme.Text;
            _rowWrites.Value = $"{b.SessionWrites} this session · {State.Settings.Bed.Switching.TotalWrites} total";
            _rowLastWrite.Value = b.LastWrite?.ToString("HH:mm:ss") ?? "—";
            _error.Text = string.IsNullOrEmpty(b.LastError) ? "" : "Last A2 write failed: " + b.LastError;
            _off.Enabled = on && conn;
        }

        // ---------------------------------------------------------------- builders

        private NumberBox Num(double min, double max, int decimals, double step, double value, string suffix, Action<double> apply) {
            var n = new NumberBox(min, max, decimals, step) {
                Suffix = suffix,
                Width = Theme.S(130),
                Margin = Padding.Empty,
                Value = value,
            };
            // Applied on Enter or when leaving the box, never halfway through typing: "12" on the way to "120" s would
            // already change the regulation of a running bed.
            double applied = value;
            n.Committed += (o, e) => {
                if (_loading) return;
                if (!n.IsValid) {
                    n.Value = applied;
                    return;
                }
                applied = n.Value.Value;
                apply(applied);
                State.Settings.Save();
            };
            return n;
        }

        private static ThemedLabel Info(string text, Color? color = null) =>
            new(text, Theme.Body, color ?? Theme.Text) { AutoSize = true, MaximumSize = new Size(Theme.S(320), 0) };

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
            t.Controls.Add(new ThemedLabel(label, Theme.Body, Theme.TextDim) { Margin = new Padding(0, Theme.S(9), Theme.S(8), 0) }, 0, r);
            var cell = new FlowLayoutPanel {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, Theme.S(12)),
            };
            control.Margin = control is ThemedLabel ? new Padding(0, Theme.S(9), 0, 0) : Padding.Empty;
            cell.Controls.Add(control);
            if (!string.IsNullOrEmpty(hint))
                cell.Controls.Add(new ThemedLabel(hint, Theme.Small, Theme.TextFaint) { MaximumSize = new Size(Theme.S(320), 0), Margin = new Padding(0, Theme.S(4), 0, 0) });
            t.Controls.Add(cell, 1, r);
            t.RowCount = r + 1;
        }

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
    }
}
