using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace LagWatch
{
    internal sealed class LatencyChart : Control
    {
        private readonly List<PingSample> samples = new List<PingSample>();
        private readonly object sync = new object();

        public LatencyChart()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Card;
            ForeColor = Theme.TextMuted;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
        }

        private ForecastSlot usual;

        /// <summary>Plus: the usual latency at this time of day, drawn as a dashed line over a shaded typical range.</summary>
        public void SetUsual(ForecastSlot slot)
        {
            usual = slot != null && slot.Ready ? slot : null;
            Invalidate();
        }

        public void SetSamples(IEnumerable<PingSample> values)
        {
            lock (sync)
            {
                samples.Clear();
                samples.AddRange(values.TakeLastCompatible(120));
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Insets follow the label text size, so the axis and caption fit at any display scaling.
            int labelWidth, labelHeight;
            using (Font measureFont = Theme.UiFont(8.5f))
            {
                Size measured = TextRenderer.MeasureText(g, "2000 ms", measureFont);
                labelWidth = measured.Width + Theme.Px(this, 6);
                labelHeight = measured.Height;
            }
            Rectangle area = new Rectangle(labelWidth, labelHeight, Math.Max(1, Width - labelWidth - Theme.Px(this, 16)),
                Math.Max(1, Height - labelHeight * 2 - Theme.Px(this, 12)));
            List<PingSample> local;
            lock (sync) local = samples.ToList();

            long observedMax = local.Where(s => s.Success).Select(s => s.LatencyMs).DefaultIfEmpty(60).Max();
            ForecastSlot expected = usual;
            if (expected != null) observedMax = Math.Max(observedMax, (long)Math.Ceiling(expected.HighMs));
            int scaleMax = NiceScale(Math.Max(60, observedMax));

            using (Pen gridPen = new Pen(Theme.Grid, 1))
            using (Brush labelBrush = new SolidBrush(Theme.TextFaint))
            using (Font labelFont = Theme.UiFont(8.5f))
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = area.Top + area.Height * i / 4f;
                    g.DrawLine(gridPen, area.Left, y, area.Right, y);
                    int value = (int)Math.Round(scaleMax * (4 - i) / 4.0);
                    g.DrawString(value + " ms", labelFont, labelBrush, 2, y - labelHeight / 2f);
                }
            }

            if (expected != null)
            {
                Func<double, float> yFor = ms => area.Bottom - (float)Math.Min(1.0, ms / scaleMax) * area.Height;
                float bandTop = yFor(expected.HighMs), bandBottom = yFor(expected.LowMs);
                using (Brush band = new SolidBrush(Color.FromArgb(38, Theme.AccentBlue)))
                    g.FillRectangle(band, area.Left, bandTop, area.Width, Math.Max(2f, bandBottom - bandTop));
                float y = yFor(expected.MedianMs);
                using (Pen dashed = new Pen(Theme.AccentBlue, 1.6f) { DashStyle = DashStyle.Dash })
                    g.DrawLine(dashed, area.Left, y, area.Right, y);
                using (Font font = Theme.UiFont(8f, true))
                using (Brush brush = new SolidBrush(Theme.AccentBlue))
                {
                    string label = "usual now ≈ " + expected.MedianMs.ToString("0") + " ms";
                    SizeF size = g.MeasureString(label, font);
                    g.DrawString(label, font, brush, area.Left + Theme.Px(this, 4), Math.Max(area.Top, y - size.Height - 1));
                }
            }

            if (local.Count == 0)
            {
                using (Brush brush = new SolidBrush(Theme.TextMuted))
                using (Font font = Theme.UiFont(11f))
                {
                    string message = "Start monitoring to see live route quality";
                    SizeF size = g.MeasureString(message, font);
                    g.DrawString(message, font, brush,
                        area.Left + (area.Width - size.Width) / 2,
                        area.Top + (area.Height - size.Height) / 2);
                }
                return;
            }

            float step = local.Count <= 1 ? area.Width : area.Width / (float)Math.Max(119, local.Count - 1);
            float startX = area.Right - step * (local.Count - 1);
            PointF? previous = null;
            using (GraphicsPath fillPath = new GraphicsPath())
            using (Pen linePen = new Pen(Theme.Accent, 2.2f))
            using (Pen lossPen = new Pen(Theme.Bad, 1.5f))
            {
                List<PointF> successfulPoints = new List<PointF>();
                for (int i = 0; i < local.Count; i++)
                {
                    float x = startX + i * step;
                    if (!local[i].Success)
                    {
                        g.DrawLine(lossPen, x - 3, area.Bottom - 10, x + 3, area.Bottom - 4);
                        g.DrawLine(lossPen, x + 3, area.Bottom - 10, x - 3, area.Bottom - 4);
                        previous = null;
                        continue;
                    }

                    float normalized = Math.Min(1f, local[i].LatencyMs / (float)scaleMax);
                    float y = area.Bottom - normalized * area.Height;
                    PointF point = new PointF(x, y);
                    successfulPoints.Add(point);
                    if (previous.HasValue)
                        g.DrawLine(linePen, previous.Value, point);
                    previous = point;
                }

                if (successfulPoints.Count > 1)
                {
                    fillPath.AddLines(successfulPoints.ToArray());
                    fillPath.AddLine(successfulPoints[successfulPoints.Count - 1].X,
                        successfulPoints[successfulPoints.Count - 1].Y,
                        successfulPoints[successfulPoints.Count - 1].X, area.Bottom);
                    fillPath.AddLine(successfulPoints[successfulPoints.Count - 1].X, area.Bottom,
                        successfulPoints[0].X, area.Bottom);
                    fillPath.CloseFigure();
                    using (LinearGradientBrush fill = new LinearGradientBrush(
                        area, Color.FromArgb(55, Theme.Accent), Color.FromArgb(2, Theme.Accent), 90f))
                    {
                        g.FillPath(fill, fillPath);
                    }

                    previous = null;
                    for (int i = 0; i < local.Count; i++)
                    {
                        if (!local[i].Success) { previous = null; continue; }
                        float x = startX + i * step;
                        float y = area.Bottom - Math.Min(1f, local[i].LatencyMs / (float)scaleMax) * area.Height;
                        PointF point = new PointF(x, y);
                        if (previous.HasValue) g.DrawLine(linePen, previous.Value, point);
                        previous = point;
                    }
                }
            }

            using (Brush brush = new SolidBrush(Theme.TextFaint))
            using (Font font = Theme.UiFont(8.5f))
            {
                SizeF caption = g.MeasureString("last 2 minutes", font);
                g.DrawString("last 2 minutes", font, brush, area.Right - caption.Width, area.Bottom + Theme.Px(this, 6));
            }
        }

        private static int NiceScale(long observed)
        {
            int[] values = { 60, 100, 150, 200, 300, 500, 750, 1000, 1500, 2000 };
            foreach (int value in values)
                if (observed <= value) return value;
            return (int)Math.Ceiling(observed / 500.0) * 500;
        }
    }

    internal static class EnumerableCompatibility
    {
        public static IEnumerable<T> TakeLastCompatible<T>(this IEnumerable<T> source, int count)
        {
            if (source == null) return Enumerable.Empty<T>();
            List<T> values = source.ToList();
            return values.Skip(Math.Max(0, values.Count - count));
        }
    }
}
