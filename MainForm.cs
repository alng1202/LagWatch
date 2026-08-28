using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LagWatch
{
    internal sealed class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private readonly Timer pingTimer;
        private readonly Timer gameDetectionTimer;
        private readonly List<PingSample> samples = new List<PingSample>();
        private bool monitoring;
        private bool pingInFlight;
        private bool? lastReplySuccessful;
        private bool suppressGameSelection;
        private int lastAutoDetectedProcessId;
        private string lastAutoServerTarget;
        private DateTime sessionStarted;
        private readonly LatencyHistoryStore latencyHistory;
        private DateTime lastGatewayEvidence = DateTime.MinValue;
        private SubscriptionTier subscriptionTier = SubscriptionTier.Free;

        private ComboBox targetBox;
        private AccentButton monitorButton;
        private AccentButton optimizeButton;
        private AccentButton diagnoseButton;
        private AccentButton exportButton;
        private AccentButton tierButton;
        private AccentButton predictionsButton;
        private AccentButton controllerButton;
        private Label tierBanner;
        private Label statusBadge;
        private Label currentValue;
        private Label currentDetail;
        private Label averageValue;
        private Label averageDetail;
        private Label jitterValue;
        private Label jitterDetail;
        private Label lossValue;
        private Label lossDetail;
        private Label sessionTarget;
        private Label sessionStartedLabel;
        private Label sessionAdapter;
        private Label sessionSamples;
        private ComboBox gameProcessBox;
        private CheckBox autoDetectGameCheck;
        private Label gameDetectionStatus;
        private LatencyChart chart;
        private RichTextBox eventLog;

        public MainForm()
        {
            Text = "LagWatch — Game Network Monitor";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1040, 700);
            Size = new Size(1180, 790);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;

            BuildInterface();
            latencyHistory = new LatencyHistoryStore(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "lagwatch-history.csv"));
            ApplySubscriptionTier();
            RefreshGameProcesses(false);

            pingTimer = new Timer();
            pingTimer.Interval = 1000;
            pingTimer.Tick += async delegate { await PerformPingAsync(); };

            gameDetectionTimer = new Timer();
            gameDetectionTimer.Interval = 4000;
            gameDetectionTimer.Tick += delegate { AutoDetectGame(); };

            Shown += delegate
            {
                AppendLog("Ready. Choose an endpoint near your game region and start monitoring.", Theme.TextMuted);
                AutoDetectGame();
                gameDetectionTimer.Start();
                targetBox.Focus();
            };
            FormClosing += delegate { pingTimer.Stop(); gameDetectionTimer.Stop(); };
            KeyDown += MainForm_KeyDown;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int enabled = 1;
                int result = DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
                if (result != 0)
                    DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
            }
            catch
            {
                // Older Windows versions keep their normal title bar.
            }
        }

        private void BuildInterface()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(24, 20, 24, 22);
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
            Controls.Add(root);

            root.Controls.Add(CreateHeader(), 0, 0);
            root.Controls.Add(CreateToolbar(), 0, 1);
            root.Controls.Add(CreateMetricRow(), 0, 2);
            root.Controls.Add(CreateMainArea(), 0, 3);
            root.Controls.Add(CreateLogArea(), 0, 4);
        }

        private Control CreateHeader()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;

            Label title = new Label();
            title.Text = "LAGWATCH";
            title.AutoSize = true;
            title.Font = new Font("Segoe UI Black", 19f);
            title.ForeColor = Theme.Text;
            title.Location = new Point(0, 0);
            panel.Controls.Add(title);

            Label accent = new Label();
            accent.Text = "//";
            accent.AutoSize = true;
            accent.Font = title.Font;
            accent.ForeColor = Theme.Accent;
            accent.Location = new Point(title.PreferredWidth + 7, 0);
            panel.Controls.Add(accent);

            Label subtitle = new Label();
            subtitle.Text = "Live latency intelligence for competitive play";
            subtitle.AutoSize = true;
            subtitle.Font = new Font("Segoe UI", 9.5f);
            subtitle.ForeColor = Theme.TextMuted;
            subtitle.Location = new Point(2, 37);
            panel.Controls.Add(subtitle);

            statusBadge = new Label();
            statusBadge.Text = "  IDLE  ";
            statusBadge.AutoSize = true;
            statusBadge.Font = new Font("Segoe UI Semibold", 9f);
            statusBadge.ForeColor = Theme.TextMuted;
            statusBadge.BackColor = Theme.CardAlt;
            statusBadge.Padding = new Padding(7, 5, 7, 5);
            statusBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            panel.Controls.Add(statusBadge);

            tierBanner = new Label();
            tierBanner.Text = "FREE · CORE NETWORK MONITOR";
            tierBanner.AutoSize = true;
            tierBanner.Font = new Font("Segoe UI Semibold", 8.5f);
            tierBanner.ForeColor = Theme.TextMuted;
            tierBanner.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            panel.Controls.Add(tierBanner);

            panel.Resize += delegate
            {
                statusBadge.Location = new Point(panel.ClientSize.Width - statusBadge.Width, 5);
                tierBanner.Location = new Point(panel.ClientSize.Width - tierBanner.Width, 38);
            };
            return panel;
        }

        private Control CreateToolbar()
        {
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.FlowDirection = FlowDirection.LeftToRight;
            bar.WrapContents = false;
            bar.Padding = new Padding(0, 5, 0, 5);
            bar.BackColor = Theme.Background;

            Label label = new Label();
            label.Text = "PING TARGET";
            label.AutoSize = false;
            label.Width = 86;
            label.Height = 36;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Font = new Font("Segoe UI Semibold", 8.5f);
            label.ForeColor = Theme.TextMuted;
            label.Margin = new Padding(0, 0, 4, 0);
            bar.Controls.Add(label);

            targetBox = new ComboBox();
            targetBox.Width = 206;
            targetBox.Height = 36;
            targetBox.DropDownStyle = ComboBoxStyle.DropDown;
            targetBox.FlatStyle = FlatStyle.Flat;
            targetBox.Font = new Font("Segoe UI", 10f);
            targetBox.BackColor = Theme.CardAlt;
            targetBox.ForeColor = Theme.Text;
            targetBox.Items.AddRange(new object[]
            {
                "1.1.1.1", "8.8.8.8", "9.9.9.9", "cloudflare.com"
            });
            targetBox.Text = "1.1.1.1";
            targetBox.Margin = new Padding(0, 2, 12, 0);
            bar.Controls.Add(targetBox);

            monitorButton = new AccentButton();
            monitorButton.Text = "START  [F5]";
            monitorButton.Width = 116;
            monitorButton.Primary = true;
            monitorButton.Margin = new Padding(0, 0, 8, 0);
            monitorButton.Click += async delegate { await ToggleMonitoringAsync(); };
            bar.Controls.Add(monitorButton);

            optimizeButton = new AccentButton();
            optimizeButton.Text = "SAFE OPTIMIZE";
            optimizeButton.Width = 128;
            optimizeButton.Margin = new Padding(0, 0, 8, 0);
            optimizeButton.Click += async delegate { await OptimizeAsync(); };
            bar.Controls.Add(optimizeButton);

            diagnoseButton = new AccentButton();
            diagnoseButton.Text = "DIAGNOSE";
            diagnoseButton.Width = 104;
            diagnoseButton.Margin = new Padding(0, 0, 0, 0);
            diagnoseButton.Click += async delegate { await DiagnoseAsync(); };
            bar.Controls.Add(diagnoseButton);

#if DEV_BUILD
            tierButton = new AccentButton();
            tierButton.Text = "TIER: FREE";
            tierButton.Width = 112;
            tierButton.Margin = new Padding(8, 0, 0, 0);
            tierButton.Click += delegate { CycleSubscriptionTier(); };
            bar.Controls.Add(tierButton);
#endif

            predictionsButton = new AccentButton();
            predictionsButton.Text = "PREDICTIONS";
            predictionsButton.Width = 112;
            predictionsButton.Margin = new Padding(8, 0, 0, 0);
            predictionsButton.Click += delegate { ShowPredictionDataset(); };
            bar.Controls.Add(predictionsButton);

            controllerButton = new AccentButton();
            controllerButton.Text = "OUTAGE MAP";
            controllerButton.Width = 116;
            controllerButton.Margin = new Padding(8, 0, 0, 0);
            controllerButton.Click += delegate
            {
                using (OutageMapForm outageMap = new OutageMapForm()) outageMap.ShowDialog(this);
            };
            bar.Controls.Add(controllerButton);

            ToolTip tips = new ToolTip();
            tips.SetToolTip(targetBox, "IP address or host name. For the best result, use your game server when known.");
            tips.SetToolTip(optimizeButton, "Flushes the Windows DNS cache and raises the selected game's process priority.");
            tips.SetToolTip(diagnoseButton, "Checks the active adapter, gateway, DNS, link speed, and target response.");
#if DEV_BUILD
            tips.SetToolTip(tierButton, "Developer build: cycle through Free, Basic, and Pro entitlements.");
#endif
            tips.SetToolTip(predictionsButton, "Inspect local prediction training data and readiness.");
            tips.SetToolTip(controllerButton, "Detect your ISP and inspect local, provider, and global outage signals.");
            return bar;
        }

        private Control CreateMetricRow()
        {
            TableLayoutPanel metrics = new TableLayoutPanel();
            metrics.Dock = DockStyle.Fill;
            metrics.ColumnCount = 4;
            metrics.RowCount = 1;
            metrics.Padding = new Padding(0, 7, 0, 9);
            for (int i = 0; i < 4; i++)
                metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

            metrics.Controls.Add(CreateMetricCard("CURRENT LATENCY", out currentValue, out currentDetail), 0, 0);
            metrics.Controls.Add(CreateMetricCard("ROLLING AVERAGE", out averageValue, out averageDetail), 1, 0);
            metrics.Controls.Add(CreateMetricCard("JITTER", out jitterValue, out jitterDetail), 2, 0);
            metrics.Controls.Add(CreateMetricCard("PACKET LOSS", out lossValue, out lossDetail), 3, 0);

            currentDetail.Text = "latest reply";
            averageDetail.Text = "successful samples";
            jitterDetail.Text = "mean variation";
            lossDetail.Text = "timeouts / failures";
            return metrics;
        }

        private Control CreateMetricCard(string caption, out Label value, out Label detail)
        {
            CardPanel card = new CardPanel();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 10, 0);
            card.Padding = new Padding(18, 10, 12, 8);

            Panel stripe = new Panel();
            stripe.Width = 3;
            stripe.Location = new Point(0, 0);
            stripe.Height = card.Height;
            stripe.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            stripe.BackColor = Theme.Accent;
            card.Controls.Add(stripe);

            Label title = new Label();
            title.Text = caption;
            title.AutoSize = true;
            title.Font = new Font("Segoe UI Semibold", 8f);
            title.ForeColor = Theme.TextMuted;
            title.Location = new Point(18, 11);
            card.Controls.Add(title);

            value = new Label();
            value.Text = "—";
            value.AutoSize = true;
            value.Font = new Font("Segoe UI Semibold", 20f);
            value.ForeColor = Theme.Text;
            value.Location = new Point(16, 31);
            card.Controls.Add(value);

            detail = new Label();
            detail.AutoSize = true;
            detail.Font = new Font("Segoe UI", 8f);
            detail.ForeColor = Theme.TextFaint;
            detail.Location = new Point(18, 66);
            card.Controls.Add(detail);
            return card;
        }

        private Control CreateMainArea()
        {
            TableLayoutPanel area = new TableLayoutPanel();
            area.Dock = DockStyle.Fill;
            area.ColumnCount = 2;
            area.RowCount = 1;
            area.Padding = new Padding(0, 0, 0, 10);
            area.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
            area.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));

            CardPanel chartCard = new CardPanel();
            chartCard.Dock = DockStyle.Fill;
            chartCard.Margin = new Padding(0, 0, 10, 0);
            chartCard.Padding = new Padding(14, 10, 14, 10);

            Panel chartHeader = new Panel();
            chartHeader.Dock = DockStyle.Top;
            chartHeader.Height = 34;
            Label chartTitle = new Label();
            chartTitle.Text = "LATENCY TIMELINE";
            chartTitle.AutoSize = true;
            chartTitle.Font = new Font("Segoe UI Semibold", 9f);
            chartTitle.ForeColor = Theme.Text;
            chartTitle.Location = new Point(1, 5);
            chartHeader.Controls.Add(chartTitle);

            exportButton = new AccentButton();
            exportButton.Text = "EXPORT CSV";
            exportButton.Size = new Size(102, 28);
            exportButton.Font = new Font("Segoe UI Semibold", 8f);
            exportButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            exportButton.Click += delegate { ExportCsv(); };
            chartHeader.Controls.Add(exportButton);
            chartHeader.Resize += delegate
            {
                exportButton.Location = new Point(chartHeader.ClientSize.Width - exportButton.Width, 0);
            };

            chart = new LatencyChart();
            chart.Dock = DockStyle.Fill;
            chartCard.Controls.Add(chart);
            chartCard.Controls.Add(chartHeader);
            area.Controls.Add(chartCard, 0, 0);

            area.Controls.Add(CreateSessionCard(), 1, 0);
            return area;
        }

        private Control CreateSessionCard()
        {
            CardPanel card = new CardPanel();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0);
            card.Padding = new Padding(18, 15, 18, 12);

            Label title = new Label();
            title.Text = "SESSION";
            title.AutoSize = true;
            title.Font = new Font("Segoe UI Semibold", 9f);
            title.ForeColor = Theme.Text;
            title.Location = new Point(18, 15);
            card.Controls.Add(title);

            sessionTarget = CreateSessionLine(card, "Target", "1.1.1.1", 45);
            sessionStartedLabel = CreateSessionLine(card, "Started", "Not running", 68);
            sessionAdapter = CreateSessionLine(card, "Adapter", NetworkDiagnostics.GetActiveAdapterSummary(), 91);
            sessionSamples = CreateSessionLine(card, "Samples", "0", 114);

            Panel divider = new Panel();
            divider.Height = 1;
            divider.BackColor = Theme.Border;
            divider.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            divider.Location = new Point(18, 143);
            divider.Width = card.ClientSize.Width - 36;
            card.Controls.Add(divider);

            Label gameLabel = new Label();
            gameLabel.Text = "GAME PROCESS FOR PRIORITY BOOST";
            gameLabel.AutoSize = true;
            gameLabel.Font = new Font("Segoe UI Semibold", 8f);
            gameLabel.ForeColor = Theme.TextMuted;
            gameLabel.Location = new Point(18, 157);
            card.Controls.Add(gameLabel);

            gameProcessBox = new ComboBox();
            gameProcessBox.DropDownStyle = ComboBoxStyle.DropDown;
            gameProcessBox.FlatStyle = FlatStyle.Flat;
            gameProcessBox.BackColor = Theme.CardAlt;
            gameProcessBox.ForeColor = Theme.Text;
            gameProcessBox.Font = new Font("Segoe UI", 8.5f);
            gameProcessBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            gameProcessBox.AutoCompleteSource = AutoCompleteSource.ListItems;
            gameProcessBox.Location = new Point(18, 179);
            gameProcessBox.Height = 28;
            gameProcessBox.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            gameProcessBox.SelectionChangeCommitted += delegate
            {
                if (suppressGameSelection) return;
                autoDetectGameCheck.Checked = false;
                GameProcessOption selected = gameProcessBox.SelectedItem as GameProcessOption;
                gameDetectionStatus.Text = selected == null || selected.ProcessId == 0
                    ? "Manual mode · no game selected"
                    : "Manual selection · " + selected.ProcessName;
                gameDetectionStatus.ForeColor = Theme.TextMuted;
            };
            card.Controls.Add(gameProcessBox);

            AccentButton refresh = new AccentButton();
            refresh.Text = "↻";
            refresh.Font = new Font("Segoe UI Symbol", 10f);
            refresh.Size = new Size(34, 28);
            refresh.Location = new Point(card.ClientSize.Width - 52, 178);
            refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            refresh.Click += delegate
            {
                RefreshGameProcesses(true);
                if (autoDetectGameCheck.Checked) AutoDetectGame();
            };
            card.Controls.Add(refresh);

            autoDetectGameCheck = new CheckBox();
            autoDetectGameCheck.Text = "Auto-detect running games";
            autoDetectGameCheck.Checked = true;
            autoDetectGameCheck.AutoSize = true;
            autoDetectGameCheck.Font = new Font("Segoe UI Semibold", 8.5f);
            autoDetectGameCheck.ForeColor = Theme.TextMuted;
            autoDetectGameCheck.Location = new Point(18, 213);
            autoDetectGameCheck.CheckedChanged += delegate
            {
                if (autoDetectGameCheck.Checked)
                {
                    gameDetectionStatus.Text = "Scanning running apps…";
                    gameDetectionStatus.ForeColor = Theme.TextFaint;
                    AutoDetectGame();
                }
                else if (!suppressGameSelection)
                {
                    gameDetectionStatus.Text = "Manual game selection enabled";
                    gameDetectionStatus.ForeColor = Theme.TextFaint;
                }
            };
            card.Controls.Add(autoDetectGameCheck);

            gameDetectionStatus = new Label();
            gameDetectionStatus.Text = "Scanning running apps…";
            gameDetectionStatus.AutoEllipsis = true;
            gameDetectionStatus.AutoSize = false;
            gameDetectionStatus.Height = 20;
            gameDetectionStatus.Font = new Font("Segoe UI", 8f);
            gameDetectionStatus.ForeColor = Theme.TextFaint;
            gameDetectionStatus.Location = new Point(18, 237);
            gameDetectionStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            card.Controls.Add(gameDetectionStatus);

            Label hint = new Label();
            hint.Text = "Detection stays local. Priority changes only when you press Safe Optimize.";
            hint.AutoSize = false;
            hint.Height = 34;
            hint.Font = new Font("Segoe UI", 8f);
            hint.ForeColor = Theme.TextFaint;
            hint.Location = new Point(18, 260);
            hint.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            card.Controls.Add(hint);

            card.Resize += delegate
            {
                divider.Width = Math.Max(30, card.ClientSize.Width - 36);
                gameProcessBox.Width = Math.Max(80, card.ClientSize.Width - 78);
                gameDetectionStatus.Width = Math.Max(80, card.ClientSize.Width - 36);
                hint.Width = Math.Max(80, card.ClientSize.Width - 36);
            };
            return card;
        }

        private static Label CreateSessionLine(Control parent, string name, string value, int top)
        {
            Label caption = new Label();
            caption.Text = name;
            caption.AutoSize = true;
            caption.Font = new Font("Segoe UI", 8.5f);
            caption.ForeColor = Theme.TextFaint;
            caption.Location = new Point(18, top);
            parent.Controls.Add(caption);

            Label data = new Label();
            data.Text = value;
            data.AutoEllipsis = true;
            data.TextAlign = ContentAlignment.MiddleRight;
            data.Font = new Font("Segoe UI", 8.5f);
            data.ForeColor = Theme.TextMuted;
            data.Location = new Point(96, top - 1);
            data.Height = 20;
            data.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            parent.Controls.Add(data);
            parent.Resize += delegate { data.Width = Math.Max(60, parent.ClientSize.Width - 114); };
            return data;
        }

        private Control CreateLogArea()
        {
            CardPanel card = new CardPanel();
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0);
            card.Padding = new Padding(14, 10, 14, 10);

            Label title = new Label();
            title.Text = "EVENT STREAM";
            title.Dock = DockStyle.Top;
            title.Height = 26;
            title.Font = new Font("Segoe UI Semibold", 9f);
            title.ForeColor = Theme.Text;
            card.Controls.Add(title);

            eventLog = new RichTextBox();
            eventLog.Dock = DockStyle.Fill;
            eventLog.BorderStyle = BorderStyle.None;
            eventLog.BackColor = Theme.Card;
            eventLog.ForeColor = Theme.TextMuted;
            eventLog.Font = new Font("Consolas", 8.5f);
            eventLog.ReadOnly = true;
            eventLog.DetectUrls = false;
            eventLog.TabStop = false;
            card.Controls.Add(eventLog);
            eventLog.BringToFront();
            return card;
        }

        private async Task ToggleMonitoringAsync()
        {
            if (monitoring)
            {
                StopMonitoring();
                return;
            }

            string target = targetBox.Text.Trim();
            if (!TargetValidator.IsValid(target))
            {
                MessageBox.Show(this,
                    "Enter a valid IP address or host name without a URL, path, or port.",
                    "Invalid ping target", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                targetBox.Focus();
                return;
            }

            samples.Clear();
            chart.SetSamples(samples);
            lastReplySuccessful = null;
            sessionStarted = DateTime.Now;
            monitoring = true;
            targetBox.Enabled = false;
            monitorButton.Text = "STOP  [F5]";
            monitorButton.Primary = false;
            sessionTarget.Text = target;
            sessionStartedLabel.Text = sessionStarted.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            sessionSamples.Text = "0";
            sessionAdapter.Text = NetworkDiagnostics.GetActiveAdapterSummary();
            SetStatus("CONNECTING", Theme.Warning);
            AppendLog("Monitoring started → " + target, Theme.Accent);
            pingTimer.Start();
            await PerformPingAsync();
        }

        private void StopMonitoring()
        {
            pingTimer.Stop();
            monitoring = false;
            targetBox.Enabled = true;
            monitorButton.Text = "START  [F5]";
            monitorButton.Primary = true;
            sessionStartedLabel.Text = "Paused " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            SetStatus("PAUSED", Theme.TextMuted);
            AppendLog("Monitoring paused.", Theme.TextMuted);
        }

        private async Task PerformPingAsync()
        {
            if (!monitoring || pingInFlight)
                return;

            pingInFlight = true;
            PingSample sample = new PingSample { Timestamp = DateTime.Now, Success = false, LatencyMs = 0 };
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = await ping.SendPingAsync(targetBox.Text.Trim(), 1400);
                    sample.Success = reply.Status == IPStatus.Success;
                    sample.LatencyMs = sample.Success ? reply.RoundtripTime : 0;
                    if (!sample.Success && (!lastReplySuccessful.HasValue || lastReplySuccessful.Value))
                        AppendLog("Ping failure: " + reply.Status, Theme.Bad);
                }
            }
            catch (Exception ex)
            {
                if (!lastReplySuccessful.HasValue || lastReplySuccessful.Value)
                    AppendLog("Ping error: " + ex.Message, Theme.Bad);
            }
            finally
            {
                pingInFlight = false;
            }

            if (lastReplySuccessful.HasValue && !lastReplySuccessful.Value && sample.Success)
                AppendLog("Target replies recovered at " + sample.LatencyMs + " ms.", Theme.Good);
            lastReplySuccessful = sample.Success;

            samples.Add(sample);
            try { latencyHistory.Record(sample.Timestamp, targetBox.Text.Trim(), sample.Success, sample.LatencyMs); }
            catch (IOException) { }
            await RecordGatewayEvidenceAsync(sample.Timestamp);
            if (samples.Count > 600)
                samples.RemoveAt(0);
            chart.SetSamples(samples);
            UpdateMetrics();
        }

        private void UpdateMetrics()
        {
            NetworkSnapshot snapshot = StatisticsCalculator.Calculate(samples);
            currentValue.Text = snapshot.CurrentMs < 0 ? "TIMEOUT" : snapshot.CurrentMs + " ms";
            currentValue.ForeColor = snapshot.CurrentMs < 0 ? Theme.Bad : MetricColor(snapshot.CurrentMs, 80, 140);
            averageValue.Text = snapshot.SuccessfulSamples == 0 ? "—" : snapshot.AverageMs.ToString("0.0") + " ms";
            averageValue.ForeColor = snapshot.SuccessfulSamples == 0 ? Theme.Text : MetricColor(snapshot.AverageMs, 80, 140);
            jitterValue.Text = snapshot.SuccessfulSamples < 2 ? "—" : snapshot.JitterMs.ToString("0.0") + " ms";
            jitterValue.ForeColor = snapshot.SuccessfulSamples < 2 ? Theme.Text : MetricColor(snapshot.JitterMs, 20, 40);
            lossValue.Text = snapshot.LossPercent.ToString("0.0") + "%";
            lossValue.ForeColor = MetricColor(snapshot.LossPercent, 2, 5);
            averageDetail.Text = snapshot.SuccessfulSamples + " successful samples";
            if (snapshot.CurrentMs < 0)
                currentDetail.Text = "no ICMP reply";
            else
            {
                if (!SubscriptionPolicy.CanUsePredictions(subscriptionTier))
                    currentDetail.Text = "latest reply · prediction in Basic";
                else
                {
                    LatencyPrediction prediction = null;
                    try { prediction = latencyHistory.Predict(DateTime.Now, targetBox.Text.Trim()); }
                    catch (IOException) { }
                    currentDetail.Text = prediction != null && prediction.Ready
                        ? (prediction.FailurePercent >= 10
                            ? "historical instability risk · " + prediction.FailurePercent.ToString("0") + "% failures"
                            : "expected now ≈ " + prediction.ExpectedMs.ToString("0") + " ms")
                        : "prediction learning locally";
                }
            }
            sessionSamples.Text = snapshot.TotalSamples.ToString(CultureInfo.InvariantCulture);

            Color qualityColor = Theme.TextMuted;
            switch (snapshot.Quality)
            {
                case "EXCELLENT":
                case "GOOD": qualityColor = Theme.Good; break;
                case "FAIR": qualityColor = Theme.Warning; break;
                case "POOR":
                case "CRITICAL": qualityColor = Theme.Bad; break;
            }
            SetStatus(snapshot.Quality, qualityColor);
        }

        private async Task OptimizeAsync()
        {
            optimizeButton.Enabled = false;
            List<string> results = new List<string>();
            AppendLog("Safe optimization started.", Theme.AccentBlue);
            try
            {
                if (autoDetectGameCheck.Checked)
                    AutoDetectGame();

                string ipconfig = Path.Combine(Environment.SystemDirectory, "ipconfig.exe");
                CommandResult flush = await CommandRunner.RunAsync(ipconfig, "/flushdns", 7000);
                if (!flush.TimedOut && flush.ExitCode == 0)
                {
                    results.Add("✓ Windows DNS cache flushed");
                    AppendLog("DNS resolver cache flushed successfully.", Theme.Good);
                }
                else
                {
                    string detail = String.IsNullOrWhiteSpace(flush.Error) ? "command failed" : flush.Error.Trim();
                    results.Add("! DNS cache: " + detail);
                    AppendLog("DNS flush did not complete: " + detail, Theme.Warning);
                }

                GameProcessOption selected = gameProcessBox.SelectedItem as GameProcessOption;
                if (selected != null && selected.ProcessId > 0)
                {
                    try
                    {
                        using (Process process = Process.GetProcessById(selected.ProcessId))
                        {
                            process.PriorityClass = ProcessPriorityClass.AboveNormal;
                            results.Add("✓ " + selected.ProcessName + " priority set to Above normal");
                            AppendLog("Game priority boosted: " + selected.ProcessName + " (PID " + selected.ProcessId + ").", Theme.Good);
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add("! Game priority: " + ex.Message);
                        AppendLog("Could not boost game priority: " + ex.Message, Theme.Warning);
                    }
                }
                else
                {
                    results.Add("• No game process selected; priority was unchanged");
                    AppendLog("No game process selected; skipped priority boost.", Theme.TextMuted);
                }

                results.Add("• No registry, TCP stack, DNS provider, or adapter settings were changed");
                MessageBox.Show(this, String.Join(Environment.NewLine, results),
                    "LagWatch safe optimization", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("Optimization error: " + ex.Message, Theme.Bad);
                MessageBox.Show(this, ex.Message, "Optimization could not finish",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                optimizeButton.Enabled = true;
            }
        }

        private async Task DiagnoseAsync()
        {
            if (!SubscriptionPolicy.CanUseAdvancedDiagnostics(subscriptionTier))
            {
                ShowTierRequirement("Advanced diagnostics", SubscriptionTier.Pro);
                return;
            }
            string target = targetBox.Text.Trim();
            if (!TargetValidator.IsValid(target))
            {
                MessageBox.Show(this, "Enter a valid ping target before running diagnostics.",
                    "Invalid target", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            diagnoseButton.Enabled = false;
            SetStatus("DIAGNOSING", Theme.AccentBlue);
            AppendLog("Inspecting active adapter, gateway, DNS, and target…", Theme.AccentBlue);
            try
            {
                DiagnosticReport report = await NetworkDiagnostics.AnalyzeAsync(target);
                sessionAdapter.Text = report.AdapterName + " · " + report.AdapterType;
                AppendLog("Diagnostics complete for " + report.AdapterName + ".", Theme.Good);
                MessageBox.Show(this, report.ToDisplayText(target), "LagWatch diagnostics",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("Diagnostics error: " + ex.Message, Theme.Bad);
                MessageBox.Show(this, ex.Message, "Diagnostics could not finish",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                diagnoseButton.Enabled = true;
                if (monitoring) UpdateMetrics();
                else SetStatus("IDLE", Theme.TextMuted);
            }
        }

        private void RefreshGameProcesses(bool writeLog)
        {
            int previousId = 0;
            GameProcessOption previous = gameProcessBox == null ? null : gameProcessBox.SelectedItem as GameProcessOption;
            if (previous != null) previousId = previous.ProcessId;

            gameProcessBox.Items.Clear();
            gameProcessBox.Items.Add(new GameProcessOption
            {
                DisplayName = "None selected",
                ProcessId = 0,
                ProcessName = ""
            });

            int preferredIndex = 0;
            foreach (GameProcessOption option in GameProcessService.GetVisibleProcesses())
            {
                int index = gameProcessBox.Items.Add(option);
                if (option.ProcessId == previousId)
                    preferredIndex = index;
            }
            suppressGameSelection = true;
            try { gameProcessBox.SelectedIndex = preferredIndex; }
            finally { suppressGameSelection = false; }
            if (writeLog && eventLog != null)
                AppendLog("Running app list refreshed.", Theme.TextFaint);
        }

        private void AutoDetectGame()
        {
            if (autoDetectGameCheck == null || !autoDetectGameCheck.Checked || gameProcessBox == null)
                return;

            GameProcessOption detected;
            try
            {
                detected = GameProcessService.DetectRunningGame();
            }
            catch (Exception ex)
            {
                gameDetectionStatus.Text = "Detection unavailable · " + ex.Message;
                gameDetectionStatus.ForeColor = Theme.Warning;
                return;
            }

            if (detected == null)
            {
                if (lastAutoDetectedProcessId != 0)
                {
                    RefreshGameProcesses(false);
                    lastAutoDetectedProcessId = 0;
                    AppendLog("The detected game closed. Auto-detection resumed scanning.", Theme.TextMuted);
                }
                gameDetectionStatus.Text = "No game detected · scanning every 4 seconds";
                gameDetectionStatus.ForeColor = Theme.TextFaint;
                return;
            }

            int matchIndex = -1;
            for (int i = 0; i < gameProcessBox.Items.Count; i++)
            {
                GameProcessOption item = gameProcessBox.Items[i] as GameProcessOption;
                if (item != null && item.ProcessId == detected.ProcessId)
                {
                    matchIndex = i;
                    detected = item;
                    break;
                }
            }

            if (matchIndex < 0)
            {
                RefreshGameProcesses(false);
                for (int i = 0; i < gameProcessBox.Items.Count; i++)
                {
                    GameProcessOption item = gameProcessBox.Items[i] as GameProcessOption;
                    if (item != null && item.ProcessId == detected.ProcessId)
                    {
                        matchIndex = i;
                        detected = item;
                        break;
                    }
                }
            }

            if (matchIndex >= 0)
            {
                suppressGameSelection = true;
                try { gameProcessBox.SelectedIndex = matchIndex; }
                finally { suppressGameSelection = false; }
            }

            gameDetectionStatus.Text = "Detected · " + detected.ProcessName + " · " + detected.DetectionReason;
            gameDetectionStatus.ForeColor = Theme.Good;
            TryUseDetectedGameServer(detected);
            if (lastAutoDetectedProcessId != detected.ProcessId)
            {
                AppendLog("Game auto-detected: " + detected.DisplayName + " [" + detected.DetectionReason + "].", Theme.Good);
                lastAutoDetectedProcessId = detected.ProcessId;
            }
        }

        private void TryUseDetectedGameServer(GameProcessOption game)
        {
            GameServerEndpoint endpoint;
            try { endpoint = GameServerDiscovery.FindBestEndpoint(game.ProcessId); }
            catch { return; }
            if (endpoint == null)
            {
                if (lastAutoDetectedProcessId != game.ProcessId)
                    AppendLog("No public TCP endpoint is visible for " + game.ProcessName +
                        ". UDP server discovery requires the native WFP telemetry component.", Theme.TextFaint);
                return;
            }
            if (String.Equals(lastAutoServerTarget, endpoint.Address, StringComparison.OrdinalIgnoreCase)) return;

            string oldTarget = targetBox.Text.Trim();
            lastAutoServerTarget = endpoint.Address;
            targetBox.Text = endpoint.Address;
            if (monitoring && !String.Equals(oldTarget, endpoint.Address, StringComparison.OrdinalIgnoreCase))
            {
                samples.Clear(); chart.SetSamples(samples); lastReplySuccessful = null;
                sessionTarget.Text = endpoint.Address; sessionSamples.Text = "0";
            }
            gameDetectionStatus.Text = "Detected · " + game.ProcessName + " · " + endpoint.Protocol +
                " server " + endpoint.Address + ":" + endpoint.Port;
            AppendLog("Game endpoint discovered → " + endpoint.Address + ":" + endpoint.Port +
                " (ICMP reference; game transport latency may differ).", Theme.Accent);
        }

        private void ExportCsv()
        {
            if (!SubscriptionPolicy.CanExportHistory(subscriptionTier))
            {
                ShowTierRequirement("Session export", SubscriptionTier.Basic);
                return;
            }
            if (samples.Count == 0)
            {
                MessageBox.Show(this, "There are no samples to export yet.", "Nothing to export",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string fileName = "LagWatch-session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv";
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            try
            {
                using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(true)))
                {
                    writer.WriteLine("timestamp,target,success,latency_ms");
                    foreach (PingSample sample in samples)
                    {
                        writer.WriteLine(String.Join(",", new[]
                        {
                            Csv(sample.Timestamp.ToString("o", CultureInfo.InvariantCulture)),
                            Csv(sessionTarget.Text),
                            sample.Success ? "true" : "false",
                            sample.Success ? sample.LatencyMs.ToString(CultureInfo.InvariantCulture) : ""
                        }));
                    }
                }
                AppendLog("Session exported → " + fileName, Theme.Good);
                MessageBox.Show(this, "Saved beside LagWatch.exe:" + Environment.NewLine + fileName,
                    "CSV exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not export CSV",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                BeginInvoke(new Action(async delegate { await ToggleMonitoringAsync(); }));
            }
            else if (e.Control && e.KeyCode == Keys.E)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                ExportCsv();
            }
        }

        private void CycleSubscriptionTier()
        {
            subscriptionTier = subscriptionTier == SubscriptionTier.Free
                ? SubscriptionTier.Basic
                : subscriptionTier == SubscriptionTier.Basic ? SubscriptionTier.Pro : SubscriptionTier.Free;
            ApplySubscriptionTier();
        }

        private void ApplySubscriptionTier()
        {
            if (tierButton != null)
                tierButton.Text = "TIER: " + subscriptionTier.ToString().ToUpperInvariant();
            predictionsButton.Visible = subscriptionTier != SubscriptionTier.Free;
            controllerButton.Visible = subscriptionTier == SubscriptionTier.Pro;
            diagnoseButton.Text = SubscriptionPolicy.CanUseAdvancedDiagnostics(subscriptionTier)
                ? "DIAGNOSE" : "DIAGNOSE 🔒";
            exportButton.Text = SubscriptionPolicy.CanExportHistory(subscriptionTier)
                ? "EXPORT CSV  [CTRL+E]" : "EXPORT CSV 🔒";
            Color identity;
            if (subscriptionTier == SubscriptionTier.Basic)
            {
                identity = Color.FromArgb(167, 139, 250);
                tierBanner.Text = "BASIC · PREDICTIVE LATENCY INTELLIGENCE";
            }
            else if (subscriptionTier == SubscriptionTier.Pro)
            {
                identity = Color.FromArgb(251, 191, 36);
                tierBanner.Text = "PRO · ADAPTIVE CONTROL LAB";
            }
            else
            {
                identity = Theme.Accent;
                tierBanner.Text = "FREE · CORE NETWORK MONITOR";
            }
            tierBanner.ForeColor = identity;
            if (tierButton != null) tierButton.AccentColor = identity;
            predictionsButton.AccentColor = identity;
            controllerButton.AccentColor = identity;
            if (tierButton != null) tierButton.Primary = true;
            predictionsButton.Primary = true;
            controllerButton.Primary = true;
            AppendLog("Developer tier switched to " + subscriptionTier.ToString().ToUpperInvariant() +
                " · " + SubscriptionPolicy.Summary(subscriptionTier) + ".", Theme.AccentBlue);
            if (subscriptionTier == SubscriptionTier.Pro)
                AppendLog("Adaptive traffic control is a monitor-only preview; no Windows network limits are applied.", Theme.Warning);
            if (monitoring) UpdateMetrics();
        }

        private void ShowPredictionDataset()
        {
            if (!SubscriptionPolicy.CanUsePredictions(subscriptionTier))
            {
                ShowTierRequirement("Historical predictions", SubscriptionTier.Basic);
                return;
            }
            string target = targetBox.Text.Trim(); HistoryDatasetStats stats;
            try { stats = latencyHistory.GetStats(target); }
            catch (IOException ex)
            {
                MessageBox.Show(this, ex.Message, "Could not read prediction data",
                    MessageBoxButtons.OK, MessageBoxIcon.Error); return;
            }
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lagwatch-history.csv");
            LatencyPrediction prediction = latencyHistory.Predict(DateTime.Now, target);
            LatencyPrediction gatewayPrediction = latencyHistory.Predict(DateTime.Now, "__local_gateway__");
            string readiness = prediction.Ready
                ? "Ready · expected now ≈ " + prediction.ExpectedMs.ToString("0") + " ms"
                : "Learning · requires 12 matching time-window samples across at least 3 prior days";
            MessageBox.Show(this, "LOCAL TRAINING DATA\n\nStored at:\n" + path +
                "\n\nAll samples: " + stats.TotalSamples +
                "\nCurrent target samples: " + stats.TargetSamples +
                "\nCurrent target days: " + stats.TargetDays +
                "\nHistorical target failure rate now: " + prediction.FailurePercent.ToString("0.0") + "%" +
                "\nHistorical gateway failure rate now: " + gatewayPrediction.FailurePercent.ToString("0.0") + "%" +
                "\n\n" + readiness +
                "\n\nLagWatch currently uses a robust time-of-day median predictor, not a neural network.",
                "Prediction dataset", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async Task RecordGatewayEvidenceAsync(DateTime timestamp)
        {
            if (lastGatewayEvidence.Date == timestamp.Date && lastGatewayEvidence.Hour == timestamp.Hour &&
                lastGatewayEvidence.Minute == timestamp.Minute) return;
            lastGatewayEvidence = timestamp;
            string gateway = null;
            try
            {
                gateway = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                    .Select(g => g.Address.ToString()).FirstOrDefault();
            }
            catch { }
            bool success = false; long latency = 0;
            if (!String.IsNullOrWhiteSpace(gateway))
            {
                try
                {
                    using (Ping ping = new Ping())
                    {
                        PingReply reply = await ping.SendPingAsync(gateway, 900);
                        success = reply.Status == IPStatus.Success; latency = success ? reply.RoundtripTime : 0;
                    }
                }
                catch { }
            }
            try { latencyHistory.Record(timestamp, "__local_gateway__", success, latency); }
            catch (IOException) { }
        }

        private void ShowTierRequirement(string feature, SubscriptionTier required)
        {
            MessageBox.Show(this, feature + " requires " + required + " in this developer demo.\n\n" +
                "Use the tier button in the toolbar to switch plans.", "Tier feature",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SetStatus(string text, Color color)
        {
            statusBadge.Text = "  " + text + "  ";
            statusBadge.ForeColor = color;
            statusBadge.BackColor = Color.FromArgb(28, color);
        }

        private void AppendLog(string message, Color color)
        {
            if (eventLog == null) return;
            if (eventLog.TextLength > 24000)
            {
                eventLog.Select(0, Math.Min(6000, eventLog.TextLength));
                eventLog.SelectedText = "";
            }

            eventLog.SelectionStart = eventLog.TextLength;
            eventLog.SelectionColor = Theme.TextFaint;
            eventLog.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + "  ");
            eventLog.SelectionColor = color;
            eventLog.AppendText(message + Environment.NewLine);
            eventLog.SelectionStart = eventLog.TextLength;
            eventLog.ScrollToCaret();
        }

        private static Color MetricColor(double value, double warning, double bad)
        {
            if (value >= bad) return Theme.Bad;
            if (value >= warning) return Theme.Warning;
            return Theme.Good;
        }

        private static string Csv(string value)
        {
            if (value == null) return "";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
