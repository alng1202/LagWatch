using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LagWatch
{
    internal static class Theme
    {
        public static Color Background = Color.FromArgb(10, 16, 27);
        public static Color Card = Color.FromArgb(18, 27, 43);
        public static Color CardAlt = Color.FromArgb(22, 33, 52);
        public static Color Border = Color.FromArgb(38, 53, 76);
        public static Color Grid = Color.FromArgb(31, 45, 65);
        public static Color Text = Color.FromArgb(235, 242, 250);
        public static Color TextMuted = Color.FromArgb(154, 170, 191);
        public static Color TextFaint = Color.FromArgb(101, 119, 145);
        public static Color Accent = Color.FromArgb(44, 209, 190);
        public static Color AccentBlue = Color.FromArgb(60, 143, 255);
        public static Color Good = Color.FromArgb(89, 214, 139);
        public static Color Warning = Color.FromArgb(247, 184, 79);
        public static Color Bad = Color.FromArgb(245, 98, 110);
        public static bool IsDark = true;

        // App typeface: Bahnschrift (ships with Windows 10 1709+ and Windows 11), falling back to Segoe UI.
        private static readonly string RegularFamily = FirstInstalled("Bahnschrift", "Segoe UI");
        private static readonly string BoldFamily = FirstInstalled("Bahnschrift SemiBold", "Segoe UI Semibold");
        private static readonly string MonoFamily = FirstInstalled("Cascadia Mono", "Consolas");

        public static Font UiFont(float size, bool bold = false)
        {
            return new Font(bold ? BoldFamily : RegularFamily, size);
        }

        /// <summary>
        /// Call first in a form's constructor. Layouts are written in 96-DPI pixels; fonts are in points and grow with
        /// Windows' display scaling. With this, WinForms scales positions and sizes by the same factor, so text never
        /// outgrows its box (the overlaps seen at 125–150% scaling).
        /// </summary>
        public static void BeginDpiLayout(Form form)
        {
            form.SuspendLayout();
            form.AutoScaleDimensions = new SizeF(96f, 96f);
            form.AutoScaleMode = AutoScaleMode.Dpi;
        }

        /// <summary>Call last in a form's constructor: scales the layout once and keeps the window on screen.</summary>
        public static void EndDpiLayout(Form form)
        {
            form.ResumeLayout(false);
            form.PerformAutoScale();
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (form.MinimumSize.Width > area.Width || form.MinimumSize.Height > area.Height)
                form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, area.Width), Math.Min(form.MinimumSize.Height, area.Height));
            if (form.Width > area.Width || form.Height > area.Height)
                form.Size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
            form.PerformLayout();
        }

        /// <summary>A 96-DPI pixel value at the control's current DPI, for sizes set after the form is built.</summary>
        public static int Px(Control control, int pixels)
        {
            return (int)Math.Round(pixels * control.DeviceDpi / 96f);
        }

        public static Font MonoFont(float size)
        {
            return new Font(MonoFamily, size);
        }

        private static string FirstInstalled(params string[] families)
        {
            try
            {
                using (System.Drawing.Text.InstalledFontCollection installed = new System.Drawing.Text.InstalledFontCollection())
                    foreach (string family in families)
                        foreach (FontFamily f in installed.Families)
                            if (String.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase)) return family;
            }
            catch { }
            return families[families.Length - 1];
        }

        public static string NormalizePreset(string name)
        {
            if (String.Equals(name, "Midnight", StringComparison.OrdinalIgnoreCase)) return "Midnight";
            if (String.Equals(name, "Light", StringComparison.OrdinalIgnoreCase)) return "Light";
            return "Dark";
        }

        public static ThemePalette Capture()
        {
            return new ThemePalette
            {
                Background = Background,
                Card = Card,
                CardAlt = CardAlt,
                Border = Border,
                Grid = Grid,
                Text = Text,
                TextMuted = TextMuted,
                TextFaint = TextFaint,
                Accent = Accent,
                AccentBlue = AccentBlue,
                Good = Good,
                Warning = Warning,
                Bad = Bad
            };
        }

        /// <summary>The theme the app is showing right now (Dark, Midnight or Light).</summary>
        public static string CurrentPreset { get; private set; } = "Dark";

        public static void ApplyPreset(string name)
        {
            string preset = NormalizePreset(name);
            CurrentPreset = preset;
            if (preset == "Light")
            {
                Background = Color.FromArgb(238, 243, 249);
                Card = Color.White;
                CardAlt = Color.FromArgb(226, 234, 244);
                Border = Color.FromArgb(190, 202, 218);
                Grid = Color.FromArgb(211, 220, 232);
                Text = Color.FromArgb(24, 34, 48);
                TextMuted = Color.FromArgb(69, 85, 105);
                TextFaint = Color.FromArgb(100, 116, 139);
                Accent = Color.FromArgb(13, 148, 136);
                AccentBlue = Color.FromArgb(37, 99, 235);
                Good = Color.FromArgb(22, 163, 74);
                Warning = Color.FromArgb(202, 138, 4);
                Bad = Color.FromArgb(220, 38, 38);
                IsDark = false;
            }
            else if (preset == "Midnight")
            {
                Background = Color.FromArgb(7, 10, 20);
                Card = Color.FromArgb(15, 20, 36);
                CardAlt = Color.FromArgb(23, 29, 50);
                Border = Color.FromArgb(48, 57, 85);
                Grid = Color.FromArgb(35, 43, 68);
                Text = Color.FromArgb(241, 243, 255);
                TextMuted = Color.FromArgb(164, 172, 205);
                TextFaint = Color.FromArgb(108, 118, 158);
                Accent = Color.FromArgb(139, 92, 246);
                AccentBlue = Color.FromArgb(56, 189, 248);
                Good = Color.FromArgb(74, 222, 128);
                Warning = Color.FromArgb(250, 204, 21);
                Bad = Color.FromArgb(251, 113, 133);
                IsDark = true;
            }
            else
            {
                Background = Color.FromArgb(10, 16, 27);
                Card = Color.FromArgb(18, 27, 43);
                CardAlt = Color.FromArgb(22, 33, 52);
                Border = Color.FromArgb(38, 53, 76);
                Grid = Color.FromArgb(31, 45, 65);
                Text = Color.FromArgb(235, 242, 250);
                TextMuted = Color.FromArgb(154, 170, 191);
                TextFaint = Color.FromArgb(101, 119, 145);
                Accent = Color.FromArgb(44, 209, 190);
                AccentBlue = Color.FromArgb(60, 143, 255);
                Good = Color.FromArgb(89, 214, 139);
                Warning = Color.FromArgb(247, 184, 79);
                Bad = Color.FromArgb(245, 98, 110);
                IsDark = true;
            }
        }
    }

    internal sealed class ThemePalette
    {
        public Color Background;
        public Color Card;
        public Color CardAlt;
        public Color Border;
        public Color Grid;
        public Color Text;
        public Color TextMuted;
        public Color TextFaint;
        public Color Accent;
        public Color AccentBlue;
        public Color Good;
        public Color Warning;
        public Color Bad;
    }

    internal sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            DoubleBuffered = true;
            // Repaint the whole card on resize; otherwise the old border stays behind as a trail of lines.
            ResizeRedraw = true;
            BackColor = Theme.Card;
            Padding = new Padding(16);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class AccentButton : Button
    {
        private bool primary;
        private Color accentColor = Theme.Accent;

        public Color AccentColor
        {
            get { return accentColor; }
            set { accentColor = value; ApplyColors(); }
        }

        public bool Primary
        {
            get { return primary; }
            set { primary = value; ApplyColors(); }
        }

        public AccentButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 1;
            Height = 36;
            Cursor = Cursors.Hand;
            Font = Theme.UiFont(9f, true);
            UseVisualStyleBackColor = false;
            UseMnemonic = false;
            ApplyColors();
        }

        private bool locked;

        /// <summary>Draws a small padlock at the right edge (a feature from a higher plan). The label stays unchanged.</summary>
        public bool Locked
        {
            get { return locked; }
            set { locked = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!locked) return;
            int h = Math.Max(10, Height / 3);
            int w = (int)(h * 0.8);
            int x = Width - w - 9;
            int y = (Height - h) / 2 + 2;
            Color color = primary ? Theme.Background : accentColor;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(BackColor))
                e.Graphics.FillRectangle(bg, x - 6, 2, w + 14, Height - 4);
            using (Pen pen = new Pen(color, 1.6f))
                e.Graphics.DrawArc(pen, x + w * 0.18f, y - h * 0.45f, w * 0.64f, h * 0.8f, 180, 180);
            using (SolidBrush body = new SolidBrush(color))
                e.Graphics.FillRectangle(body, x, y, w, (int)(h * 0.62f));
        }

        protected override void OnEnabledChanged(System.EventArgs e)
        {
            base.OnEnabledChanged(e);
            ApplyColors();
        }

        private void ApplyColors()
        {
            if (!Enabled)
            {
                BackColor = Theme.CardAlt;
                ForeColor = Theme.TextFaint;
                FlatAppearance.BorderColor = Theme.Border;
            }
            else if (primary)
            {
                BackColor = accentColor;
                ForeColor = Theme.Background;
                FlatAppearance.BorderColor = accentColor;
            }
            else
            {
                BackColor = Theme.CardAlt;
                ForeColor = Theme.Text;
                FlatAppearance.BorderColor = Theme.Border;
            }
        }
    }
}
