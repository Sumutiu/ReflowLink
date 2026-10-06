// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ReflowLink.UI.Controls {
    /// <summary>Base for owner-drawn controls: double buffered, paints the parent background behind rounded shapes.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public abstract class ThemedControl : Control {
        protected ThemedControl() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            ForeColor = Theme.Text;
            Font = Theme.Body;
        }

        protected Color ParentBack {
            get {
                for (Control c = Parent; c != null; c = c.Parent)
                    if (c.BackColor.A == 255) return c.BackColor;
                return Theme.Window;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) {
            e.Graphics.Clear(ParentBack);
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    }

    // =====================================================================================

    public enum ButtonKind { Primary, Secondary, Danger, Success, Ghost }

    [System.ComponentModel.DesignerCategory("Code")]
    public class ModernButton : ThemedControl, IButtonControl {
        private bool _hover, _down;
        private ButtonKind _kind = ButtonKind.Secondary;
        private Glyph _glyph;

        public ModernButton() {
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            Font = Theme.BodyBold;
            Height = Theme.S(34);
            TabStop = true;
        }

        public ModernButton(string text, Glyph glyph = Glyph.None, ButtonKind kind = ButtonKind.Secondary) : this() {
            _glyph = glyph;
            _kind = kind;
            Text = text;
            FitWidth();
        }

        [DefaultValue(ButtonKind.Secondary)]
        public ButtonKind Kind { get => _kind; set { _kind = value; Invalidate(); } }

        public Glyph Glyph { get => _glyph; set { _glyph = value; FitWidth(); Invalidate(); } }

        /// <summary>When true (default) the width follows the content.</summary>
        public bool AutoWidth { get; set; } = true;

        public string ToolTipText { get; set; }

        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitWidth(); }

        public void FitWidth() {
            if (!AutoWidth) return;
            Width = GetPreferredSize(Size.Empty).Width;
        }

        public override Size GetPreferredSize(Size proposedSize) {
            int pad = Theme.S(string.IsNullOrEmpty(Text) ? 9 : 14);
            int icon = _glyph != Glyph.None ? Theme.S(16) : 0;
            int gap = _glyph != Glyph.None && !string.IsNullOrEmpty(Text) ? Theme.S(8) : 0;
            int tw = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            return new Size(pad * 2 + icon + gap + tw, Height);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnKeyUp(KeyEventArgs e) {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) { PerformClick(); e.Handled = true; }
        }

        private void Colors(out Color bg, out Color fg, out Color border) {
            border = Color.Empty;
            if (!Enabled) {
                bg = _kind == ButtonKind.Ghost ? Color.Transparent : Theme.Blend(Theme.Input, Theme.Surface, 0.4f);
                fg = Theme.TextFaint;
                return;
            }
            switch (_kind) {
                case ButtonKind.Primary:
                    bg = _down ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent; fg = Theme.Window; break;
                case ButtonKind.Success:
                    bg = _down ? Theme.Blend(Theme.Green, Color.Black, 0.12f) : _hover ? Theme.Blend(Theme.Green, Color.White, 0.12f) : Theme.Green; fg = Theme.Window; break;
                case ButtonKind.Danger:
                    bg = _down ? Theme.Blend(Theme.Red, Color.Black, 0.12f) : _hover ? Theme.Blend(Theme.Red, Color.White, 0.12f) : Theme.Red; fg = Theme.Window; break;
                case ButtonKind.Ghost:
                    bg = _down ? Theme.InputHover : _hover ? Theme.Input : Color.Transparent; fg = _hover ? Theme.Text : Theme.TextDim; break;
                default:
                    bg = _down ? Theme.Border : _hover ? Theme.InputHover : Theme.Input; fg = Theme.Text; border = Theme.Border; break;
            }
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Theme.HighQuality(g);
            Colors(out var bg, out var fg, out var border);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = Theme.Sf(7);
            using (var p = Theme.Rounded(r, radius)) {
                if (bg.A > 0) using (var b = new SolidBrush(bg)) g.FillPath(b, p);
                if (border != Color.Empty) using (var pen = new Pen(border)) g.DrawPath(pen, p);
                if (Focused && ShowFocusCues)
                    using (var pen = new Pen(Theme.WithAlpha(Theme.Accent, 200), Theme.Sf(1.5f))) g.DrawPath(pen, p);
            }

            int icon = _glyph != Glyph.None ? Theme.S(16) : 0;
            int gap = icon > 0 && !string.IsNullOrEmpty(Text) ? Theme.S(8) : 0;
            int tw = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int total = icon + gap + tw;
            int x = (Width - total) / 2;
            if (icon > 0)
                IconPainter.Draw(g, _glyph, new RectangleF(x, (Height - icon) / 2f, icon, icon), fg);
            if (tw > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x + icon + gap, 0, tw + 2, Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    // =====================================================================================

    /// <summary>Rounded surface that hosts content.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class CardPanel : Panel {
        public CardPanel() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Padding = Theme.Pad(16);
        }

        public bool ShowBorder { get; set; } = true;

        // Windows scrolls by copying pixels; repaint so the rounded border does not smear.
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(true); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); if (AutoScroll) Invalidate(true); }

        protected override void OnPaintBackground(PaintEventArgs e) {
            var g = e.Graphics;
            Color parent = Theme.Window;
            for (Control c = Parent; c != null; c = c.Parent)
                if (c.BackColor.A == 255) { parent = c.BackColor; break; }
            g.Clear(parent);
            Theme.HighQuality(g);
            using var p = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.Sf(10));
            using (var b = new SolidBrush(BackColor)) g.FillPath(b, p);
            if (ShowBorder) using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
        }
    }

    /// <summary>Title row for a card, with optional subtitle and right-aligned tools.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class CardHeader : Panel {
        private string _title = "", _subtitle = "";

        public CardHeader(string title, string subtitle = "") {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _title = title;
            _subtitle = subtitle;
            Dock = DockStyle.Top;
            Height = Theme.S(subtitle.Length > 0 ? 50 : 40);
            Tools = new FlowLayoutPanel {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 0, 0, Theme.S(6)),
                BackColor = Color.Transparent,
            };
            Controls.Add(Tools);
        }

        public FlowLayoutPanel Tools { get; }

        public string Title { get => _title; set { _title = value ?? ""; Invalidate(); } }
        public string Subtitle { get => _subtitle; set { _subtitle = value ?? ""; Invalidate(); } }

        protected override void OnParentChanged(EventArgs e) {
            base.OnParentChanged(e);
            if (Parent != null) BackColor = Parent.BackColor;
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            int titleH = Theme.Heading.Height;
            int y = _subtitle.Length > 0 ? 0 : (Height - Theme.S(6) - titleH) / 2;
            TextRenderer.DrawText(g, _title, Theme.Heading, new Rectangle(0, y, Width - Tools.Width, titleH + 2), Theme.Text, Theme.LeftMiddle);
            if (_subtitle.Length > 0)
                TextRenderer.DrawText(g, _subtitle, Theme.Small, new Rectangle(0, y + titleH + Theme.S(3), Width - Tools.Width, Theme.Small.Height + 2), Theme.TextDim, Theme.LeftMiddle);
        }
    }

    // =====================================================================================

    [System.ComponentModel.DesignerCategory("Code")]
    public class NavButton : ThemedControl {
        private bool _hover, _selected;

        public NavButton(string text, Glyph glyph) {
            Text = text;
            Glyph = glyph;
            Height = Theme.S(42);
            Cursor = Cursors.Hand;
            Font = Theme.Body;
        }

        public Glyph Glyph { get; }
        public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Theme.HighQuality(g);
            var r = new RectangleF(Theme.S(10), 1, Width - Theme.S(20), Height - 2);
            if (_selected || _hover)
                using (var p = Theme.Rounded(r, Theme.Sf(8)))
                using (var b = new SolidBrush(_selected ? Theme.SurfaceRaised : Theme.Surface))
                    g.FillPath(b, p);
            if (_selected)
                using (var p = Theme.Rounded(new RectangleF(r.X, r.Y + r.Height * 0.28f, Theme.Sf(3), r.Height * 0.44f), Theme.Sf(1.5f)))
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillPath(b, p);
            var fg = _selected ? Theme.Text : _hover ? Theme.Text : Theme.TextDim;
            int icon = Theme.S(18);
            IconPainter.Draw(g, Glyph, new RectangleF(r.X + Theme.S(14), (Height - icon) / 2f, icon, icon), _selected ? Theme.Accent : fg);
            TextRenderer.DrawText(g, Text, _selected ? Theme.BodyBold : Theme.Body,
                new Rectangle((int)r.X + Theme.S(44), 0, (int)r.Width - Theme.S(48), Height), fg, Theme.LeftMiddle);
        }
    }

    // =====================================================================================

    [System.ComponentModel.DesignerCategory("Code")]
    public class ToggleSwitch : ThemedControl {
        private bool _checked, _hover;

        public ToggleSwitch(string text = "") {
            SetStyle(ControlStyles.Selectable, true);
            Text = text;
            Cursor = Cursors.Hand;
            Height = Theme.S(28);
            TabStop = true;
            FitWidth();
        }

        public event EventHandler CheckedChanged;

        public bool Checked {
            get => _checked;
            set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitWidth(); }

        private void FitWidth() {
            int tw = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font).Width + Theme.S(6);
            Width = Theme.S(40) + tw;
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Checked = !Checked; Focus(); }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode == Keys.Space) Checked = !Checked; }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Theme.HighQuality(g);
            float tw = Theme.Sf(36), th = Theme.Sf(20);
            var track = new RectangleF(0.5f, (Height - th) / 2f, tw, th);
            Color trackColor = !Enabled ? (_checked ? Theme.Blend(Theme.Accent, Theme.Surface, 0.45f) : Theme.Input) : _checked ? (_hover ? Theme.AccentHover : Theme.Accent) : (_hover ? Theme.BorderStrong : Theme.Border);
            using (var p = Theme.Rounded(track, th / 2)) {
                using (var b = new SolidBrush(trackColor)) g.FillPath(b, p);
                if (Focused && ShowFocusCues) using (var pen = new Pen(Theme.TextDim)) g.DrawPath(pen, p);
            }
            float k = th - Theme.Sf(6);
            float kx = _checked ? track.Right - k - Theme.Sf(3) : track.X + Theme.Sf(3);
            using (var b = new SolidBrush(!Enabled ? (_checked ? Theme.Blend(Theme.Window, Theme.Surface, 0.3f) : Theme.TextFaint) : _checked ? Theme.Window : Theme.Text))
                g.FillEllipse(b, kx, track.Y + Theme.Sf(3), k, k);
            if (!string.IsNullOrEmpty(Text))
                TextRenderer.DrawText(g, Text, Font, new Rectangle(Theme.S(46), 0, Width - Theme.S(46), Height), Enabled ? Theme.Text : Theme.TextFaint, Theme.LeftMiddle);
        }
    }

    // =====================================================================================

    [System.ComponentModel.DesignerCategory("Code")]
    public class StatusPill : ThemedControl {
        private Color _color = Theme.TextFaint;
        private bool _pulse;
        private float _phase;
        private readonly Timer _timer;

        public StatusPill() {
            Height = Theme.S(26);
            Font = Theme.SmallBold;
            _timer = new Timer { Interval = 60 };
            _timer.Tick += (s, e) => { _phase += 0.12f; Invalidate(); };
        }

        public Color DotColor { get => _color; set { _color = value; Invalidate(); } }

        public bool Pulse {
            get => _pulse;
            set { _pulse = value; if (value) _timer.Start(); else { _timer.Stop(); _phase = 0; } Invalidate(); }
        }

        public void Set(string text, Color color, bool pulse = false) {
            Text = text;
            DotColor = color;
            Pulse = pulse;
            Width = TextRenderer.MeasureText(text, Font).Width + Theme.S(32);
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Theme.HighQuality(g);
            using (var p = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), (Height - 1) / 2f))
            using (var b = new SolidBrush(Theme.WithAlpha(_color, 34)))
                g.FillPath(b, p);
            float d = Theme.Sf(8);
            float cx = Theme.Sf(12), cy = Height / 2f;
            if (_pulse) {
                float t = (float)((Math.Sin(_phase) + 1) / 2);
                float halo = d + Theme.Sf(6) * t;
                using var b = new SolidBrush(Theme.WithAlpha(_color, (int)(90 * (1 - t))));
                g.FillEllipse(b, cx - halo / 2, cy - halo / 2, halo, halo);
            }
            using (var b = new SolidBrush(_color)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Theme.S(22), 0, Width - Theme.S(24), Height), Theme.Text, Theme.LeftMiddle);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }

    // =====================================================================================

    /// <summary>Large numeric read-out tile.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class StatCard : ThemedControl {
        private string _caption = "", _value = "—", _unit = "", _detail = "";
        private Color _accent = Theme.Accent;
        private double? _bar;

        public StatCard(string caption, string unit, Color accent) {
            _caption = caption;
            _unit = unit;
            _accent = accent;
            Height = Theme.S(118);
        }

        public string Caption { get => _caption; set { _caption = value; Invalidate(); } }
        public string Value { get => _value; set { if (_value == value) return; _value = value; Invalidate(); } }
        public string Unit { get => _unit; set { _unit = value; Invalidate(); } }
        public string Detail { get => _detail; set { if (_detail == value) return; _detail = value; Invalidate(); } }
        public Color Accent { get => _accent; set { _accent = value; Invalidate(); } }
        /// <summary>Optional 0..1 bar under the value (e.g. output power).</summary>
        public double? Bar { get => _bar; set { _bar = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Theme.HighQuality(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var p = Theme.Rounded(r, Theme.Sf(10))) {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                using var pen = new Pen(Theme.Border); g.DrawPath(pen, p);
            }
            int pad = Theme.S(16);
            float dot = Theme.Sf(8);
            using (var b = new SolidBrush(_accent)) g.FillEllipse(b, pad, pad + Theme.S(4), dot, dot);
            TextRenderer.DrawText(g, _caption.ToUpperInvariant(), Theme.SmallBold, new Rectangle(pad + Theme.S(14), pad - 1, Width - pad * 2, Theme.S(18)), Theme.TextDim, Theme.LeftMiddle);

            int vy = pad + Theme.S(22);
            var vs = TextRenderer.MeasureText(g, _value, Theme.Big, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _value, Theme.Big, new Point(pad - Theme.S(2), vy), Theme.Text, TextFormatFlags.NoPadding);
            if (!string.IsNullOrEmpty(_unit)) {
                int uy = vy + vs.Height - Theme.Heading.Height - Theme.S(4);
                TextRenderer.DrawText(g, _unit, Theme.Heading, new Point(pad + vs.Width + Theme.S(4), uy), Theme.TextDim, TextFormatFlags.NoPadding);
            }

            int by = Height - pad - Theme.S(16);
            if (_bar.HasValue) {
                var track = new RectangleF(pad, by - Theme.S(8), Width - pad * 2, Theme.Sf(5));
                using (var p = Theme.Rounded(track, track.Height / 2)) using (var b = new SolidBrush(Theme.Input)) g.FillPath(b, p);
                float w = (float)(Math.Max(0, Math.Min(1, _bar.Value)) * track.Width);
                if (w > 1)
                    using (var p = Theme.Rounded(new RectangleF(track.X, track.Y, Math.Max(w, track.Height), track.Height), track.Height / 2))
                    using (var b = new SolidBrush(_accent)) g.FillPath(b, p);
                by += Theme.S(3);
            }
            if (!string.IsNullOrEmpty(_detail))
                TextRenderer.DrawText(g, _detail, Theme.Small, new Rectangle(pad, by, Width - pad * 2, Theme.S(18)), Theme.TextDim, Theme.LeftMiddle);
        }
    }

    // =====================================================================================

    [System.ComponentModel.DesignerCategory("Code")]
    public class ModernProgressBar : ThemedControl {
        private double _value;
        private bool _indeterminate;
        private float _phase;
        private readonly Timer _timer = new() { Interval = 30 };

        public ModernProgressBar() {
            Height = Theme.S(4);
            _timer.Tick += (s, e) => { _phase = (_phase + 0.02f) % 1.4f; Invalidate(); };
        }

        public double Value { get => _value; set { _value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

        public bool Indeterminate {
            get => _indeterminate;
            set { _indeterminate = value; if (value) _timer.Start(); else _timer.Stop(); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Theme.HighQuality(g);
            var r = new RectangleF(0, 0, Width, Height);
            using (var p = Theme.Rounded(r, Height / 2f)) using (var b = new SolidBrush(Theme.Input)) g.FillPath(b, p);
            RectangleF fill;
            if (_indeterminate) {
                float w = Width * 0.3f;
                float x = (_phase - 0.3f) * Width;
                fill = RectangleF.Intersect(new RectangleF(x, 0, w, Height), r);
            } else fill = new RectangleF(0, 0, (float)(Width * _value), Height);
            if (fill.Width > 1)
                using (var p = Theme.Rounded(fill, Height / 2f)) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }

    // =====================================================================================

    /// <summary>Plain themed text label with optional wrapping.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class ThemedLabel : Label {
        public ThemedLabel(string text, Font font = null, Color? color = null) {
            Text = text;
            Font = font ?? Theme.Body;
            ForeColor = color ?? Theme.Text;
            BackColor = Color.Transparent;
            AutoSize = true;
            UseMnemonic = false;
        }
    }

    /// <summary>Thin horizontal divider.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class Divider : Control {
        public Divider() {
            Height = Theme.S(17);
            Dock = DockStyle.Top;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e) {
            Color back = Theme.Surface;
            for (Control c = Parent; c != null; c = c.Parent)
                if (c.BackColor.A == 255) { back = c.BackColor; break; }
            e.Graphics.Clear(back);
            using var pen = new Pen(Theme.Border); e.Graphics.DrawLine(pen, 0, Height / 2, Width, Height / 2);
        }
    }

    // =====================================================================================

    public sealed class DarkColorTable : ProfessionalColorTable {
        public override Color ToolStripDropDownBackground => Theme.SurfaceRaised;
        public override Color ImageMarginGradientBegin => Theme.SurfaceRaised;
        public override Color ImageMarginGradientMiddle => Theme.SurfaceRaised;
        public override Color ImageMarginGradientEnd => Theme.SurfaceRaised;
        public override Color MenuBorder => Theme.BorderStrong;
        public override Color MenuItemBorder => Theme.InputHover;
        public override Color MenuItemSelected => Theme.InputHover;
        public override Color MenuItemSelectedGradientBegin => Theme.InputHover;
        public override Color MenuItemSelectedGradientEnd => Theme.InputHover;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }

    public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer {
        public DarkMenuRenderer() : base(new DarkColorTable()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextFaint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            Theme.HighQuality(g);
            var r = new RectangleF(Theme.S(4), 1, e.Item.Width - Theme.S(8), e.Item.Height - 2);
            using var p = Theme.Rounded(r, Theme.Sf(5)); using var b = new SolidBrush(Theme.InputHover); g.FillPath(b, p);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) {
            var r = e.ImageRectangle;
            IconPainter.Draw(e.Graphics, Glyph.Check, new RectangleF(r.X, r.Y, r.Width, r.Height), Theme.Accent);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) {
            e.ArrowColor = Theme.TextDim;
            base.OnRenderArrow(e);
        }
    }

    // =====================================================================================

    /// <summary>Themed drop-down list (replacement for ComboBox).</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class ModernDropDown : ThemedControl {
        private readonly System.Collections.Generic.List<string> _items = [];
        private int _index = -1;
        private bool _hover, _open;

        public ModernDropDown() {
            SetStyle(ControlStyles.Selectable, true);
            Height = Theme.S(34);
            Width = Theme.S(180);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public event EventHandler SelectedIndexChanged;

        public System.Collections.Generic.IReadOnlyList<string> Items => _items;

        public void SetItems(System.Collections.Generic.IEnumerable<string> items, int selected = 0) {
            _items.Clear();
            _items.AddRange(items);
            _index = _items.Count == 0 ? -1 : Math.Max(0, Math.Min(_items.Count - 1, selected));
            Invalidate();
        }

        public void SetItemText(int index, string text) {
            if (index < 0 || index >= _items.Count) return;
            _items[index] = text;
            Invalidate();
        }

        public int SelectedIndex {
            get => _index;
            set {
                int v = _items.Count == 0 ? -1 : Math.Max(-1, Math.Min(_items.Count - 1, value));
                if (v == _index) return;
                _index = v;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string SelectedItem => _index >= 0 && _index < _items.Count ? _items[_index] : null;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && Enabled) { Focus(); ShowMenu(); }
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e) {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Down) { SelectedIndex = Math.Min(_items.Count - 1, _index + 1); e.Handled = true; } else if (e.KeyCode == Keys.Up) { SelectedIndex = Math.Max(0, _index - 1); e.Handled = true; } else if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || (e.Alt && e.KeyCode == Keys.Down)) { ShowMenu(); e.Handled = true; }
        }

        private void ShowMenu() {
            if (_items.Count == 0) return;
            var menu = new ContextMenuStrip {
                Renderer = new DarkMenuRenderer(),
                ShowImageMargin = false,
                ShowCheckMargin = true,
                Font = Theme.Body,
                BackColor = Theme.SurfaceRaised,
                ForeColor = Theme.Text,
            };
            for (int i = 0; i < _items.Count; i++) {
                int idx = i;
                var it = new ToolStripMenuItem(_items[i]) { Checked = i == _index, Padding = new Padding(0, Theme.S(3), 0, Theme.S(3)) };
                it.Click += (s, e) => SelectedIndex = idx;
                menu.Items.Add(it);
            }
            menu.MinimumSize = new Size(Width, 0);
            menu.Closed += (s, e) => { _open = false; Invalidate(); BeginInvoke(new Action(menu.Dispose)); };
            _open = true;
            Invalidate();
            menu.Show(this, new Point(0, Height + Theme.S(2)));
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Theme.HighQuality(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var p = Theme.Rounded(r, Theme.Sf(7))) {
                using (var b = new SolidBrush(!Enabled ? Theme.Surface : (_hover || _open) ? Theme.InputHover : Theme.Input)) g.FillPath(b, p);
                using var pen = new Pen(Focused || _open ? Theme.Accent : Theme.Border); g.DrawPath(pen, p);
            }
            int pad = Theme.S(11);
            int chev = Theme.S(12);
            TextRenderer.DrawText(g, SelectedItem ?? "", Font, new Rectangle(pad, 0, Width - pad * 2 - chev - Theme.S(4), Height), Enabled ? Theme.Text : Theme.TextFaint, Theme.LeftMiddle);
            IconPainter.Draw(g, Glyph.Chevron, new RectangleF(Width - pad - chev, (Height - chev) / 2f, chev, chev), Theme.TextDim);
        }
    }

    // =====================================================================================

    /// <summary>Rounded text input with optional unit suffix and validation state.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class ModernTextBox : ThemedControl {
        protected readonly TextBox Inner;
        private string _suffix = "";
        private bool _invalid, _hover;

        public ModernTextBox() {
            Height = Theme.S(34);
            Width = Theme.S(160);
            Inner = new TextBox {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Input,
                ForeColor = Theme.Text,
                Font = Theme.Body,
            };
            Inner.TextChanged += (s, e) => OnTextChanged(e);
            Inner.GotFocus += (s, e) => Invalidate();
            Inner.LostFocus += (s, e) => { Invalidate(); OnCommitted(); };
            Inner.KeyDown += InnerKeyDown;
            Inner.MouseEnter += (s, e) => { _hover = true; Invalidate(); };
            Inner.MouseLeave += (s, e) => { _hover = ClientRectangle.Contains(PointToClient(MousePosition)); Invalidate(); };
            Controls.Add(Inner);
            Cursor = Cursors.IBeam;
        }

        public event EventHandler Committed;

        public override string Text { get => Inner?.Text ?? ""; set { Inner?.Text = value ?? ""; } }

        public string Suffix { get => _suffix; set { _suffix = value ?? ""; LayoutInner(); Invalidate(); } }

        public bool Invalid { get => _invalid; set { if (_invalid == value) return; _invalid = value; Invalidate(); } }

        public bool ReadOnly { get => Inner.ReadOnly; set => Inner.ReadOnly = value; }

        public HorizontalAlignment TextAlign { get => Inner.TextAlign; set => Inner.TextAlign = value; }

        public TextBox InnerTextBox => Inner;

        public string Placeholder {
            set {
                if (!Theme.IsWindows) return;
                void apply() { try { SendMessage(Inner.Handle, 0x1501, (IntPtr)1, value ?? ""); } catch { } }
                if (Inner.IsHandleCreated) apply(); else Inner.HandleCreated += (s, e) => apply();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        protected virtual void InnerKeyDown(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Enter) { OnCommitted(); e.SuppressKeyPress = true; }
        }

        protected virtual void OnCommitted() => Committed?.Invoke(this, EventArgs.Empty);

        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (Inner == null) return; Inner.Font = Font; LayoutInner(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutInner(); }
        protected override void OnEnabledChanged(EventArgs e) {
            base.OnEnabledChanged(e);
            if (Inner == null) return;
            Inner.BackColor = Enabled ? Theme.Input : Theme.Surface;
            Inner.ForeColor = Enabled ? Theme.Text : Theme.TextFaint;
        }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Inner.Focus(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Inner.Focus(); }

        private void LayoutInner() {
            if (Inner == null) return;
            int pad = Theme.S(10);
            int sfx = string.IsNullOrEmpty(_suffix) ? 0 : TextRenderer.MeasureText(_suffix, Theme.Small).Width + Theme.S(4);
            int h = Inner.PreferredHeight;
            Inner.SetBounds(pad, Math.Max(1, (Height - h) / 2 + 1), Math.Max(10, Width - pad * 2 - sfx), h);
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Theme.HighQuality(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color border = _invalid ? Theme.Red : Inner != null && Inner.Focused ? Theme.Accent : _hover ? Theme.BorderStrong : Theme.Border;
            using (var p = Theme.Rounded(r, Theme.Sf(7))) {
                using (var b = new SolidBrush(Enabled ? Theme.Input : Theme.Surface)) g.FillPath(b, p);
                using var pen = new Pen(border, Inner.Focused || _invalid ? Theme.Sf(1.4f) : 1f); g.DrawPath(pen, p);
            }
            if (!string.IsNullOrEmpty(_suffix))
                TextRenderer.DrawText(g, _suffix, Theme.Small, new Rectangle(0, 0, Width - Theme.S(10), Height), Theme.TextFaint, Theme.RightMiddle);
        }
    }

    /// <summary>Numeric input accepting '.' or ',' as decimal separator; Up/Down arrows step the value.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class NumberBox : ModernTextBox {
        private bool _setting;

        public NumberBox(double min, double max, int decimals, double step = 1) {
            Minimum = min;
            Maximum = max;
            Decimals = decimals;
            Step = step;
            TextAlign = HorizontalAlignment.Right;
            Width = Theme.S(110);
        }

        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public int Decimals { get; set; }
        public double Step { get; set; }

        public event EventHandler ValueChanged;

        public double? Value {
            get => TryParse(Text, out double v) ? v : (double?)null;
            set {
                _setting = true;
                Text = value.HasValue ? Format(value.Value) : "";
                _setting = false;
                Validate();
            }
        }

        public bool IsValid => TryParse(Text, out double v) && v >= Minimum - 1e-9 && v <= Maximum + 1e-9;

        public string Format(double v) =>
            v.ToString(Decimals <= 0 ? "0" : "0." + new string('#', Decimals), System.Globalization.CultureInfo.InvariantCulture);

        public static bool TryParse(string s, out double v) {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Replace(',', '.');
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        private void Validate() => Invalid = !IsValid;

        protected override void OnTextChanged(EventArgs e) {
            base.OnTextChanged(e);
            Validate();
            if (!_setting) ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnCommitted() {
            if (TryParse(Text, out double v)) {
                v = Math.Round(v, Math.Max(0, Decimals));
                string f = Format(v);
                if (f != Text) Text = f;
            }
            base.OnCommitted();
        }

        protected override void InnerKeyDown(object sender, KeyEventArgs e) {
            if (e.KeyCode is Keys.Up or Keys.Down) {
                double cur = TryParse(Text, out double v) ? v : Minimum;
                double step = e.Shift ? Step * 10 : Step;
                cur += e.KeyCode == Keys.Up ? step : -step;
                cur = Math.Max(Minimum, Math.Min(Maximum, Math.Round(cur, Math.Max(0, Decimals))));
                Text = Format(cur);
                Inner.SelectionStart = Inner.Text.Length;
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }
            base.InnerKeyDown(sender, e);
        }
    }
}
