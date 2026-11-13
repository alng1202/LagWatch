using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LagWatch
{
    internal sealed class MultiTargetResult
    {
        public string Name { get; set; }
        public string Target { get; set; }
        public bool? Success { get; set; }
        public long LatencyMs { get; set; }
        public int Attempts { get; set; }
        public int Failures { get; set; }

        public double LossPercent
        {
            get { return Attempts == 0 ? 0 : 100.0 * Failures / Attempts; }
        }

        public string Quality
        {
            get
            {
                if (!Success.HasValue) return "CHECKING";
                if (LossPercent >= 20 || !Success.Value || LatencyMs >= 180) return "CRITICAL";
                if (LossPercent >= 5 || LatencyMs >= 120) return "POOR";
                if (LossPercent >= 2 || LatencyMs >= 80) return "FAIR";
                return "HEALTHY";
            }
        }
    }

    internal sealed class MultiTargetMonitorForm : Form, IEmbeddedTool
    {
        private bool started;

        /// <summary>Starts the tool's work once, whether it opens as a window (Shown) or as a page in the main window.</summary>
        public async System.Threading.Tasks.Task StartAsync()
        {
            if (started || IsDisposed) return;
            started = true;
            await PollAsync(); timer.Start();
        }

        private readonly Timer timer;
        private readonly List<MultiTargetResult> results = new List<MultiTargetResult>();
        private readonly TableLayoutPanel rows;
        private readonly Label summary;
        private bool pollInFlight;

        public MultiTargetMonitorForm(string selectedTarget)
        {
            Theme.BeginDpiLayout(this);
            Text = "LagWatch — Path Check";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(650, 300);
            MinimumSize = new Size(650, 300);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.UiFont(9f);

            Label title = new Label { Text = "MULTI-TARGET PATH CHECK", AutoSize = true,
                Font = Theme.UiFont(11f, true), ForeColor = Theme.Text, Location = new Point(22, 18) };
            Controls.Add(title);
            Label help = new Label { Text = "Compare the local network path with the wider Internet and your selected target.",
                AutoSize = true, ForeColor = Theme.TextMuted, Location = new Point(23, 45) };
            Controls.Add(help);

            rows = new CompositedTable { Location = new Point(22, 78), Width = 606, Height = 125,
                ColumnCount = 4, RowCount = 1, BackColor = Theme.Card, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            Controls.Add(rows);

            summary = new Label { Text = "Checking targets…", AutoSize = false, Width = 606, Height = 24,
                ForeColor = Theme.TextMuted, Location = new Point(22, 222) };
            Controls.Add(summary);

            string gateway = NetworkDiagnostics.GetDefaultGateway();
            AddTarget("Local gateway", gateway);
            AddTarget("Public reference", "1.1.1.1");
            if (!String.IsNullOrWhiteSpace(selectedTarget) &&
                !String.Equals(selectedTarget, gateway, StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(selectedTarget, "1.1.1.1", StringComparison.OrdinalIgnoreCase))
                AddTarget("Selected target", selectedTarget);

            timer = new Timer { Interval = 1600 };
            timer.Tick += async delegate { await PollAsync(); };
            Shown += async delegate { await StartAsync(); };
            FormClosed += delegate { timer.Stop(); };
            // As a page in the main window it's closed by being disposed; stop polling then too.
            Disposed += delegate { timer.Stop(); timer.Dispose(); };
            Theme.EndDpiLayout(this);
        }

        private void AddTarget(string name, string target)
        {
            if (String.IsNullOrWhiteSpace(target) || !TargetValidator.IsValid(target)) return;
            results.Add(new MultiTargetResult { Name = name, Target = target });
        }

        private async Task PollAsync()
        {
            if (pollInFlight) return;
            pollInFlight = true;
            try
            {
                await Task.WhenAll(results.Select(PingOneAsync));
                RenderRows();
            }
            finally { pollInFlight = false; }
        }

        private static async Task PingOneAsync(MultiTargetResult result)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = await ping.SendPingAsync(result.Target, 1400);
                    result.Attempts++;
                    result.Success = reply.Status == IPStatus.Success;
                    if (result.Success.Value) result.LatencyMs = reply.RoundtripTime;
                    else result.Failures++;
                }
            }
            catch
            {
                result.Attempts++;
                result.Failures++;
                result.Success = false;
            }
        }

        /// <summary>Cells are created once and then updated in place; rebuilding them every poll made the table blink.</summary>
        private Label[,] cells;

        private void BuildRows()
        {
            rows.SuspendLayout();
            rows.Controls.Clear();
            rows.RowStyles.Clear();
            rows.RowCount = results.Count + 1;
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            foreach (MultiTargetResult result in results)
                rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / Math.Max(1, results.Count)));
            AddCell("TARGET", 0, 0, Theme.TextFaint, true);
            AddCell("LATENCY", 1, 0, Theme.TextFaint, true);
            AddCell("LOSS", 2, 0, Theme.TextFaint, true);
            AddCell("STATUS", 3, 0, Theme.TextFaint, true);
            cells = new Label[results.Count, 4];
            for (int i = 0; i < results.Count; i++)
            {
                cells[i, 0] = AddCell(results[i].Name + "  ·  " + results[i].Target, 0, i + 1, Theme.Text, false);
                for (int c = 1; c < 4; c++) cells[i, c] = AddCell("—", c, i + 1, Theme.TextMuted, false);
            }
            rows.ResumeLayout();
        }

        private static void SetCell(Label label, string text, Color color)
        {
            if (label.Text != text) label.Text = text;
            if (label.ForeColor != color) label.ForeColor = color;
        }

        private void RenderRows()
        {
            if (cells == null) BuildRows();
            for (int i = 0; i < results.Count; i++)
            {
                MultiTargetResult result = results[i];
                SetCell(cells[i, 1], result.Success == true ? result.LatencyMs + " ms" : result.Success == false ? "TIMEOUT" : "—",
                    result.Success == false ? Theme.Bad : Theme.Text);
                SetCell(cells[i, 2], result.Attempts == 0 ? "—" : result.LossPercent.ToString("0.0") + "%",
                    result.LossPercent >= 5 ? Theme.Bad : Theme.TextMuted);
                Color color = result.Quality == "HEALTHY" ? Theme.Good : result.Quality == "FAIR" ? Theme.Warning :
                    result.Quality == "CHECKING" ? Theme.TextMuted : Theme.Bad;
                SetCell(cells[i, 3], result.Quality, color);
            }
            int unhealthy = results.Count(r => r.Quality == "POOR" || r.Quality == "CRITICAL");
            summary.Text = unhealthy == 0 ? "All monitored paths are currently healthy." :
                unhealthy + " monitored path" + (unhealthy == 1 ? " is" : "s are") + " degraded."
                + " Compare the rows to locate the failing segment.";
        }

        private Label AddCell(string text, int column, int row, Color color, bool header)
        {
            Label label = new Label { Text = text, Dock = DockStyle.Fill, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(9, 0, 4, 0),
                ForeColor = color, Font = Theme.UiFont(header ? 8f : 8.5f, header) };
            rows.Controls.Add(label, column, row);
            return label;
        }
    }
}
