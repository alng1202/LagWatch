using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LagWatch
{
    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(10, 16, 27);
        public static readonly Color Card = Color.FromArgb(18, 27, 43);
        public static readonly Color CardAlt = Color.FromArgb(22, 33, 52);
        public static readonly Color Border = Color.FromArgb(38, 53, 76);
        public static readonly Color Grid = Color.FromArgb(31, 45, 65);
        public static readonly Color Text = Color.FromArgb(235, 242, 250);
        public static readonly Color TextMuted = Color.FromArgb(154, 170, 191);
        public static readonly Color TextFaint = Color.FromArgb(101, 119, 145);
        public static readonly Color Accent = Color.FromArgb(44, 209, 190);
        public static readonly Color AccentBlue = Color.FromArgb(60, 143, 255);
        public static readonly Color Good = Color.FromArgb(89, 214, 139);
        public static readonly Color Warning = Color.FromArgb(247, 184, 79);
        public static readonly Color Bad = Color.FromArgb(245, 98, 110);
    }

    internal sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            DoubleBuffered = true;
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
            Font = new Font("Segoe UI Semibold", 9f);
            UseVisualStyleBackColor = false;
            ApplyColors();
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
