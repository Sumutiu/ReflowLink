// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ReflowLink.UI {
    /// <summary>Dark theme palette, typography and DPI helpers.</summary>
    public static class Theme {
        // Surfaces
        public static readonly Color Window = Color.FromArgb(15, 17, 21);
        public static readonly Color Sidebar = Color.FromArgb(19, 22, 27);
        public static readonly Color Surface = Color.FromArgb(25, 29, 35);
        public static readonly Color SurfaceRaised = Color.FromArgb(32, 37, 45);
        public static readonly Color Input = Color.FromArgb(34, 39, 47);
        public static readonly Color InputHover = Color.FromArgb(40, 46, 56);
        public static readonly Color Border = Color.FromArgb(44, 50, 60);
        public static readonly Color BorderStrong = Color.FromArgb(62, 70, 82);
        public static readonly Color Grid = Color.FromArgb(34, 39, 47);

        // Text
        public static readonly Color Text = Color.FromArgb(230, 232, 235);
        public static readonly Color TextDim = Color.FromArgb(154, 163, 174);
        public static readonly Color TextFaint = Color.FromArgb(107, 116, 128);

        // Accents
        public static readonly Color Accent = Color.FromArgb(255, 112, 67);
        public static readonly Color AccentHover = Color.FromArgb(255, 134, 94);
        public static readonly Color AccentPressed = Color.FromArgb(230, 94, 52);
        public static readonly Color Blue = Color.FromArgb(78, 168, 255);
        public static readonly Color Green = Color.FromArgb(61, 220, 151);
        public static readonly Color Yellow = Color.FromArgb(245, 196, 81);
        public static readonly Color Red = Color.FromArgb(255, 92, 92);
        public static readonly Color Purple = Color.FromArgb(167, 139, 250);

        // Chart series
        public static readonly Color SeriesPv = Accent;
        public static readonly Color SeriesSp = Blue;
        public static readonly Color SeriesOp = Purple;
        public static readonly Color SeriesPlan = Color.FromArgb(150, 160, 172);

        private static float _scale = 1f;
        public static float Scale => _scale;

        public static void Initialize() {
            try {
                using var g = Graphics.FromHwnd(IntPtr.Zero); _scale = Math.Max(1f, g.DpiX / 96f);
            } catch { _scale = 1f; }
        }

        /// <summary>Scales a 96-DPI design pixel value to the current DPI.</summary>
        public static int S(float px) => (int)Math.Round(px * _scale);
        public static float Sf(float px) => px * _scale;
        public static Padding Pad(int all) => new(S(all));
        public static Padding Pad(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

        // Typography (point sizes scale with DPI automatically)
        private static readonly string UiFamily = PickFamily("Segoe UI", "Tahoma");
        private static readonly string UiSemibold = PickFamily("Segoe UI Semibold", "Segoe UI");
        private static readonly string UiLight = PickFamily("Segoe UI Light", "Segoe UI");
        private static readonly FontStyle SemiStyle = UiSemibold.IndexOf("Semibold", StringComparison.OrdinalIgnoreCase) >= 0 ? FontStyle.Regular : FontStyle.Bold;
        private static readonly string MonoFamily = PickFamily("Cascadia Mono", "Consolas", "Courier New");

        public static readonly Font Body = new(UiFamily, 9.75f);
        public static readonly Font Small = new(UiFamily, 8.5f);
        public static readonly Font SmallBold = new(UiSemibold, 8.25f, SemiStyle);
        public static readonly Font BodyBold = new(UiSemibold, 9.75f, SemiStyle);
        public static readonly Font Heading = new(UiSemibold, 11.5f, SemiStyle);
        public static readonly Font Title = new(UiSemibold, 17f, SemiStyle);
        public static readonly Font Big = new(UiLight, 26f);
        public static readonly Font Mono = new(MonoFamily, 9f);

        private static string PickFamily(params string[] names) {
            bool mono = Array.IndexOf(names, "Courier New") >= 0;
            foreach (var n in names) {
                try {
                    using var f = new Font(n, 10f);
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return n;
                } catch { }
            }
            return mono ? FontFamily.GenericMonospace.Name : SystemFonts.MessageBoxFont.FontFamily.Name;
        }

        public static Color Blend(Color a, Color b, float t) {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color WithAlpha(Color c, int alpha) => Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c.R, c.G, c.B);

        public static GraphicsPath Rounded(RectangleF r, float radius) => LogoPainter.RoundedRect(r, radius);

        public static void HighQuality(Graphics g) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        public static readonly TextFormatFlags LeftMiddle = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        public static readonly TextFormatFlags CenterMiddle = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        public static readonly TextFormatFlags RightMiddle = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        /// <summary>Owner-drawn dark tooltip.</summary>
        public static ToolTip CreateToolTip() {
            var t = new ToolTip { OwnerDraw = true, InitialDelay = 400, ReshowDelay = 100 };
            t.Popup += (s, e) => {
                string text = ((ToolTip)s).GetToolTip(e.AssociatedControl) ?? "";
                var sz = TextRenderer.MeasureText(text, Small);
                e.ToolTipSize = new Size(sz.Width + S(18), sz.Height + S(10));
            };
            t.Draw += (s, e) => {
                using (var b = new SolidBrush(SurfaceRaised)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var pen = new Pen(BorderStrong)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, Small, e.Bounds, Text, CenterMiddle);
            };
            return t;
        }

        // ------------------------------------------------------------------ native dark-mode helpers

        public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        /// <summary>Dark title bar (Windows 10 1809+ / 11) and a matching caption colour on Windows 11.</summary>
        public static void ApplyDarkTitleBar(Form f) {
            if (!IsWindows) return;
            try {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref on, 4); // pre-20H1 builds
                int caption = ColorTranslator.ToWin32(Sidebar);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4); // DWMWA_CAPTION_COLOR (Win11)
                int corners = 2;
                DwmSetWindowAttribute(f.Handle, 33, ref corners, 4); // rounded corners (Win11)
            } catch { }
        }

        /// <summary>Dark scrollbars for native controls (TextBox, RichTextBox, ListBox…).</summary>
        public static void ApplyDarkScrollbars(Control c) {
            if (!IsWindows) return;
            try {
                if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
                else c.HandleCreated += (s, e) => { try { SetWindowTheme(((Control)s).Handle, "DarkMode_Explorer", null); } catch { } };
            } catch { }
        }
    }
}
