using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DL1Assistant
{
    internal static class Theme
    {
        internal static readonly Color Background = Color.FromArgb(9, 13, 21);
        internal static readonly Color Sidebar = Color.FromArgb(12, 19, 33);
        internal static readonly Color Card = Color.FromArgb(17, 24, 39);
        internal static readonly Color Card2 = Color.FromArgb(23, 32, 51);
        internal static readonly Color Line = Color.FromArgb(43, 55, 77);
        internal static readonly Color Text = Color.FromArgb(238, 244, 255);
        internal static readonly Color Muted = Color.FromArgb(158, 171, 192);
        internal static readonly Color Accent = Color.FromArgb(72, 215, 165);
        internal static readonly Color AccentHover = Color.FromArgb(89, 228, 180);
        internal static readonly Color Warning = Color.FromArgb(255, 204, 102);
        internal static readonly Color Bad = Color.FromArgb(255, 125, 125);
        internal static readonly Color Blue = Color.FromArgb(112, 167, 255);
        internal static readonly Font Normal = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
        internal static readonly Font Bold = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        internal static readonly Font MinusSymbol = new Font("Segoe UI", 12F, FontStyle.Bold);
        internal static readonly Font PlusSymbol = new Font("Segoe UI", 11F, FontStyle.Bold);

        internal static GraphicsPath RoundRect(RectangleF rect, float radius)
        {
            float d = Math.Max(2f, Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height)));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static Label Label(string text, float size, FontStyle style, Color color)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Text = text;
            l.Font = new Font("Microsoft YaHei UI", size, style);
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            l.Margin = Padding.Empty;
            return l;
        }
    }

    // IMPORTANT: these controls do not rely on WinForms transparency for their corners.
    // WinForms transparent controls ask the parent to repaint and can expose the native
    // white/rectangular backing during nested layout/resize.  Every custom control clears
    // its full client area with the exact surface colour first and then draws the rounded
    // body.  This is the same class of fix used by the later SMT helper UI.
    internal class RoundedPanel : Panel
    {
        internal int Radius = 16;
        internal Color BorderColor = Theme.Line;
        internal int BorderWidth = 1;
        internal Color SurfaceColor = Theme.Background;

        internal RoundedPanel()
        {
            DoubleBuffered = true;
            BackColor = Theme.Card;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Opaque, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Opaque owner-drawn control: background and rounded body are painted
            // together in OnPaint. This avoids native rectangular erase/focus artifacts.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(SurfaceColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(1f, 1f, Math.Max(1f, Width - 2f), Math.Max(1f, Height - 2f));
            using (GraphicsPath path = Theme.RoundRect(r, Radius))
            using (SolidBrush brush = new SolidBrush(BackColor))
            {
                e.Graphics.FillPath(brush, path);
                if (BorderWidth > 0 && BorderColor.A > 0)
                {
                    using (Pen pen = new Pen(BorderColor, BorderWidth)) e.Graphics.DrawPath(pen, path);
                }
            }
        }
    }

    internal class RoundedButton : Control
    {
        internal int Radius = 12;
        internal Color SurfaceColor = Theme.Background;
        private Color _normalColor = Theme.Card2;
        private Color _hoverColor = Color.FromArgb(31, 44, 68);
        private Color _pressedColor = Color.FromArgb(38, 51, 75);
        internal Color NormalColor { get { return _normalColor; } set { if (_normalColor == value) return; _normalColor = value; Invalidate(); } }
        internal Color HoverColor { get { return _hoverColor; } set { if (_hoverColor == value) return; _hoverColor = value; Invalidate(); } }
        internal Color PressedColor { get { return _pressedColor; } set { if (_pressedColor == value) return; _pressedColor = value; Invalidate(); } }
        internal ContentAlignment TextAlign = ContentAlignment.MiddleCenter;
        private bool _hover;
        private bool _pressed;

        internal RoundedButton()
        {
            Cursor = Cursors.Hand;
            ForeColor = Theme.Text;
            Font = Theme.Bold;
            Height = 38;
            MinimumSize = new Size(56, 32);
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Opaque, true);
            // We dispatch Click ourselves after validating mouse-up inside the rounded
            // control. Disable WinForms' standard click dispatch so one physical click
            // cannot invoke an action twice.
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // No native Region: rounded shape is paint-only. This avoids WinForms/GDI
            // rectangular leftovers while the parent window is being resized.
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Enabled && e.Button == MouseButtons.Left) { _pressed = true; Capture = true; Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool click = Enabled && _pressed && ClientRectangle.Contains(e.Location);
            _pressed = false;
            Capture = false;
            Invalidate();
            base.OnMouseUp(e);
            if (click) OnClick(EventArgs.Empty);
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            // Do not use OnCaptureChanged: WinForms does not expose it as an overridable
            // member.  Resetting here prevents the old pressed/focus rectangle from being
            // left behind after drag/resize or a cancelled click.
            _pressed = false;
            Invalidate();
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(SurfaceColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = !Enabled ? Color.FromArgb(18, 27, 43) : (_pressed ? PressedColor : (_hover ? HoverColor : NormalColor));
            RectangleF r = new RectangleF(0.5f, 0.5f, Math.Max(1f, Width - 1f), Math.Max(1f, Height - 1f));
            using (GraphicsPath p = Theme.RoundRect(r, Radius))
            using (SolidBrush b = new SolidBrush(fill))
                e.Graphics.FillPath(b, p);

            Color tc = Enabled ? ForeColor : Color.FromArgb(93, 106, 128);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            if (TextAlign == ContentAlignment.MiddleLeft) flags |= TextFormatFlags.Left;
            else if (TextAlign == ContentAlignment.MiddleRight) flags |= TextFormatFlags.Right;
            else flags |= TextFormatFlags.HorizontalCenter;
            Rectangle tr = ClientRectangle;
            tr.Inflate(-10, -2);

            // Never silently ellipsize an action label.  Most buttons fit at the
            // normal font; only narrow/high-DPI cells create this temporary smaller
            // font.  No cached bitmap/Region is involved, so resize remains smooth.
            Font drawFont = Font;
            Font compact = null;
            try
            {
                Size required = TextRenderer.MeasureText(e.Graphics, Text ?? "", drawFont,
                    new Size(Math.Max(1, tr.Width), 1000), flags);
                float pts = drawFont.SizeInPoints;
                while ((required.Height > tr.Height || required.Width > tr.Width) && pts > 6.6F)
                {
                    pts = Math.Max(6.5F, pts - 0.6F);
                    if (compact != null) compact.Dispose();
                    compact = new Font(Font.FontFamily, pts, Font.Style, GraphicsUnit.Point);
                    drawFont = compact;
                    required = TextRenderer.MeasureText(e.Graphics, Text ?? "", drawFont,
                        new Size(Math.Max(1, tr.Width), 1000), flags);
                }
                TextRenderer.DrawText(e.Graphics, Text, drawFont, tr, tc, flags);
            }
            finally { if (compact != null) compact.Dispose(); }
        }
    }

    internal sealed class ToggleSwitch : Control
    {
        private bool _checked;
        private bool _hover;
        internal Color SurfaceColor = Theme.Card;
        internal event EventHandler ValueChanged;

        internal bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        internal ToggleSwitch()
        {
            Size = new Size(48, 28);
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Opaque, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (Enabled && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Checked = !Checked;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(SurfaceColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color track = !Enabled ? Color.FromArgb(35, 43, 57) : (_checked ? Theme.Accent : (_hover ? Color.FromArgb(62, 76, 96) : Theme.Line));
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (GraphicsPath p = Theme.RoundRect(r, Height / 2f))
            using (SolidBrush b = new SolidBrush(track)) e.Graphics.FillPath(b, p);
            float d = Height - 8;
            float x = _checked ? Width - d - 4 : 4;
            using (SolidBrush b = new SolidBrush(_checked ? Color.FromArgb(6, 38, 28) : Theme.Text)) e.Graphics.FillEllipse(b, x, 4, d, d);
        }
    }

    internal sealed class NumberPicker : Control
    {
        private int _value = 1;
        private bool _minusHover;
        private bool _plusHover;
        internal Color SurfaceColor = Theme.Card;
        internal int Minimum = 1;
        internal int Maximum = 10;
        internal int Step = 1;
        internal string Prefix = "x";
        internal event EventHandler ValueChanged;

        internal int Value
        {
            get { return _value; }
            set
            {
                int next = Math.Max(Minimum, Math.Min(Maximum, value));
                if (_value == next) return;
                _value = next;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        internal NumberPicker()
        {
            Size = new Size(148, 36);
            Font = Theme.Bold;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Opaque, true);
        }

        private Rectangle MinusRect { get { return new Rectangle(1, 1, 36, Height - 2); } }
        private Rectangle PlusRect { get { return new Rectangle(Width - 37, 1, 36, Height - 2); } }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool m = MinusRect.Contains(e.Location), p = PlusRect.Contains(e.Location);
            if (m != _minusHover || p != _plusHover) { _minusHover = m; _plusHover = p; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { _minusHover = _plusHover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            if (MinusRect.Contains(e.Location)) Value -= Step;
            else if (PlusRect.Contains(e.Location)) Value += Step;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(SurfaceColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF all = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (GraphicsPath p = Theme.RoundRect(all, 11))
            using (SolidBrush b = new SolidBrush(Theme.Card2))
            using (Pen pen = new Pen(Theme.Line))
            {
                e.Graphics.FillPath(b, p);
                e.Graphics.DrawPath(pen, p);
            }
            using (SolidBrush b = new SolidBrush(_minusHover ? Color.FromArgb(35, 49, 72) : Theme.Card2)) e.Graphics.FillRectangle(b, MinusRect);
            using (SolidBrush b = new SolidBrush(_plusHover ? Color.FromArgb(35, 49, 72) : Theme.Card2)) e.Graphics.FillRectangle(b, PlusRect);
            using (Pen pen = new Pen(Theme.Line))
            {
                e.Graphics.DrawLine(pen, 38, 6, 38, Height - 6);
                e.Graphics.DrawLine(pen, Width - 39, 6, Width - 39, Height - 6);
            }
            TextRenderer.DrawText(e.Graphics, "−", Theme.MinusSymbol, MinusRect, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, "+", Theme.PlusSymbol, PlusRect, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Rectangle valueRect = new Rectangle(39, 0, Math.Max(1, Width - 78), Height);
            TextRenderer.DrawText(e.Graphics, Prefix + _value, Font, valueRect, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    internal sealed class ProgressStrip : Control
    {
        internal Color SurfaceColor = Theme.Card;
        internal int Value = 0;
        internal int Maximum = 100;
        internal ProgressStrip()
        {
            Height = 7;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Opaque, true);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(SurfaceColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath bg = Theme.RoundRect(r, Height))
            using (SolidBrush b = new SolidBrush(Theme.Line)) e.Graphics.FillPath(b, bg);
            double ratio = Maximum <= 0 ? 0 : Math.Max(0, Math.Min(1, (double)Value / Maximum));
            float w = (float)Math.Round(r.Width * ratio);
            if (w > 1)
            {
                RectangleF f = new RectangleF(0, 0, w, r.Height);
                using (GraphicsPath fg = Theme.RoundRect(f, Height))
                using (SolidBrush b = new SolidBrush(Theme.Accent)) e.Graphics.FillPath(b, fg);
            }
        }
    }
}
