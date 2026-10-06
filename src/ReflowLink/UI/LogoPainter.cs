// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System.Drawing;
using System.Drawing.Drawing2D;

namespace ReflowLink.UI {
    /// <summary>Vector app logo: heat waves rising above a chip. Used for the sidebar and to generate app.ico.</summary>
    public static class LogoPainter {
        public static void Draw(Graphics g, RectangleF r) {
            var oldSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = r.Width;

            using (var path = RoundedRect(r, s * 0.24f))
            using (var bg = new LinearGradientBrush(r, Color.FromArgb(255, 255, 140, 66), Color.FromArgb(255, 226, 54, 44), 45f))
                g.FillPath(bg, path);

            // Chip body
            var chip = new RectangleF(r.X + s * 0.24f, r.Y + s * 0.60f, s * 0.52f, s * 0.20f);
            using (var chipPath = RoundedRect(chip, s * 0.05f))
            using (var white = new SolidBrush(Color.White))
                g.FillPath(white, chipPath);

            // Pins
            using (var pin = new SolidBrush(Color.FromArgb(235, 255, 255, 255))) {
                float pw = s * 0.055f, ph = s * 0.07f;
                for (int i = 0; i < 4; i++) {
                    float x = chip.X + chip.Width * (0.14f + i * 0.24f) - pw / 2 + s * 0.012f;
                    g.FillRectangle(pin, x, chip.Bottom, pw, ph);
                }
            }

            // Heat waves
            using (var pen = new Pen(Color.White, s * 0.065f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                for (int i = 0; i < 3; i++) {
                    float cx = r.X + s * (0.33f + i * 0.17f);
                    float top = r.Y + s * 0.17f, bottom = r.Y + s * 0.50f;
                    float a = s * 0.055f;
                    float h = (bottom - top) / 2f;
                    g.DrawBezier(pen,
                        new PointF(cx, bottom), new PointF(cx + a, bottom - h * 0.5f),
                        new PointF(cx + a, bottom - h * 0.5f), new PointF(cx, bottom - h));
                    g.DrawBezier(pen,
                        new PointF(cx, bottom - h), new PointF(cx - a, top + h * 0.5f),
                        new PointF(cx - a, top + h * 0.5f), new PointF(cx, top));
                }
            }
            g.SmoothingMode = oldSmooth;
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius) {
            var p = new GraphicsPath();
            float d = radius * 2;
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
