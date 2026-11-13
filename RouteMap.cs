using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LagWatch
{
    internal sealed class RouteHop
    {
        public int Number { get; set; }
        public string Address { get; set; }
        public long LatencyMs { get; set; }
        public bool Responded { get; set; }
        /// <summary>What the hop most likely is (router, ISP edge, destination…), plus its reverse-DNS name when it has one.</summary>
        public string Device { get; set; }
    }

    /// <summary>Best-effort labels for route hops, from the address range, its position and reverse DNS.</summary>
    internal static class HopClassifier
    {
        internal static string Role(RouteHop hop, string gateway, ICollection<string> destination, bool isLast)
        {
            if (!hop.Responded) return "No reply · router doesn't answer traceroute";
            if (hop.Address == gateway) return "Your router";
            if (destination.Contains(hop.Address) || (isLast && destination.Count == 0)) return "Destination";
            byte[] b;
            if (!TryBytes(hop.Address, out b)) return "Router";
            bool privateRange = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
            if (privateRange) return hop.Number == 1 ? "Local network device" : "Private router (home or ISP internal)";
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "ISP carrier-grade NAT";
            return "Internet router"; // LabelAsync marks the first public one as the ISP edge
        }

        private static bool TryBytes(string address, out byte[] bytes)
        {
            bytes = null;
            System.Net.IPAddress ip;
            if (!System.Net.IPAddress.TryParse(address, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            bytes = ip.GetAddressBytes();
            return true;
        }

        /// <summary>Reverse-DNS name, or null; never waits more than the timeout.</summary>
        internal static async Task<string> ReverseDnsAsync(string address, int timeoutMs)
        {
            try
            {
                Task<System.Net.IPHostEntry> lookup = System.Net.Dns.GetHostEntryAsync(address);
                if (await Task.WhenAny(lookup, Task.Delay(timeoutMs)) != lookup) return null;
                string name = lookup.Result.HostName;
                return String.IsNullOrWhiteSpace(name) || name == address ? null : name;
            }
            catch { return null; }
        }

        /// <summary>Labels every hop; the reverse-DNS lookups run in parallel.</summary>
        internal static async Task LabelAsync(List<RouteHop> hops, string target)
        {
            string gateway = NetworkDiagnostics.GetDefaultGateway();
            HashSet<string> destination = new HashSet<string>();
            System.Net.IPAddress parsed;
            if (System.Net.IPAddress.TryParse(target, out parsed)) destination.Add(parsed.ToString());
            else
            {
                try
                {
                    Task<System.Net.IPAddress[]> resolve = System.Net.Dns.GetHostAddressesAsync(target);
                    if (await Task.WhenAny(resolve, Task.Delay(1500)) == resolve)
                        foreach (System.Net.IPAddress a in resolve.Result) destination.Add(a.ToString());
                }
                catch { }
            }

            string[] names = await Task.WhenAll(hops.Select(h => h.Responded ? ReverseDnsAsync(h.Address, 1500) : Task.FromResult<string>(null)));
            bool seenPublic = false;
            for (int i = 0; i < hops.Count; i++)
            {
                RouteHop hop = hops[i];
                string role = Role(hop, gateway, destination, i == hops.Count - 1);
                if (role == "Internet router" && !seenPublic) role = "ISP edge router";
                if (role == "Internet router" || role == "ISP edge router" || role == "Destination") seenPublic = true;
                hop.Device = names[i] == null ? role : role + " · " + names[i];
            }
        }
    }

    /// <summary>A grid that paints itself and its cell labels in one pass, so rows never show blank before their text.</summary>
    internal sealed class CompositedTable : TableLayoutPanel
    {
        public CompositedTable() { DoubleBuffered = true; }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; } // WS_EX_COMPOSITED
        }
    }

    internal sealed class RouteMapForm : Form, IEmbeddedTool
    {
        private bool started;

        /// <summary>Starts the tool's work once, whether it opens as a window (Shown) or as a page in the main window.</summary>
        public async System.Threading.Tasks.Task StartAsync()
        {
            if (started || IsDisposed) return;
            started = true;
            await TraceAsync();
        }

        private readonly string target;
        private readonly TableLayoutPanel rows;
        private readonly Label summary;
        private readonly AccentButton refreshButton;

        public RouteMapForm(string target, string note = null)
        {
            Theme.BeginDpiLayout(this);
            this.target = target;
            AutoScroll = true; // long routes scroll instead of squeezing rows
            Text = "LagWatch — Route Map";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(720, 560);
            MinimumSize = new Size(720, 560);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.UiFont(9f);

            Label title = new Label { Text = "ROUTE MAP", AutoSize = true,
                Font = Theme.UiFont(11f, true), ForeColor = Theme.Text, Location = new Point(22, 18) };
            Controls.Add(title);
            Label destination = new Label { Text = "Tracing route to " + target + " · ICMP hop response" + (note ?? ""), AutoSize = true,
                ForeColor = Theme.TextMuted, Location = new Point(23, 45), UseMnemonic = false };
            Controls.Add(destination);

            refreshButton = new AccentButton { Text = "TRACE ROUTE", Width = 120, Height = 32, Primary = true,
                Location = new Point(575, 18), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            refreshButton.Click += async delegate { await TraceAsync(); };
            Controls.Add(refreshButton);

            // The grid is as tall as its rows (a fixed-height grid gives all spare height to the last row).
            rows = new CompositedTable { Location = new Point(22, 82), Width = 676,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4, RowCount = 1, BackColor = Theme.Card, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            Controls.Add(rows);

            summary = new Label { Text = "Trace not started.", AutoSize = false, Height = 24, Width = 676,
                Location = new Point(22, 130), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = Theme.TextMuted };
            // The summary sits just under the grid, wherever the grid ends.
            rows.SizeChanged += delegate { summary.Top = rows.Bottom + Theme.Px(this, 12); };
            Controls.Add(summary);
            Shown += async delegate { await StartAsync(); };
            Theme.EndDpiLayout(this);
        }

        private async Task TraceAsync()
        {
            refreshButton.Enabled = false;
            summary.Text = "Tracing route…";
            ResetGrid(0);
            try
            {
                CommandResult command = await CommandRunner.RunAsync("tracert.exe", "-d -h 20 -w 800 " + target, 22000);
                List<RouteHop> hops = Parse(command.Output);
                if (IsDisposed) return;
                summary.Text = "Identifying devices…";
                await HopClassifier.LabelAsync(hops, target);
                if (IsDisposed) return;
                // Build every row while the grid is hidden and laid out once, so rows never paint blank before their text.
                rows.Visible = false;
                rows.SuspendLayout();
                ResetGrid(hops.Count);
                rows.RowCount = hops.Count + 1;
                rows.RowStyles.Clear();
                rows.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Px(this, 30)));
                foreach (RouteHop hop in hops)
                {
                    rows.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Px(this, 30)));
                    Color color = !hop.Responded ? Theme.Bad : hop.LatencyMs >= 120 ? Theme.Warning : Theme.Text;
                    AddCell(hop.Number.ToString(), 0, hop.Number, Theme.TextMuted, false);
                    AddCell(hop.Address, 1, hop.Number, color, false);
                    AddCell(hop.Device ?? "", 2, hop.Number, hop.Responded ? Theme.TextMuted : Theme.TextFaint, false);
                    AddCell(hop.Responded ? hop.LatencyMs + " ms" : "TIMEOUT", 3, hop.Number, color, false);
                }
                int missing = hops.Count(h => !h.Responded);
                summary.Text = hops.Count == 0 ? "No route data returned. Windows tracert may be blocked by the target or firewall." :
                    hops.Count + " hops observed · " + missing + " non-responding hop" + (missing == 1 ? "" : "s") +
                    " · higher latency often begins at or after the first highlighted hop.";
            }
            catch (Exception ex) { summary.Text = "Route trace failed · " + ex.Message; }
            finally
            {
                if (!IsDisposed)
                {
                    rows.ResumeLayout(true); // a no-op when the grid wasn't suspended
                    rows.Visible = true;
                    refreshButton.Enabled = true;
                }
            }
        }

        /// <summary>Clears the grid down to its header row (disposing the old labels).</summary>
        private void ResetGrid(int hopCount)
        {
            foreach (Control c in rows.Controls.Cast<Control>().ToList()) c.Dispose();
            rows.Controls.Clear();
            rows.RowStyles.Clear();
            rows.RowCount = 1;
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Px(this, 30)));
            AddCell("HOP", 0, 0, Theme.TextFaint, true);
            AddCell("ADDRESS", 1, 0, Theme.TextFaint, true);
            AddCell("DEVICE", 2, 0, Theme.TextFaint, true);
            AddCell("LATENCY", 3, 0, Theme.TextFaint, true);
        }

        private static List<RouteHop> Parse(string output)
        {
            List<RouteHop> result = new List<RouteHop>();
            if (String.IsNullOrWhiteSpace(output)) return result;
            Regex line = new Regex("^\\s*(\\d+)\\s+(.*)$");
            Regex address = new Regex("(\\d{1,3}\\.){3}\\d{1,3}");
            Regex latency = new Regex("<?\\s*(\\d+)\\s*ms", RegexOptions.IgnoreCase);
            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match match = line.Match(raw);
                if (!match.Success) continue;
                int number;
                if (!Int32.TryParse(match.Groups[1].Value, out number)) continue;
                Match ip = address.Match(match.Groups[2].Value);
                Match ms = latency.Match(match.Groups[2].Value);
                bool responded = ip.Success && ms.Success;
                result.Add(new RouteHop { Number = number, Address = ip.Success ? ip.Value : "* * *",
                    Responded = responded, LatencyMs = responded ? Int64.Parse(ms.Groups[1].Value) : 0 });
            }
            return result.OrderBy(h => h.Number).ToList();
        }

        private void AddCell(string text, int column, int row, Color color, bool header)
        {
            Label label = new Label { Text = text, Dock = DockStyle.Fill, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(9, 0, 4, 0), ForeColor = color,
                Font = Theme.UiFont(header ? 8f : 8.5f, header) };
            rows.Controls.Add(label, column, row);
        }
    }
}
