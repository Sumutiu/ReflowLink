// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace ReflowLink.UI.Controls {
    public sealed class ChartSeries {
        public string Name;
        public Color Color;
        public float Width = 2f;
        public bool Dashed;
        public bool FillArea;
        /// <summary>Plot against the right-hand 0..100 % axis.</summary>
        public bool Secondary;
        public bool Visible = true;
        public bool ShowInLegend = true;
        public string Unit = "°C";
        public string Format = "0.0";
        /// <summary>Points must be added in increasing X order.</summary>
        public readonly List<PointD> Points = [];
    }

    public struct PointD(double x, double y) {
        public double X = x, Y = y;
    }

    public sealed class ChartMarker {
        public double X;
        public string Label;
        public Color Color = Theme.TextFaint;
    }

    /// <summary>Lightweight time/temperature chart with hover read-out.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class ChartView : Control {
        private Point? _mouse;

        public ChartView() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Font = Theme.Small;
        }

        public readonly List<ChartSeries> Series = [];
        public readonly List<ChartMarker> Markers = [];

        public double? XMin { get; set; }
        public double? XMax { get; set; }
        /// <summary>Minimum span of the X axis in seconds.</summary>
        public double MinXSpan { get; set; } = 60;
        public double YMinFloor { get; set; } = 0;
        public double YMaxMinimum { get; set; } = 100;
        /// <summary>Horizontal limit line (e.g. maximum temperature).</summary>
        public double? LimitLine { get; set; }
        public string LimitLabel { get; set; } = "";
        public bool ShowSecondaryAxis { get; set; } = true;
        public string EmptyText { get; set; } = "No data yet";
        public bool ShowLegend { get; set; } = true;
        public Func<double, string> XLabel { get; set; } = FormatTime;

        public static string FormatTime(double s) {
            if (s < 0) return "-" + FormatTime(-s);
            int t = (int)Math.Round(s);
            return t >= 3600 ? $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}" : $"{t / 60}:{t % 60:00}";
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); _mouse = e.Location; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _mouse = null; Invalidate(); }

        private void DataRange(out double xmin, out double xmax, out double ymin, out double ymax, out bool any) {
            xmin = double.MaxValue; xmax = double.MinValue; ymin = double.MaxValue; ymax = double.MinValue; any = false;
            foreach (var s in Series) {
                if (!s.Visible || s.Points.Count == 0) continue;
                any = true;
                xmin = Math.Min(xmin, s.Points[0].X);
                xmax = Math.Max(xmax, s.Points[s.Points.Count - 1].X);
            }
            if (!any) { xmin = 0; xmax = MinXSpan; }
            if (XMin.HasValue) xmin = XMin.Value;
            if (XMax.HasValue) xmax = XMax.Value;
            if (xmax - xmin < MinXSpan) xmax = xmin + MinXSpan;

            foreach (var s in Series) {
                if (!s.Visible || s.Secondary) continue;
                int i0 = LowerBound(s.Points, xmin);
                if (i0 > 0) i0--;
                for (int i = i0; i < s.Points.Count; i++) {
                    var p = s.Points[i];
                    if (p.X > xmax) { ymin = Math.Min(ymin, p.Y); ymax = Math.Max(ymax, p.Y); break; }
                    ymin = Math.Min(ymin, p.Y);
                    ymax = Math.Max(ymax, p.Y);
                }
            }
            if (ymin == double.MaxValue) { ymin = YMinFloor; ymax = YMaxMinimum; }
            ymin = Math.Min(ymin, YMinFloor);
            ymax = Math.Max(ymax * 1.06 + 2, YMaxMinimum);
            if (LimitLine.HasValue && ymax > LimitLine.Value * 0.8) ymax = Math.Max(ymax, LimitLine.Value * 1.04);
        }

        private static int LowerBound(List<PointD> pts, double x) {
            int lo = 0, hi = pts.Count;
            while (lo < hi) {
                int mid = (lo + hi) / 2;
                if (pts[mid].X < x) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private static double NiceStep(double range, int targetTicks) {
            double raw = range / Math.Max(1, targetTicks);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            double nice = n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10;
            return nice * mag;
        }

        private static double NiceTimeStep(double range, int targetTicks) {
            double raw = range / Math.Max(1, targetTicks);
            double[] steps = [5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
            foreach (var st in steps) if (st >= raw) return st;
            return 7200;
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.Clear(BackColor);
            Theme.HighQuality(g);

            DataRange(out double xmin, out double xmax, out double ymin, out double ymax, out bool any);
            double yStep = NiceStep(ymax - ymin, Math.Max(3, Height / Theme.S(60)));
            ymax = Math.Ceiling(ymax / yStep) * yStep;
            ymin = Math.Floor(ymin / yStep) * yStep;

            int legendH = ShowLegend ? Theme.S(26) : 0;
            int left = Theme.S(44), right = ShowSecondaryAxis ? Theme.S(42) : Theme.S(14), top = legendH + Theme.S(8), bottom = Theme.S(26);
            var plot = new Rectangle(left, top, Math.Max(10, Width - left - right), Math.Max(10, Height - top - bottom));

            float X(double x) => (float)(plot.Left + (x - xmin) / (xmax - xmin) * plot.Width);
            float Y(double y) => (float)(plot.Bottom - (y - ymin) / (ymax - ymin) * plot.Height);
            float Y2(double y) => (float)(plot.Bottom - y / 100.0 * plot.Height);

            // Grid + Y labels
            using (var grid = new Pen(Theme.Grid)) {
                for (double y = ymin; y <= ymax + 1e-9; y += yStep) {
                    float py = Y(y);
                    g.DrawLine(grid, plot.Left, py, plot.Right, py);
                    TextRenderer.DrawText(g, y.ToString("0", CultureInfo.InvariantCulture), Font,
                        new Rectangle(0, (int)py - Theme.S(9), left - Theme.S(8), Theme.S(18)), Theme.TextFaint, Theme.RightMiddle);
                }
                if (ShowSecondaryAxis)
                    for (int p = 0; p <= 100; p += 25)
                        TextRenderer.DrawText(g, p + "%", Font, new Rectangle(plot.Right + Theme.S(6), (int)Y2(p) - Theme.S(9), right - Theme.S(6), Theme.S(18)), Theme.WithAlpha(Theme.SeriesOp, 200), Theme.LeftMiddle);

                double xStep = NiceTimeStep(xmax - xmin, Math.Max(2, plot.Width / Theme.S(90)));
                double x0 = Math.Ceiling(xmin / xStep) * xStep;
                for (double x = x0; x <= xmax + 1e-9; x += xStep) {
                    float px = X(x);
                    g.DrawLine(grid, px, plot.Top, px, plot.Bottom);
                    TextRenderer.DrawText(g, XLabel(x), Font, new Rectangle((int)px - Theme.S(50), plot.Bottom + Theme.S(4), Theme.S(100), Theme.S(18)), Theme.TextFaint, Theme.CenterMiddle);
                }
            }

            // Limit line
            if (LimitLine.HasValue && LimitLine.Value <= ymax && LimitLine.Value >= ymin) {
                float ly = Y(LimitLine.Value);
                using (var pen = new Pen(Theme.WithAlpha(Theme.Red, 150), 1f) { DashStyle = DashStyle.Dash })
                    g.DrawLine(pen, plot.Left, ly, plot.Right, ly);
                if (!string.IsNullOrEmpty(LimitLabel))
                    TextRenderer.DrawText(g, LimitLabel, Font, new Rectangle(plot.Left + 4, (int)ly + Theme.S(2), plot.Width - 8, Theme.S(16)), Theme.WithAlpha(Theme.Red, 220), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }

            // Markers
            foreach (var m in Markers) {
                if (m.X < xmin || m.X > xmax) continue;
                float mx = X(m.X);
                using (var pen = new Pen(Theme.WithAlpha(m.Color, 120)) { DashStyle = DashStyle.Dot })
                    g.DrawLine(pen, mx, plot.Top, mx, plot.Bottom);
                if (!string.IsNullOrEmpty(m.Label))
                    TextRenderer.DrawText(g, m.Label, Font, new Point((int)mx + 3, plot.Top + 2), m.Color, TextFormatFlags.NoPadding);
            }

            // Series
            var oldClip = g.Clip;
            g.SetClip(new Rectangle(plot.Left, plot.Top - 2, plot.Width + 1, plot.Height + 3));
            foreach (var s in Series) {
                if (!s.Visible || s.Points.Count == 0) continue;
                var pts = Project(s, xmin, xmax, plot.Width, s.Secondary ? (Func<double, float>)Y2 : Y, X);
                if (pts.Count < 1) continue;
                if (pts.Count == 1) pts.Add(new PointF(pts[0].X + 0.5f, pts[0].Y));
                if (s.FillArea) {
                    var poly = new List<PointF>(pts) { new(pts[pts.Count - 1].X, plot.Bottom), new(pts[0].X, plot.Bottom) };
                    using var br = new LinearGradientBrush(new RectangleF(plot.Left, plot.Top, plot.Width, plot.Height + 1), Theme.WithAlpha(s.Color, 70), Theme.WithAlpha(s.Color, 6), 90f);
                    g.FillPolygon(br, poly.ToArray());
                }
                using var pen = new Pen(s.Color, Theme.Sf(s.Width)) { LineJoin = LineJoin.Round };
                if (s.Dashed) pen.DashPattern = [4f, 3f];
                g.DrawLines(pen, pts.ToArray());
            }
            g.Clip = oldClip;

            if (!any)
                TextRenderer.DrawText(g, EmptyText, Theme.Body, plot, Theme.TextFaint, Theme.CenterMiddle);

            // Legend
            if (ShowLegend) {
                int lx = left;
                for (int si = Series.Count - 1; si >= 0; si--) {
                    var s = Series[si];
                    if (!s.ShowInLegend) continue;
                    var c = s.Visible ? s.Color : Theme.TextFaint;
                    using (var pen = new Pen(c, Theme.Sf(2.5f)) { DashStyle = s.Dashed ? DashStyle.Dash : DashStyle.Solid })
                        g.DrawLine(pen, lx, Theme.S(13), lx + Theme.S(16), Theme.S(13));
                    var sz = TextRenderer.MeasureText(g, s.Name, Font);
                    TextRenderer.DrawText(g, s.Name, Font, new Point(lx + Theme.S(22), Theme.S(13) - sz.Height / 2), s.Visible ? Theme.TextDim : Theme.TextFaint, TextFormatFlags.NoPadding);
                    lx += Theme.S(34) + sz.Width;
                }
            }

            // Hover read-out
            if (_mouse.HasValue && plot.Contains(_mouse.Value) && any) {
                double hx = xmin + (_mouse.Value.X - plot.Left) / (double)plot.Width * (xmax - xmin);
                using (var pen = new Pen(Theme.WithAlpha(Theme.Text, 70)))
                    g.DrawLine(pen, _mouse.Value.X, plot.Top, _mouse.Value.X, plot.Bottom);

                var lines = new List<KeyValuePair<Color, string>> { new(Theme.TextDim, XLabel(hx)) };
                for (int si = Series.Count - 1; si >= 0; si--) {
                    var s = Series[si];
                    if (!s.Visible || s.Points.Count == 0 || !s.ShowInLegend) continue;
                    int i = LowerBound(s.Points, hx);
                    if (i >= s.Points.Count) i = s.Points.Count - 1;
                    if (i > 0 && Math.Abs(s.Points[i - 1].X - hx) < Math.Abs(s.Points[i].X - hx)) i--;
                    var p = s.Points[i];
                    if (Math.Abs(p.X - hx) > (xmax - xmin) * 0.05 && (hx < s.Points[0].X || hx > s.Points[s.Points.Count - 1].X)) continue;
                    double val = p.Y;
                    if (s.Dashed && i + 1 < s.Points.Count && hx >= p.X && s.Points[i + 1].X > p.X) {
                        var q = s.Points[i + 1]; // planned curves are piecewise linear: interpolate
                        val = p.Y + (q.Y - p.Y) * (hx - p.X) / (q.X - p.X);
                    } else if (s.Dashed && i > 0 && hx < p.X) {
                        var q = s.Points[i - 1];
                        val = q.Y + (p.Y - q.Y) * (hx - q.X) / (p.X - q.X);
                    }
                    float py = s.Secondary ? Y2(val) : Y(val);
                    using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, _mouse.Value.X - 3.5f, py - 3.5f, 7, 7);
                    lines.Add(new KeyValuePair<Color, string>(s.Color, $"{s.Name}  {val.ToString(s.Format, CultureInfo.InvariantCulture)} {s.Unit}"));
                }
                int lh = Theme.S(18), bw = Theme.S(10);
                foreach (var l in lines) bw = Math.Max(bw, TextRenderer.MeasureText(g, l.Value, Font).Width);
                bw += Theme.S(20);
                int bh = lh * lines.Count + Theme.S(12);
                int bx = _mouse.Value.X + Theme.S(14);
                if (bx + bw > plot.Right) bx = _mouse.Value.X - Theme.S(14) - bw;
                int by = Math.Max(plot.Top, Math.Min(plot.Bottom - bh, _mouse.Value.Y - bh / 2));
                using (var p = Theme.Rounded(new RectangleF(bx, by, bw, bh), Theme.Sf(6))) {
                    using (var b = new SolidBrush(Theme.WithAlpha(Theme.SurfaceRaised, 245))) g.FillPath(b, p);
                    using var pen = new Pen(Theme.BorderStrong); g.DrawPath(pen, p);
                }
                for (int i = 0; i < lines.Count; i++)
                    TextRenderer.DrawText(g, lines[i].Value, i == 0 ? Theme.SmallBold : Font,
                        new Rectangle(bx + Theme.S(10), by + Theme.S(6) + i * lh, bw - Theme.S(12), lh), i == 0 ? Theme.Text : Theme.Blend(lines[i].Key, Theme.Text, 0.35f), Theme.LeftMiddle);
            }
        }

        /// <summary>Projects visible points to screen space, decimating to min/max per pixel column.</summary>
        private static List<PointF> Project(ChartSeries s, double xmin, double xmax, int width, Func<double, float> y, Func<double, float> x) {
            var pts = s.Points;
            var res = new List<PointF>();
            int i0 = LowerBound(pts, xmin);
            if (i0 > 0) i0--;
            int i1 = LowerBound(pts, xmax);
            if (i1 < pts.Count) i1++;
            int n = i1 - i0;
            if (n <= 0) return res;
            if (n <= width * 2 || s.Dashed) {
                for (int i = i0; i < i1; i++) res.Add(new PointF(x(pts[i].X), y(pts[i].Y)));
                return res;
            }
            int col = int.MinValue;
            double mn = 0, mxv = 0, first = 0, last = 0;
            for (int i = i0; i < i1; i++) {
                int c = (int)x(pts[i].X);
                if (c != col) {
                    if (col != int.MinValue) Flush(res, col, first, mn, mxv, last, y);
                    col = c; mn = mxv = first = last = pts[i].Y;
                } else {
                    if (pts[i].Y < mn) mn = pts[i].Y;
                    if (pts[i].Y > mxv) mxv = pts[i].Y;
                    last = pts[i].Y;
                }
            }
            if (col != int.MinValue) Flush(res, col, first, mn, mxv, last, y);
            return res;
        }

        private static void Flush(List<PointF> res, int col, double first, double mn, double mx, double last, Func<double, float> y) {
            res.Add(new PointF(col, y(first)));
            if (mn != first && mn != last) res.Add(new PointF(col, y(mn)));
            if (mx != first && mx != last) res.Add(new PointF(col, y(mx)));
            if (last != first) res.Add(new PointF(col, y(last)));
        }

        /// <summary>Renders the chart into a bitmap (for PNG export).</summary>
        public Bitmap Snapshot() {
            var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            var saved = _mouse;
            _mouse = null;
            using (var g = Graphics.FromImage(bmp))
                OnPaint(new PaintEventArgs(g, new Rectangle(0, 0, Width, Height)));
            _mouse = saved;
            return bmp;
        }
    }
}
