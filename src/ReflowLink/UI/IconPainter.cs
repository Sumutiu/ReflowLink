// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ReflowLink.UI {
    public enum Glyph {
        None, Dashboard, Profile, Console, Settings, Play, Pause, Stop, Record, Download, Upload,
        Folder, Save, Plus, Refresh, Plug, Unplug, Image, Trash, Chevron, Check, Info, Warning, Copy, Send, Spark,
    }

    /// <summary>Resolution-independent line icons drawn with GDI+ (no icon font required).</summary>
    public static class IconPainter {
        public static void Draw(Graphics g, Glyph icon, RectangleF r, Color color) {
            if (icon == Glyph.None) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(r.Width, r.Height);
            float x = r.X + (r.Width - s) / 2, y = r.Y + (r.Height - s) / 2;
            PointF P(float px, float py) => new(x + px * s, y + py * s);
            RectangleF R(float l, float t, float w, float h) => new(x + l * s, y + t * s, w * s, h * s);

            using (var pen = new Pen(color, Math.Max(1.2f, s * 0.095f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var brush = new SolidBrush(color)) {
                switch (icon) {
                    case Glyph.Dashboard:
                        using (var p = Theme.Rounded(R(0.08f, 0.12f, 0.84f, 0.76f), s * 0.12f)) g.DrawPath(pen, p);
                        g.DrawLines(pen, [P(0.22f, 0.64f), P(0.38f, 0.46f), P(0.52f, 0.58f), P(0.78f, 0.32f)]);
                        break;
                    case Glyph.Profile:
                        g.DrawLines(pen, [P(0.08f, 0.82f), P(0.30f, 0.50f), P(0.48f, 0.50f), P(0.66f, 0.22f), P(0.92f, 0.22f)]);
                        g.DrawLine(pen, P(0.08f, 0.92f), P(0.92f, 0.92f));
                        break;
                    case Glyph.Console:
                        using (var p = Theme.Rounded(R(0.06f, 0.14f, 0.88f, 0.72f), s * 0.12f)) g.DrawPath(pen, p);
                        g.DrawLines(pen, [P(0.24f, 0.38f), P(0.38f, 0.50f), P(0.24f, 0.62f)]);
                        g.DrawLine(pen, P(0.48f, 0.64f), P(0.72f, 0.64f));
                        break;
                    case Glyph.Settings: {
                            var c = P(0.5f, 0.5f);
                            float ro = s * 0.40f, ri = s * 0.29f;
                            using (var gp = new GraphicsPath()) {
                                int teeth = 8;
                                var pts = new PointF[teeth * 4];
                                for (int i = 0; i < teeth; i++) {
                                    double a0 = (i / (double)teeth) * Math.PI * 2;
                                    double w = Math.PI * 2 / teeth;
                                    pts[i * 4 + 0] = Polar(c, ri, a0 - w * 0.30);
                                    pts[i * 4 + 1] = Polar(c, ro, a0 - w * 0.18);
                                    pts[i * 4 + 2] = Polar(c, ro, a0 + w * 0.18);
                                    pts[i * 4 + 3] = Polar(c, ri, a0 + w * 0.30);
                                }
                                gp.AddPolygon(pts);
                                g.DrawPath(pen, gp);
                            }
                            g.DrawEllipse(pen, c.X - s * 0.12f, c.Y - s * 0.12f, s * 0.24f, s * 0.24f);
                        }
                        break;
                    case Glyph.Play:
                        using (var gp = new GraphicsPath()) {
                            gp.AddPolygon([P(0.28f, 0.16f), P(0.84f, 0.50f), P(0.28f, 0.84f)]);
                            g.FillPath(brush, gp);
                            g.DrawPath(pen, gp);
                        }
                        break;
                    case Glyph.Pause:
                        using (var a = Theme.Rounded(R(0.22f, 0.16f, 0.18f, 0.68f), s * 0.05f)) g.FillPath(brush, a);
                        using (var b = Theme.Rounded(R(0.60f, 0.16f, 0.18f, 0.68f), s * 0.05f)) g.FillPath(brush, b);
                        break;
                    case Glyph.Stop:
                        using (var p = Theme.Rounded(R(0.18f, 0.18f, 0.64f, 0.64f), s * 0.10f)) g.FillPath(brush, p);
                        break;
                    case Glyph.Record:
                        g.FillEllipse(brush, R(0.2f, 0.2f, 0.6f, 0.6f));
                        break;
                    case Glyph.Download:
                        g.DrawLine(pen, P(0.5f, 0.10f), P(0.5f, 0.62f));
                        g.DrawLines(pen, [P(0.28f, 0.42f), P(0.5f, 0.64f), P(0.72f, 0.42f)]);
                        g.DrawLines(pen, [P(0.12f, 0.66f), P(0.12f, 0.88f), P(0.88f, 0.88f), P(0.88f, 0.66f)]);
                        break;
                    case Glyph.Upload:
                        g.DrawLine(pen, P(0.5f, 0.64f), P(0.5f, 0.12f));
                        g.DrawLines(pen, [P(0.28f, 0.34f), P(0.5f, 0.12f), P(0.72f, 0.34f)]);
                        g.DrawLines(pen, [P(0.12f, 0.66f), P(0.12f, 0.88f), P(0.88f, 0.88f), P(0.88f, 0.66f)]);
                        break;
                    case Glyph.Folder:
                        g.DrawLines(pen, [P(0.08f, 0.82f), P(0.08f, 0.20f), P(0.38f, 0.20f), P(0.48f, 0.32f), P(0.92f, 0.32f), P(0.92f, 0.82f), P(0.08f, 0.82f)]);
                        break;
                    case Glyph.Save:
                        using (var p = Theme.Rounded(R(0.12f, 0.12f, 0.76f, 0.76f), s * 0.10f)) g.DrawPath(pen, p);
                        g.DrawLines(pen, [P(0.30f, 0.12f), P(0.30f, 0.34f), P(0.66f, 0.34f), P(0.66f, 0.12f)]);
                        g.DrawRectangle(pen, x + 0.30f * s, y + 0.56f * s, 0.40f * s, 0.32f * s);
                        break;
                    case Glyph.Plus:
                        g.DrawLine(pen, P(0.5f, 0.16f), P(0.5f, 0.84f));
                        g.DrawLine(pen, P(0.16f, 0.5f), P(0.84f, 0.5f));
                        break;
                    case Glyph.Refresh:
                        g.DrawArc(pen, R(0.16f, 0.16f, 0.68f, 0.68f), -60, 290);
                        g.DrawLines(pen, [P(0.62f, 0.10f), P(0.70f, 0.24f), P(0.56f, 0.30f)]);
                        break;
                    case Glyph.Plug:
                        g.DrawLine(pen, P(0.38f, 0.10f), P(0.38f, 0.30f));
                        g.DrawLine(pen, P(0.62f, 0.10f), P(0.62f, 0.30f));
                        using (var p = Theme.Rounded(R(0.22f, 0.30f, 0.56f, 0.30f), s * 0.08f)) g.DrawPath(pen, p);
                        g.DrawLines(pen, [P(0.5f, 0.60f), P(0.5f, 0.76f), P(0.66f, 0.92f)]);
                        break;
                    case Glyph.Unplug:
                        g.DrawLine(pen, P(0.38f, 0.10f), P(0.38f, 0.30f));
                        g.DrawLine(pen, P(0.62f, 0.10f), P(0.62f, 0.30f));
                        using (var p = Theme.Rounded(R(0.22f, 0.30f, 0.56f, 0.30f), s * 0.08f)) g.DrawPath(pen, p);
                        g.DrawLine(pen, P(0.5f, 0.60f), P(0.5f, 0.76f));
                        g.DrawLine(pen, P(0.12f, 0.88f), P(0.88f, 0.12f));
                        break;
                    case Glyph.Image:
                        using (var p = Theme.Rounded(R(0.08f, 0.14f, 0.84f, 0.72f), s * 0.10f)) g.DrawPath(pen, p);
                        g.DrawLines(pen, [P(0.16f, 0.76f), P(0.40f, 0.50f), P(0.56f, 0.64f), P(0.68f, 0.54f), P(0.84f, 0.72f)]);
                        g.FillEllipse(brush, R(0.62f, 0.26f, 0.13f, 0.13f));
                        break;
                    case Glyph.Trash:
                        g.DrawLine(pen, P(0.14f, 0.24f), P(0.86f, 0.24f));
                        g.DrawLines(pen, [P(0.38f, 0.24f), P(0.40f, 0.12f), P(0.60f, 0.12f), P(0.62f, 0.24f)]);
                        g.DrawLines(pen, [P(0.24f, 0.24f), P(0.30f, 0.88f), P(0.70f, 0.88f), P(0.76f, 0.24f)]);
                        break;
                    case Glyph.Chevron:
                        g.DrawLines(pen, [P(0.22f, 0.38f), P(0.5f, 0.64f), P(0.78f, 0.38f)]);
                        break;
                    case Glyph.Check:
                        g.DrawLines(pen, [P(0.16f, 0.52f), P(0.40f, 0.76f), P(0.86f, 0.26f)]);
                        break;
                    case Glyph.Info:
                        g.DrawEllipse(pen, R(0.08f, 0.08f, 0.84f, 0.84f));
                        g.DrawLine(pen, P(0.5f, 0.46f), P(0.5f, 0.72f));
                        g.FillEllipse(brush, R(0.44f, 0.24f, 0.12f, 0.12f));
                        break;
                    case Glyph.Warning:
                        using (var gp = new GraphicsPath()) {
                            gp.AddPolygon([P(0.5f, 0.10f), P(0.92f, 0.86f), P(0.08f, 0.86f)]);
                            g.DrawPath(pen, gp);
                        }
                        g.DrawLine(pen, P(0.5f, 0.38f), P(0.5f, 0.60f));
                        g.FillEllipse(brush, R(0.445f, 0.68f, 0.11f, 0.11f));
                        break;
                    case Glyph.Copy:
                        using (var a = Theme.Rounded(R(0.30f, 0.30f, 0.56f, 0.58f), s * 0.08f)) g.DrawPath(pen, a);
                        g.DrawLines(pen, [P(0.16f, 0.66f), P(0.16f, 0.14f), P(0.60f, 0.14f)]);
                        break;
                    case Glyph.Send:
                        using (var gp = new GraphicsPath()) {
                            gp.AddPolygon([P(0.10f, 0.14f), P(0.90f, 0.50f), P(0.10f, 0.86f), P(0.24f, 0.50f)]);
                            g.DrawPath(pen, gp);
                        }
                        g.DrawLine(pen, P(0.24f, 0.50f), P(0.56f, 0.50f));
                        break;
                    case Glyph.Spark:
                        g.DrawLines(pen, [P(0.56f, 0.08f), P(0.26f, 0.54f), P(0.50f, 0.54f), P(0.42f, 0.92f), P(0.76f, 0.42f), P(0.52f, 0.42f), P(0.56f, 0.08f)]);
                        break;
                }
            }
            g.SmoothingMode = old;
        }

        private static PointF Polar(PointF c, float r, double a) =>
            new(c.X + (float)(Math.Cos(a) * r), c.Y + (float)(Math.Sin(a) * r));
    }
}
