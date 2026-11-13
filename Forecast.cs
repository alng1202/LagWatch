using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace LagWatch
{
    /// <summary>What LagWatch expects at one time of day, learned from the same time on earlier days.</summary>
    internal sealed class ForecastSlot
    {
        public DateTime Start { get; set; }
        public bool Ready { get; set; }
        public double MedianMs { get; set; }
        /// <summary>Typical range: the middle half of earlier samples (25th–75th percentile).</summary>
        public double LowMs { get; set; }
        public double HighMs { get; set; }
        public int Samples { get; set; }
        public int Days { get; set; }
        public double FailurePercent { get; set; }
    }

    /// <summary>Everything the main window shows about predictions, computed off the UI thread.</summary>
    internal sealed class ForecastSnapshot
    {
        public string Target { get; set; }
        public ForecastSlot Now { get; set; }
        public List<ForecastSlot> Upcoming { get; set; } = new List<ForecastSlot>();
        public static readonly ForecastSnapshot Empty = new ForecastSnapshot { Now = new ForecastSlot() };
    }

    /// <summary>
    /// Reads lagwatch-history.csv once (and again only when it changes) and answers "what is usual at this time of day".
    /// Same rule as before: a time is ready after 12 observations within ±15 minutes of it, on at least 3 earlier days.
    /// It's a robust time-of-day median, not machine learning.
    /// </summary>
    internal sealed class LatencyForecaster
    {
        public const int RequiredSamples = 12;
        public const int RequiredDays = 3;
        private const int WindowMinutes = 15;

        private struct Point { public DateTime Time; public bool Success; public double Ms; }

        private readonly string path;
        private readonly object sync = new object();
        private Dictionary<string, List<Point>> byTarget = new Dictionary<string, List<Point>>(StringComparer.OrdinalIgnoreCase);
        private DateTime loadedWrite = DateTime.MinValue;
        private DateTime loadedAt = DateTime.MinValue;

        public LatencyForecaster(string path) { this.path = path; }

        public ForecastSnapshot Build(DateTime now, string target, int hours, int slotMinutes)
        {
            EnsureLoaded();
            List<Point> points;
            lock (sync) byTarget.TryGetValue(target ?? "", out points);
            points = points ?? new List<Point>();
            ForecastSnapshot snapshot = new ForecastSnapshot { Target = target, Now = Slot(points, now, now) };
            DateTime start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute < 30 ? 0 : 30, 0);
            if (slotMinutes != 30) start = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            for (DateTime t = start; t < now.AddHours(hours); t = t.AddMinutes(slotMinutes))
            {
                // Center each slot so its ±15 min window covers the whole half hour.
                ForecastSlot slot = Slot(points, t.AddMinutes(slotMinutes / 2.0), now);
                slot.Start = t;
                snapshot.Upcoming.Add(slot);
            }
            return snapshot;
        }

        private static ForecastSlot Slot(List<Point> points, DateTime when, DateTime today)
        {
            ForecastSlot slot = new ForecastSlot { Start = when };
            List<double> values = new List<double>();
            HashSet<DateTime> days = new HashSet<DateTime>();
            int failures = 0;
            double minute = when.TimeOfDay.TotalMinutes;
            foreach (Point p in points)
            {
                if (p.Time.Date == today.Date) continue; // only earlier days predict today
                double distance = Math.Abs(p.Time.TimeOfDay.TotalMinutes - minute);
                distance = Math.Min(distance, 1440 - distance);
                if (distance > WindowMinutes) continue;
                slot.Samples++;
                days.Add(p.Time.Date);
                if (p.Success) values.Add(p.Ms); else failures++;
            }
            slot.Days = days.Count;
            slot.FailurePercent = slot.Samples == 0 ? 0 : 100.0 * failures / slot.Samples;
            if (slot.Samples >= RequiredSamples && slot.Days >= RequiredDays && values.Count > 0)
            {
                values.Sort();
                slot.MedianMs = Percentile(values, 0.5);
                slot.LowMs = Percentile(values, 0.25);
                slot.HighMs = Percentile(values, 0.75);
                slot.Ready = true;
            }
            return slot;
        }

        private static double Percentile(List<double> sorted, double p)
        {
            if (sorted.Count == 1) return sorted[0];
            double index = p * (sorted.Count - 1);
            int lower = (int)Math.Floor(index);
            int upper = Math.Min(sorted.Count - 1, lower + 1);
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
        }

        /// <summary>Reloads the file when it changed, at most once a minute (it gains one row per minute).</summary>
        private void EnsureLoaded()
        {
            if (!File.Exists(path)) { lock (sync) byTarget.Clear(); return; }
            DateTime write = File.GetLastWriteTimeUtc(path);
            if (write == loadedWrite || DateTime.UtcNow - loadedAt < TimeSpan.FromSeconds(55)) return;
            var fresh = new Dictionary<string, List<Point>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream))
                {
                    reader.ReadLine();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] parts = ParseCsv(line);
                        DateTime time;
                        if ((parts.Length != 3 && parts.Length != 4) || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out time)) continue;
                        bool success = true; double ms = 0;
                        if (parts.Length == 4) Boolean.TryParse(parts[2], out success);
                        string latency = parts.Length == 4 ? parts[3] : parts[2];
                        if (success && !Double.TryParse(latency, NumberStyles.Float, CultureInfo.InvariantCulture, out ms)) continue;
                        List<Point> list;
                        if (!fresh.TryGetValue(parts[1], out list)) fresh[parts[1]] = list = new List<Point>();
                        list.Add(new Point { Time = time, Success = success, Ms = ms });
                    }
                }
            }
            catch (IOException) { return; }
            lock (sync) byTarget = fresh;
            loadedWrite = write;
            loadedAt = DateTime.UtcNow;
        }

        private static string[] ParseCsv(string line)
        {
            List<string> fields = new List<string>();
            System.Text.StringBuilder current = new System.Text.StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else current.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
            fields.Add(current.ToString());
            return fields.ToArray();
        }

        /// <summary>Quality of an expected latency, with the same thresholds as the live status badge.</summary>
        public static Color ColorFor(ForecastSlot slot)
        {
            if (!slot.Ready) return Theme.Border;
            if (slot.FailurePercent >= 10 || slot.MedianMs >= 120) return Theme.Bad;
            if (slot.FailurePercent >= 2 || slot.MedianMs >= 80) return Theme.Warning;
            return Theme.Good;
        }
    }

    /// <summary>"Next 6 hours": one bar per half hour with the usual latency at that time, and the best time coming up.</summary>
    internal sealed class ForecastStrip : Control
    {
        private ForecastSnapshot snapshot = ForecastSnapshot.Empty;

        public ForecastStrip()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Card;
        }

        public void SetSnapshot(ForecastSnapshot value)
        {
            snapshot = value ?? ForecastSnapshot.Empty;
            Invalidate();
        }

        private RiskOutlook risk = RiskOutlook.Empty;

        /// <summary>Coming spotty times from the connection watch; they take the top-right spot and mark their bars.</summary>
        public void SetRisk(RiskOutlook value)
        {
            risk = value ?? RiskOutlook.Empty;
            Invalidate();
        }

        /// <summary>The first spotty time in the strip's 6 hours, if any.</summary>
        private RiskWindow NextRisk()
        {
            DateTime horizon = DateTime.Now.AddHours(6);
            return risk.Windows.FirstOrDefault(w => w.End > DateTime.Now && w.Start < horizon);
        }

        private void DrawRiskText(Graphics g, Font font, int top, int pad)
        {
            RiskWindow w = NextRisk();
            if (w == null) return;
            string text = "⚠ " + (LinkEvaluator.IsOutage(w.Usual) ? "Outage" : "Spotty") + " likely " +
                w.Start.ToString("h:mm", CultureInfo.CurrentCulture) + "–" + w.End.ToString("h:mm tt", CultureInfo.CurrentCulture) +
                " · " + w.SpottyDays + " of last " + w.ObservedDays + " " + w.Basis;
            SizeF size = g.MeasureString(text, font);
            using (Brush brush = new SolidBrush(w.High ? Theme.Bad : Theme.Warning))
                g.DrawString(text, font, brush, Width - size.Width - pad, top);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int pad = Theme.Px(this, 2);
            using (Font titleFont = Theme.UiFont(8f, true))
            using (Font smallFont = Theme.UiFont(8f))
            using (Brush titleBrush = new SolidBrush(Theme.TextMuted))
            using (Brush faintBrush = new SolidBrush(Theme.TextFaint))
            using (Pen divider = new Pen(Theme.Border))
            {
                g.DrawLine(divider, 0, 0, Width, 0);
                int titleHeight = TextRenderer.MeasureText("NEXT", titleFont).Height;
                int top = Theme.Px(this, 8);
                g.DrawString("NEXT 6 HOURS · USUAL LATENCY", titleFont, titleBrush, pad, top);

                List<ForecastSlot> slots = snapshot.Upcoming;
                bool anyReady = slots.Any(s => s.Ready);
                if (!anyReady)
                {
                    DrawRiskText(g, titleFont, top, pad);
                    DrawLearning(g, smallFont, faintBrush, top + titleHeight + Theme.Px(this, 6));
                    return;
                }

                // A coming spotty time matters more than the best time, so it takes the spot when there is one.
                ForecastSlot best = slots.Skip(1).Where(s => s.Ready && s.FailurePercent < 10).OrderBy(s => s.MedianMs).FirstOrDefault();
                if (NextRisk() != null) DrawRiskText(g, titleFont, top, pad);
                else if (best != null)
                {
                    string bestText = "Best ahead: " + best.Start.ToString("h:mm tt", CultureInfo.CurrentCulture) + " · ≈" + best.MedianMs.ToString("0") + " ms";
                    SizeF size = g.MeasureString(bestText, titleFont);
                    using (Brush goodBrush = new SolidBrush(Theme.Good))
                        g.DrawString(bestText, titleFont, goodBrush, Width - size.Width - pad, top);
                }

                int labelHeight = TextRenderer.MeasureText("12 AM", smallFont).Height;
                // Room above the bars for each bar's value.
                int barsTop = top + titleHeight + labelHeight + Theme.Px(this, 6);
                int barsBottom = Height - labelHeight - Theme.Px(this, 4);
                int barsHeight = Math.Max(4, barsBottom - barsTop);
                // Scale to the forecast itself, so a quiet 10 ms night still shows its shape.
                double scale = Math.Max(20, slots.Where(s => s.Ready).Select(s => s.MedianMs).DefaultIfEmpty(20).Max() * 1.1);
                float slotWidth = (Width - pad * 2) / (float)Math.Max(1, slots.Count);
                float gap = Math.Max(2f, slotWidth * 0.18f);
                for (int i = 0; i < slots.Count; i++)
                {
                    ForecastSlot s = slots[i];
                    float x = pad + i * slotWidth + gap / 2;
                    float w = slotWidth - gap;
                    float h = s.Ready ? (float)Math.Max(Theme.Px(this, 3), barsHeight * Math.Min(1, s.MedianMs / scale)) : Theme.Px(this, 3);
                    Color color = ForecastStripColor(s, i == 0);
                    using (Brush bar = new SolidBrush(color))
                        g.FillRectangle(bar, x, barsBottom - h, w, h);
                    // Usually-spotty half hours get a warning line across the top of their column.
                    RiskWindow slotRisk = risk.Windows.FirstOrDefault(r => r.Start < s.Start.AddMinutes(30) && r.End > s.Start);
                    if (slotRisk != null)
                        using (Brush mark = new SolidBrush(slotRisk.High ? Theme.Bad : Theme.Warning))
                            g.FillRectangle(mark, x, barsTop - Theme.Px(this, 4), w, Theme.Px(this, 3));
                    if (i == 0)
                        using (Pen nowPen = new Pen(Theme.Text, 1.4f))
                            g.DrawRectangle(nowPen, x - 1, barsTop - 1, w + 2, barsHeight + 2);
                    if (s.Ready)
                    {
                        string value = s.MedianMs.ToString("0");
                        SizeF size = g.MeasureString(value, smallFont);
                        if (size.Width <= slotWidth)
                            g.DrawString(value, smallFont, faintBrush, x + (w - size.Width) / 2,
                                barsBottom - h - size.Height - Theme.Px(this, 1));
                    }
                    // "now" under the first bar, then each full hour.
                    string label = i == 0 ? "now"
                        : s.Start.Minute == 0 ? s.Start.ToString("h tt", CultureInfo.CurrentCulture).ToLowerInvariant() : null;
                    if (label != null)
                        g.DrawString(label, smallFont, faintBrush, x, barsBottom + Theme.Px(this, 2));
                }
            }
        }

        private static Color ForecastStripColor(ForecastSlot slot, bool current)
        {
            Color c = LatencyForecaster.ColorFor(slot);
            return slot.Ready ? Color.FromArgb(current ? 255 : 200, c) : c;
        }

        private void DrawLearning(Graphics g, Font font, Brush brush, int top)
        {
            ForecastSlot now = snapshot.Now ?? new ForecastSlot();
            int pad = Theme.Px(this, 2);
            double progress = Math.Min(1.0, Math.Min(now.Samples / (double)LatencyForecaster.RequiredSamples,
                now.Days / (double)LatencyForecaster.RequiredDays));
            string text = "Learning your usual latency for this time of day · " +
                Math.Min(now.Days, LatencyForecaster.RequiredDays) + " of " + LatencyForecaster.RequiredDays + " days, " +
                Math.Min(now.Samples, LatencyForecaster.RequiredSamples) + " of " + LatencyForecaster.RequiredSamples +
                " samples. Keep LagWatch monitoring at this time on other days.";
            RectangleF textBox = new RectangleF(pad, top, Width - pad * 2, Height - top - Theme.Px(this, 14));
            g.DrawString(text, font, brush, textBox);
            int barTop = Height - Theme.Px(this, 10);
            using (Brush track = new SolidBrush(Theme.CardAlt))
            using (Brush fill = new SolidBrush(Theme.Accent))
            {
                g.FillRectangle(track, pad, barTop, Width - pad * 2, Theme.Px(this, 5));
                g.FillRectangle(fill, pad, barTop, (float)((Width - pad * 2) * progress), Theme.Px(this, 5));
            }
        }
    }
}
