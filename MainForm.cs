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
    internal sealed partial class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);



        private readonly Timer pingTimer;
        private readonly Timer gameDetectionTimer;
        private Timer sidebarAnimationTimer;
        private readonly List<PingSample> samples = new List<PingSample>();
        private readonly bool startInBackground;
        private AppSettings appSettings;
        private bool startupRegistered;
        private string startupRegistrationError;
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
        private bool sidebarExpanded = true;
        private int sidebarTargetWidth;
        private bool minimizedTipShown;
        private int degradedSampleStreak;
        private int healthySampleStreak;
        private bool networkAlertActive;
        private DateTime lastNetworkAlert = DateTime.MinValue;

        private ComboBox targetBox;
        private AccentButton monitorButton;
        private AccentButton optimizeButton;
        private TableLayoutPanel appShell;
        private Panel sidebar;
        private Panel sidebarHeader;
        private Panel sidebarFeaturePanel;
        private FlowLayoutPanel sidebarFeatureList;
        private Panel sidebarTierPanel;
        private Label sidebarTitle;
        private Label sidebarTierName;
        private Label sidebarTierSummary;
        private Button sidebarToggle;
#if DEV_BUILD
        private FlowLayoutPanel tierSelectorBar;
        private AccentButton tierFreeButton;
        private AccentButton tierPlusButton;
        private AccentButton tierProButton;
#endif
        private AccentButton optionsButton;
        private AccentButton doctorButton;
        private AccentButton workspaceButton;
        private AccentButton historyButton;
        private AccentButton toolsButton;
        private Doctor.DoctorSession doctorSession;
        private Timer backgroundWatchTimer;
        private bool backgroundWatchEnabled;
        private bool backgroundWatchRunning;
        private string lastWatchProblems = "";
        private Label tierBanner;
        private Label headerTitle;
        private Label headerAccent;
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
        private Label sessionDownload;
        private Label sessionUpload;
        private ComboBox gameProcessBox;
        private CheckBox autoDetectGameCheck;
        private Label gameDetectionStatus;
        private LatencyChart chart;
        private ForecastStrip forecastStrip;
        private LatencyForecaster forecaster;
        private ForecastSnapshot forecast = ForecastSnapshot.Empty;
        private readonly NetworkUsageSampler usageSampler = new NetworkUsageSampler();
        private NetworkUsage usage = new NetworkUsage();
        private Timer forecastTimer;
        private bool forecastRefreshing;
        private RichTextBox eventLog;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem trayStatusItem;
        private ToolStripMenuItem trayMonitoringItem;
        private ToolStripMenuItem trayStartupItem;
        private ConnectionWatch connectionWatch;
        /// <summary>What clicking the notification currently showing does (null: just open LagWatch).</summary>
        private Action pendingBalloonClick;
        private bool exitRequested;

        private const int SidebarExpandedWidth = 224;
        private const int SidebarCollapsedWidth = 70;

        public MainForm() : this(false, new AppSettings(), true, null)
        {
        }

        public MainForm(bool startInBackground, AppSettings settings, bool startupRegistered,
            string startupRegistrationError)
        {
            this.startInBackground = startInBackground;
            appSettings = settings == null ? new AppSettings() : settings.Clone();
            this.startupRegistered = startupRegistered;
            this.startupRegistrationError = startupRegistrationError;
            Theme.BeginDpiLayout(this);
            Text = "LagWatch — Game Network Monitor";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(960, 640);
            Size = new Size(1180, 790);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.UiFont(9f);
            KeyPreview = true;

            BuildInterface();
            Theme.EndDpiLayout(this);
            SizeToolTabs();
            latencyHistory = new LatencyHistoryStore(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "lagwatch-history.csv"));
            forecaster = new LatencyForecaster(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "lagwatch-history.csv"));
            forecastTimer = new Timer();
            forecastTimer.Interval = 60 * 1000;
            forecastTimer.Tick += async delegate { await RefreshForecastAsync(); };
            forecastTimer.Start();
            ApplySubscriptionTier();
            RefreshGameProcesses(false);

            pingTimer = new Timer();
            pingTimer.Interval = 1000;
            pingTimer.Tick += async delegate { await PerformPingAsync(); };

            gameDetectionTimer = new Timer();
            gameDetectionTimer.Interval = 4000;
            gameDetectionTimer.Tick += delegate { AutoDetectGame(); };

            InitializeTrayIcon();

            // Always on while LagWatch runs (also from the tray): learns the connection, warns before/during/after.
            connectionWatch = new ConnectionWatch(new HealthHistory(HealthHistory.DefaultPath));
            connectionWatch.CanCheckProvider = () => SubscriptionPolicy.Allows(subscriptionTier, Feature.HeadsUp);
            connectionWatch.HeadsUpRaised += OnHeadsUp;
            connectionWatch.OutlookChanged += ShowOutlook;

            Shown += async delegate
            {
                connectionWatch.Start();
                AppendLog(appSettings.AutoPollOnLaunch
                    ? "Ready. LagWatch will monitor the active local gateway automatically."
                    : "Ready. Automatic gateway polling is off; press Start when you want to monitor.", Theme.TextMuted);
                if (!appSettings.LaunchAtStartup)
                    AppendLog("Windows sign-in startup is off in Options.", Theme.TextMuted);
                else
                    AppendLog(startupRegistered
                        ? "Windows sign-in startup is enabled; startup launches stay in the system tray."
                        : "Windows sign-in startup could not be enabled: " + startupRegistrationError,
                        startupRegistered ? Theme.Good : Theme.Warning);
                if (startInBackground)
                    BeginInvoke(new Action(HideToTray));
                else
                    targetBox.Focus();
                if (appSettings.AutoPollOnLaunch)
                    await StartGatewayMonitoringOnLaunchAsync();
                AutoDetectGame();
                gameDetectionTimer.Start();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!exitRequested && appSettings.CloseToTrayOnClose)
                {
                    e.Cancel = true;
                    HideToTray();
                    return;
                }
                pingTimer.Stop();
                gameDetectionTimer.Stop();
                if (forecastTimer != null) forecastTimer.Stop();
                if (sidebarAnimationTimer != null) sidebarAnimationTimer.Stop();
                if (backgroundWatchTimer != null) backgroundWatchTimer.Stop();
                if (connectionWatch != null) connectionWatch.Dispose();
                if (doctorSession != null) doctorSession.Dispose();
            };
            FormClosed += delegate
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
                if (trayMenu != null) trayMenu.Dispose();
            };
            KeyDown += MainForm_KeyDown;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyWindowChrome();
        }

        private void ApplyWindowChrome()
        {
            try
            {
                int enabled = Theme.IsDark ? 1 : 0;
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
            appShell = new TableLayoutPanel();
            appShell.Dock = DockStyle.Fill;
            appShell.ColumnCount = 2;
            appShell.RowCount = 1;
            appShell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SidebarExpandedWidth));
            appShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(appShell);

            sidebar = CreateSidebar();
            appShell.Controls.Add(sidebar, 0, 0);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(24, 20, 24, 22);
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            appShell.Controls.Add(root, 1, 0);

            // The header (title, live status, plan) stays on every page; the sidebar switches what's below it.
            root.Controls.Add(CreateHeader(), 0, 0);
            pageHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Theme.Background };
            root.Controls.Add(pageHost, 0, 1);

            TableLayoutPanel monitor = new TableLayoutPanel();
            monitor.Dock = DockStyle.Fill;
            monitor.Margin = new Padding(0);
            monitor.ColumnCount = 1;
            monitor.RowCount = 4;
            monitor.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            monitor.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            monitor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            monitor.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            monitor.Controls.Add(CreateToolbar(), 0, 0);
            monitor.Controls.Add(CreateMetricRow(), 0, 1);
            monitor.Controls.Add(CreateMainArea(), 0, 2);
            monitor.Controls.Add(CreateLogArea(), 0, 3);
            monitorPage = monitor;
            pageHost.Controls.Add(monitorPage);
            toolsPage = CreateToolsPage();
            toolsPage.Visible = false;
            pageHost.Controls.Add(toolsPage);
        }

        private Panel CreateSidebar()
        {
            Panel rail = new Panel();
            rail.Dock = DockStyle.Fill;
            rail.BackColor = Theme.Card;
            rail.Padding = new Padding(10, 14, 10, 14);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            rail.Controls.Add(layout);

            sidebarHeader = new Panel();
            sidebarHeader.Dock = DockStyle.Fill;
            sidebarTitle = new Label();
            sidebarTitle.Text = "MENU";
            sidebarTitle.AutoSize = true;
            sidebarTitle.Font = Theme.UiFont(9f, true);
            sidebarTitle.ForeColor = Theme.Text;
            sidebarTitle.Location = new Point(4, 10);
            sidebarHeader.Controls.Add(sidebarTitle);

            sidebarToggle = new Button();
            sidebarToggle.Text = "‹";
            sidebarToggle.FlatStyle = FlatStyle.Flat;
            sidebarToggle.FlatAppearance.BorderSize = 1;
            sidebarToggle.FlatAppearance.BorderColor = Theme.Border;
            sidebarToggle.BackColor = Theme.CardAlt;
            sidebarToggle.ForeColor = Theme.Text;
            sidebarToggle.Font = new Font("Segoe UI Symbol", 15f);
            sidebarToggle.Size = new Size(34, 34);
            sidebarToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            sidebarToggle.Location = new Point(170, 0);
            sidebarToggle.Cursor = Cursors.Hand;
            sidebarToggle.Click += delegate { ToggleSidebar(); };
            sidebarHeader.Controls.Add(sidebarToggle);
            sidebarHeader.Resize += delegate
            {
                sidebarToggle.Location = new Point(Math.Max(0, sidebarHeader.ClientSize.Width - sidebarToggle.Width), 0);
            };
            layout.Controls.Add(sidebarHeader, 0, 0);

            sidebarFeaturePanel = new CardPanel();
            sidebarFeaturePanel.Dock = DockStyle.Fill;
            sidebarFeaturePanel.Margin = new Padding(0, 4, 0, 12);
            sidebarFeaturePanel.Padding = new Padding(12, 12, 12, 12);

            sidebarFeatureList = new FlowLayoutPanel();
            sidebarFeatureList.Dock = DockStyle.Fill;
            sidebarFeatureList.FlowDirection = FlowDirection.TopDown;
            sidebarFeatureList.WrapContents = false;
            sidebarFeatureList.AutoScroll = true;

            // One short list of pages. Free sees MONITOR, TROUBLESHOOT and SETTINGS; Plus and Pro reveal more
            // (see ApplySubscriptionTier). Everything opens in this window; only Settings is a popup.
            monitorNavButton = CreateSidebarButton("MONITOR");
            monitorNavButton.Click += delegate { ShowPage(AppPage.Monitor); };
            sidebarFeatureList.Controls.Add(monitorNavButton);

            doctorButton = CreateSidebarButton("TROUBLESHOOT");
            doctorButton.Click += delegate { ShowPage(AppPage.CheckFix); };
            sidebarFeatureList.Controls.Add(doctorButton);

            workspaceButton = CreateSidebarButton("WORKSPACE");
            workspaceButton.Click += delegate { ShowPage(AppPage.Workspace); };
            sidebarFeatureList.Controls.Add(workspaceButton);

            // Predictions are shown live on the timeline (Plus), so the only history action left is the export.
            historyButton = CreateSidebarButton("EXPORT CSV");
            historyButton.Click += delegate { ExportCsv(); };
            sidebarFeatureList.Controls.Add(historyButton);

            toolsButton = CreateSidebarButton("TOOLS");
            toolsButton.Click += delegate { ShowPage(AppPage.Tools); };
            sidebarFeatureList.Controls.Add(toolsButton);

            optionsButton = CreateSidebarButton("SETTINGS");
            optionsButton.Margin = new Padding(0, 12, 0, 5);
            sidebarFeatureList.Controls.Add(optionsButton);
            optionsButton.Click += delegate { ShowOptions(); };
            sidebarFeaturePanel.Controls.Add(sidebarFeatureList);
            layout.Controls.Add(sidebarFeaturePanel, 0, 1);

            sidebarTierPanel = new CardPanel();
            sidebarTierPanel.Dock = DockStyle.Fill;
            sidebarTierPanel.Margin = new Padding(0);
            sidebarTierPanel.Padding = new Padding(12, 10, 12, 10);

            Label tierCaption = new Label();
            tierCaption.Text = "CURRENT TIER";
            tierCaption.AutoSize = true;
            tierCaption.Font = Theme.UiFont(8f, true);
            tierCaption.ForeColor = Theme.TextFaint;
            tierCaption.Location = new Point(12, 10);
            sidebarTierPanel.Controls.Add(tierCaption);

            sidebarTierName = new Label();
            sidebarTierName.Text = "FREE";
            sidebarTierName.AutoSize = true;
            sidebarTierName.Font = Theme.UiFont(11f, true);
            sidebarTierName.ForeColor = Theme.Accent;
            sidebarTierName.Location = new Point(12, 31);
            sidebarTierPanel.Controls.Add(sidebarTierName);

            sidebarTierSummary = new Label();
            sidebarTierSummary.Text = "Core monitoring";
            sidebarTierSummary.AutoEllipsis = true;
            sidebarTierSummary.AutoSize = false;
            sidebarTierSummary.Size = new Size(176, 30);
            sidebarTierSummary.Font = Theme.UiFont(8f);
            sidebarTierSummary.ForeColor = Theme.TextMuted;
            sidebarTierSummary.Location = new Point(12, 55);
            sidebarTierSummary.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            sidebarTierPanel.Controls.Add(sidebarTierSummary);
            sidebarTierSummary.Visible = false;

            sidebarTierPanel.Resize += delegate
            {
                sidebarTierSummary.Width = Math.Max(40, sidebarTierPanel.ClientSize.Width - 24);
            };
            layout.Controls.Add(sidebarTierPanel, 0, 2);

            sidebarAnimationTimer = new Timer();
            sidebarAnimationTimer.Interval = 15;
            sidebarAnimationTimer.Tick += delegate { AnimateSidebar(); };
            return rail;
        }

#if DEV_BUILD
        private AccentButton CreateTierButton(string text, SubscriptionTier tier)
        {
            AccentButton button = new AccentButton();
            button.Text = text;
            button.Size = new Size(60, 28);
            button.Font = Theme.UiFont(7.5f, true);
            button.Margin = new Padding(0, 0, 4, 0);
            button.Click += delegate
            {
                subscriptionTier = tier;
                ApplySubscriptionTier();
            };
            return button;
        }
#endif

        private static Label CreateSidebarHeading(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Size = new Size(168, 22);
            label.Font = Theme.UiFont(8f, true);
            label.ForeColor = Theme.TextFaint;
            label.Margin = new Padding(0, 0, 0, 5);
            return label;
        }

        private static AccentButton CreateSidebarButton(string text)
        {
            AccentButton button = new AccentButton();
            button.Text = text;
            button.Size = new Size(168, 32);
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(9, 0, 0, 0);
            button.Margin = new Padding(0, 0, 0, 5);
            return button;
        }

        private void ToggleSidebar()
        {
            sidebarExpanded = !sidebarExpanded;
            sidebarTargetWidth = Theme.Px(this, sidebarExpanded ? SidebarExpandedWidth : SidebarCollapsedWidth);
            sidebarToggle.Text = sidebarExpanded ? "‹" : "›";
            sidebarFeaturePanel.Visible = false;
            sidebarTierPanel.Visible = false;
            sidebarTitle.Visible = false;
            sidebarAnimationTimer.Start();
        }

        private void AnimateSidebar()
        {
            int current = (int)appShell.ColumnStyles[0].Width;
            int difference = sidebarTargetWidth - current;
            if (Math.Abs(difference) <= Theme.Px(this, 18))
            {
                SetSidebarWidth(sidebarTargetWidth);
                sidebarAnimationTimer.Stop();
                if (sidebarExpanded)
                {
                    sidebarFeaturePanel.Visible = true;
                    sidebarTierPanel.Visible = true;
                    sidebarTitle.Visible = true;
                }
                return;
            }
            SetSidebarWidth(current + Math.Sign(difference) * Theme.Px(this, 18));
        }

        private void SetSidebarWidth(int width)
        {
            appShell.ColumnStyles[0].Width = width;
            sidebar.Width = width;
            sidebarHeader.Width = Math.Max(34, width - sidebar.Padding.Horizontal - 6);
            sidebarToggle.Location = new Point(Math.Max(0, sidebarHeader.ClientSize.Width - sidebarToggle.Width), 0);
            sidebarToggle.Text = width <= Theme.Px(this, SidebarCollapsedWidth) ? "›" : "‹";
            appShell.PerformLayout();
            sidebar.PerformLayout();
            sidebarHeader.PerformLayout();
        }

        private Control CreateHeader()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;

            Label title = new Label();
            title.Text = "LAGWATCH";
            title.AutoSize = true;
            title.Font = Theme.UiFont(19f, true);
            title.ForeColor = Theme.Text;
            title.Location = new Point(0, 0);
            panel.Controls.Add(title);

            Label accent = new Label();
            accent.Text = "//";
            accent.AutoSize = true;
            accent.Font = title.Font;
            accent.ForeColor = Theme.Accent;
            headerTitle = title;
            headerAccent = accent;
            panel.Controls.Add(accent);

            Label subtitle = new Label();
            subtitle.Text = "Live latency intelligence for competitive play";
            subtitle.AutoSize = true;
            subtitle.Font = Theme.UiFont(9.5f);
            subtitle.ForeColor = Theme.TextMuted;
            subtitle.Location = new Point(2, 37);
            panel.Controls.Add(subtitle);

            statusBadge = new Label();
            statusBadge.Text = "  IDLE  ";
            statusBadge.AutoSize = true;
            statusBadge.Font = Theme.UiFont(9f, true);
            statusBadge.ForeColor = Theme.TextMuted;
            statusBadge.BackColor = Theme.CardAlt;
            statusBadge.Padding = new Padding(7, 5, 7, 5);
            statusBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            panel.Controls.Add(statusBadge);

            tierBanner = new Label();
            tierBanner.Text = "FREE · CORE NETWORK MONITOR";
            tierBanner.AutoSize = true;
            tierBanner.Font = Theme.UiFont(8.5f, true);
            tierBanner.ForeColor = Theme.TextMuted;
            tierBanner.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            panel.Controls.Add(tierBanner);

#if DEV_BUILD
            tierSelectorBar = new FlowLayoutPanel();
            tierSelectorBar.Name = "DeveloperTierSelector";
            tierSelectorBar.FlowDirection = FlowDirection.LeftToRight;
            tierSelectorBar.WrapContents = false;
            tierSelectorBar.Size = new Size(200, 30);
            tierSelectorBar.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            tierFreeButton = CreateTierButton("FREE", SubscriptionTier.Free);
            tierPlusButton = CreateTierButton("PLUS", SubscriptionTier.Plus);
            tierProButton = CreateTierButton("PRO", SubscriptionTier.Pro);
            tierSelectorBar.Controls.Add(tierFreeButton);
            tierSelectorBar.Controls.Add(tierPlusButton);
            tierSelectorBar.Controls.Add(tierProButton);
            tierSelectorBar.Enabled = true;
            tierSelectorBar.BringToFront();
            panel.Controls.Add(tierSelectorBar);
            ToolTip tierTip = new ToolTip();
            tierTip.SetToolTip(tierSelectorBar, "DEV BUILD: click FREE, PLUS, or PRO to switch tiers.");
            tierTip.SetToolTip(tierFreeButton, "Free: monitor + Troubleshoot with safe fixes.");
            tierTip.SetToolTip(tierPlusButton, "Plus: network repairs, Fix all, custom scans, predictions, export.");
            tierTip.SetToolTip(tierProButton, "Pro: full network metrics, route/outage tools, background watch, workspace optimization.");
#endif

            panel.Resize += delegate
            {
                PositionHeaderControls(panel);
            };
            return panel;
        }

        private int tierSelectorBarBottom()
        {
#if DEV_BUILD
            return tierSelectorBar.Bottom;
#else
            return 0;
#endif
        }

        private void PositionHeaderControls(Control panel)
        {
#if DEV_BUILD
            tierSelectorBar.Location = new Point(Math.Max(0, panel.ClientSize.Width - tierSelectorBar.Width), Theme.Px(panel, 2));
            statusBadge.Location = new Point(Math.Max(0, tierSelectorBar.Left - statusBadge.Width - Theme.Px(panel, 10)), Theme.Px(panel, 3));
#else
            statusBadge.Location = new Point(Math.Max(0, panel.ClientSize.Width - statusBadge.Width), Theme.Px(panel, 5));
#endif
            int bannerTop = Math.Max(statusBadge.Bottom, tierSelectorBarBottom()) + Theme.Px(panel, 4);
            tierBanner.Location = new Point(Math.Max(0, panel.ClientSize.Width - tierBanner.Width), bannerTop);
            if (headerAccent != null && headerTitle != null)
                headerAccent.Location = new Point(headerTitle.Right + Theme.Px(panel, 4), headerTitle.Top);
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
            label.Font = Theme.UiFont(8.5f, true);
            label.ForeColor = Theme.TextMuted;
            label.Margin = new Padding(0, 0, 4, 0);
            bar.Controls.Add(label);

            targetBox = new ComboBox();
            targetBox.Width = 206;
            targetBox.DropDownWidth = 310;
            targetBox.Height = 36;
            targetBox.DropDownStyle = ComboBoxStyle.DropDown;
            targetBox.FlatStyle = FlatStyle.Flat;
            targetBox.Font = Theme.UiFont(10f);
            targetBox.BackColor = Theme.CardAlt;
            targetBox.ForeColor = Theme.Text;
            targetBox.Items.AddRange(new object[]
            {
                new PingTargetOption { DisplayName = "Cloudflare DNS · 1.1.1.1 (public reference)", Target = "1.1.1.1" },
                new PingTargetOption { DisplayName = "Google DNS · 8.8.8.8 (public reference)", Target = "8.8.8.8" },
                new PingTargetOption { DisplayName = "Quad9 DNS · 9.9.9.9 (public reference)", Target = "9.9.9.9" },
                new PingTargetOption { DisplayName = "Cloudflare website · cloudflare.com (DNS + Internet)", Target = "cloudflare.com" }
            });
            RefreshGatewayTargetOption();
            targetBox.SelectedItem = targetBox.Items.OfType<PingTargetOption>()
                .FirstOrDefault(option => String.Equals(option.Target, "1.1.1.1", StringComparison.OrdinalIgnoreCase));
            targetBox.DropDown += delegate { RefreshGatewayTargetOption(); };
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
            optimizeButton.Text = "OPTIMIZE";
            optimizeButton.Width = 128;
            optimizeButton.Margin = new Padding(0, 0, 8, 0);
            optimizeButton.Click += async delegate { await ShowOptimizerAsync(); };
            bar.Controls.Add(optimizeButton);

            ToolTip tips = new ToolTip();
            tips.SetToolTip(targetBox, "Local gateway tests your PC-to-router link. Public references test wider Internet reachability. Cloudflare website also tests DNS lookup.");
            tips.SetToolTip(optimizeButton, "Choose an optimization profile and review its checklist before applying it.");
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
            title.Font = Theme.UiFont(8f, true);
            title.ForeColor = Theme.TextMuted;
            title.Location = new Point(18, 11);
            card.Controls.Add(title);

            value = new Label();
            value.Text = "—";
            value.AutoSize = true;
            value.Font = Theme.UiFont(20f, true);
            value.ForeColor = Theme.Text;
            value.Location = new Point(16, 31);
            card.Controls.Add(value);

            detail = new Label();
            detail.AutoSize = true;
            detail.Font = Theme.UiFont(8f);
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
            chartTitle.Font = Theme.UiFont(9f, true);
            chartTitle.ForeColor = Theme.Text;
            chartTitle.Location = new Point(1, 5);
            chartHeader.Controls.Add(chartTitle);

            chart = new LatencyChart();
            chart.Dock = DockStyle.Fill;
            chartCard.Controls.Add(chart);
            chartCard.Controls.Add(chartHeader);

            // Plus: the forecast for the next hours, right under the live timeline.
            forecastStrip = new ForecastStrip();
            forecastStrip.Dock = DockStyle.Bottom;
            forecastStrip.Height = 88;
            forecastStrip.Visible = false;
            chartCard.Controls.Add(forecastStrip);
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
            title.Font = Theme.UiFont(9f, true);
            title.ForeColor = Theme.Text;
            title.Location = new Point(18, 15);
            card.Controls.Add(title);

            sessionTarget = CreateSessionLine(card, "Target", "1.1.1.1", 45);
            sessionStartedLabel = CreateSessionLine(card, "Started", "Not running", 68);
            sessionAdapter = CreateSessionLine(card, "Adapter", NetworkDiagnostics.GetActiveAdapterSummary(), 91);
            sessionSamples = CreateSessionLine(card, "Samples", "0", 114);
            sessionDownload = CreateSessionLine(card, "Download", "0.0 Mbps", 137);
            sessionUpload = CreateSessionLine(card, "Upload", "0.0 Mbps", 160);

            Panel divider = new Panel();
            divider.Height = 1;
            divider.BackColor = Theme.Border;
            divider.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            divider.Location = new Point(18, 235);
            divider.Width = card.ClientSize.Width - 36;
            card.Controls.Add(divider);

            Label gameLabel = new Label();
            gameLabel.Text = "GAME PROCESS FOR PRIORITY BOOST";
            gameLabel.AutoSize = true;
            gameLabel.Font = Theme.UiFont(8f, true);
            gameLabel.ForeColor = Theme.TextMuted;
            gameLabel.Location = new Point(18, 203);
            card.Controls.Add(gameLabel);

            gameProcessBox = new ComboBox();
            gameProcessBox.DropDownStyle = ComboBoxStyle.DropDown;
            gameProcessBox.FlatStyle = FlatStyle.Flat;
            gameProcessBox.BackColor = Theme.CardAlt;
            gameProcessBox.ForeColor = Theme.Text;
            gameProcessBox.Font = Theme.UiFont(8.5f);
            gameProcessBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            gameProcessBox.AutoCompleteSource = AutoCompleteSource.ListItems;
            gameProcessBox.Location = new Point(18, 225);
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
            // An editable combo box highlights its text whenever the selection changes in code; clear that so it
            // doesn't look like the field is being edited.
            gameProcessBox.SelectedIndexChanged += delegate
            {
                if (gameProcessBox.IsHandleCreated)
                    gameProcessBox.BeginInvoke(new Action(delegate { gameProcessBox.SelectionLength = 0; }));
            };
            card.Controls.Add(gameProcessBox);

            AccentButton refresh = new AccentButton();
            refresh.Text = "↻";
            refresh.Font = new Font("Segoe UI Symbol", 10f);
            refresh.Size = new Size(34, 28);
            refresh.Location = new Point(card.ClientSize.Width - 52, 224);
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
            autoDetectGameCheck.Font = Theme.UiFont(8.5f, true);
            autoDetectGameCheck.ForeColor = Theme.TextMuted;
            autoDetectGameCheck.Location = new Point(18, 259);
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
            gameDetectionStatus.Font = Theme.UiFont(8f);
            gameDetectionStatus.ForeColor = Theme.TextFaint;
            gameDetectionStatus.Location = new Point(18, 283);
            gameDetectionStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            card.Controls.Add(gameDetectionStatus);

            Label hint = new Label();
            hint.Text = "Detection stays local. Priority changes only when you press Safe Optimize.";
            hint.AutoSize = false;
            hint.Height = 34;
            hint.Font = Theme.UiFont(8f);
            hint.ForeColor = Theme.TextFaint;
            hint.Location = new Point(18, 306);
            hint.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            card.Controls.Add(hint);

            card.Resize += delegate
            {
                int inset = Theme.Px(card, 36);
                divider.Width = Math.Max(30, card.ClientSize.Width - inset);
                gameProcessBox.Width = Math.Max(80, refresh.Left - gameProcessBox.Left - Theme.Px(card, 6));
                gameDetectionStatus.Width = Math.Max(80, card.ClientSize.Width - inset);
                hint.Width = Math.Max(80, card.ClientSize.Width - inset);
            };
            return card;
        }

        private static Label CreateSessionLine(Control parent, string name, string value, int top)
        {
            Label caption = new Label();
            caption.Text = name;
            caption.AutoSize = true;
            caption.Font = Theme.UiFont(8.5f);
            caption.ForeColor = Theme.TextFaint;
            caption.Location = new Point(18, top);
            parent.Controls.Add(caption);

            Label data = new Label();
            data.Text = value;
            data.AutoEllipsis = true;
            data.TextAlign = ContentAlignment.MiddleRight;
            data.Font = Theme.UiFont(8.5f);
            data.ForeColor = Theme.TextMuted;
            data.Location = new Point(96, top - 1);
            data.Height = 20;
            data.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            parent.Controls.Add(data);
            parent.Resize += delegate { data.Width = Math.Max(60, parent.ClientSize.Width - data.Left - Theme.Px(parent, 18)); };
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
            title.Font = Theme.UiFont(9f, true);
            title.ForeColor = Theme.Text;
            card.Controls.Add(title);

            eventLog = new RichTextBox();
            eventLog.Dock = DockStyle.Fill;
            eventLog.BorderStyle = BorderStyle.None;
            eventLog.BackColor = Theme.Card;
            eventLog.ForeColor = Theme.TextMuted;
            eventLog.Font = Theme.MonoFont(8.5f);
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

            string target = GetSelectedPingTarget();
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
            degradedSampleStreak = 0;
            healthySampleStreak = 0;
            networkAlertActive = false;
            lastNetworkAlert = DateTime.MinValue;
            sessionStarted = DateTime.Now;
            monitoring = true;
            targetBox.Enabled = false;
            monitorButton.Text = "STOP  [F5]";
            monitorButton.Primary = false;
            sessionTarget.Text = target;
            sessionStartedLabel.Text = sessionStarted.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            sessionSamples.Text = "0";
            sessionDownload.Text = "0.0 Mbps";
            sessionUpload.Text = "0.0 Mbps";
            usage = new NetworkUsage();
            sessionAdapter.Text = NetworkDiagnostics.GetActiveAdapterSummary();
            SetStatus("CONNECTING", Theme.Warning);
            AppendLog("Monitoring started → " + target, Theme.Accent);
            pingTimer.Start();
            await RefreshForecastAsync();
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
                    PingReply reply = await ping.SendPingAsync(GetSelectedPingTarget(), 1400);
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
            try { latencyHistory.Record(sample.Timestamp, GetSelectedPingTarget(), sample.Success, sample.LatencyMs); }
            catch (IOException) { }
            await RecordGatewayEvidenceAsync(sample.Timestamp);
            usage = usageSampler.Sample();
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
                currentDetail.Text = SubscriptionPolicy.CanUsePredictions(subscriptionTier)
                    ? CompareWithUsual(snapshot.CurrentMs)
                    : "latest reply";
            }
            sessionSamples.Text = snapshot.TotalSamples.ToString(CultureInfo.InvariantCulture);
            sessionDownload.Text = usage.DownloadMbps.ToString("0.0", CultureInfo.CurrentCulture) + " Mbps";
            sessionUpload.Text = usage.UploadMbps.ToString("0.0", CultureInfo.CurrentCulture) + " Mbps";

            EvaluateTrayAlert(snapshot);

            Color qualityColor = Theme.TextMuted;
            switch (snapshot.Quality)
            {
                case "EXCELLENT":
                case "GOOD": qualityColor = Theme.Good; break;
                case "FAIR": qualityColor = Theme.Warning; break;
                case "POOR":
                case "CRITICAL": qualityColor = Theme.Bad; break;
            }
            // The badge is on every page, so it carries the live reading too.
            SetStatus((snapshot.CurrentMs >= 0 ? snapshot.CurrentMs + " ms · " : "") + snapshot.Quality, qualityColor);
        }

        private void EvaluateTrayAlert(NetworkSnapshot snapshot)
        {
            if (trayIcon == null || snapshot.TotalSamples == 0) return;

            bool degraded = snapshot.CurrentMs < 0 || snapshot.Quality == "POOR" || snapshot.Quality == "CRITICAL";
            if (degraded)
            {
                degradedSampleStreak++;
                healthySampleStreak = 0;
            }
            else
            {
                healthySampleStreak++;
                degradedSampleStreak = 0;
            }

            // The connection watch already told the user about this (with its cause); don't stack a second notice.
            bool connectionNoticeShowing = connectionWatch != null && connectionWatch.IncidentActive && appSettings.ConnectionAlerts;
            if (!networkAlertActive && !connectionNoticeShowing && degradedSampleStreak >= 3 &&
                (DateTime.Now - lastNetworkAlert).TotalSeconds >= 30)
            {
                networkAlertActive = true;
                lastNetworkAlert = DateTime.Now;
                string detail = snapshot.CurrentMs < 0
                    ? "The target is not responding."
                    : "Latency " + snapshot.AverageMs.ToString("0") + " ms · jitter " +
                      snapshot.JitterMs.ToString("0") + " ms · loss " + snapshot.LossPercent.ToString("0.0") + "%.";
                ShowNetworkNotification("Network quality degraded", detail, ToolTipIcon.Warning);
            }
            else if (networkAlertActive && healthySampleStreak >= 5)
            {
                networkAlertActive = false;
                healthySampleStreak = 0;
                ShowNetworkNotification("Network recovered", "The monitored target is responding normally again.", ToolTipIcon.Info);
            }
        }

        private void ShowNetworkNotification(string title, string message, ToolTipIcon icon)
        {
            if (trayIcon == null) return;
            pendingBalloonClick = null;
            trayIcon.BalloonTipTitle = "LagWatch · " + title;
            trayIcon.BalloonTipText = message;
            trayIcon.BalloonTipIcon = icon;
            trayIcon.ShowBalloonTip(4500);
            AppendLog(title + " · " + message, icon == ToolTipIcon.Warning ? Theme.Warning : Theme.Good);
        }

        // MARK: Connection heads-ups

        /// <summary>
        /// Free: during/after notices. Plus: also the heads-up before a usually-spotty time and provider outage alerts.
        /// Each one says what to do; clicking opens the page that helps (Check &amp; Fix when the problem is on this side).
        /// </summary>
        private void OnHeadsUp(HeadsUp headsUp)
        {
            bool ahead = headsUp.Kind == HeadsUpKind.Before || headsUp.Kind == HeadsUpKind.ProviderOutage;
            if (ahead && !SubscriptionPolicy.Allows(subscriptionTier, Feature.HeadsUp)) return;
            Color logColor = headsUp.Kind == HeadsUpKind.After ? Theme.Good : headsUp.Serious ? Theme.Bad : Theme.Warning;
            AppendLog(headsUp.Title + " · " + headsUp.Body, logColor);
            if (!appSettings.ConnectionAlerts || trayIcon == null) return;
            // The live monitor already announced this one.
            if ((headsUp.Kind == HeadsUpKind.During || headsUp.Kind == HeadsUpKind.After) && networkAlertActive) return;

            Action click;
            if (headsUp.OffersFixes)
                click = delegate { RestoreFromTray(); ShowPage(AppPage.CheckFix); };
            else if (headsUp.Kind == HeadsUpKind.ProviderOutage && SubscriptionPolicy.Allows(subscriptionTier, Feature.OutageCenter))
                click = delegate { RestoreFromTray(); currentTool = ToolPage.OutageMap; ShowPage(AppPage.Tools); };
            else
                click = delegate { RestoreFromTray(); ShowPage(AppPage.Monitor); };

            pendingBalloonClick = click;
            trayIcon.BalloonTipTitle = "LagWatch · " + headsUp.Title;
            trayIcon.BalloonTipText = headsUp.Body;
            trayIcon.BalloonTipIcon = headsUp.Kind == HeadsUpKind.After ? ToolTipIcon.Info : ToolTipIcon.Warning;
            trayIcon.ShowBalloonTip(headsUp.Serious ? 10000 : 7000);
        }

        /// <summary>The coming spotty times, on the forecast strip (Plus).</summary>
        private void ShowOutlook()
        {
            if (forecastStrip == null || connectionWatch == null) return;
            forecastStrip.SetRisk(SubscriptionPolicy.Allows(subscriptionTier, Feature.HeadsUp) ? connectionWatch.Outlook : RiskOutlook.Empty);
        }

        private async Task ShowOptimizerAsync()
        {
            using (OptimizationForm optimizer = new OptimizationForm())
            {
                if (optimizer.ShowDialog(this) != DialogResult.OK || optimizer.SelectedPlan == null)
                    return;
                await ApplyOptimizationPlanAsync(optimizer.SelectedPlan);
            }
        }

        private async Task ApplyOptimizationPlanAsync(OptimizationPlan plan)
        {
            optimizeButton.Enabled = false;
            List<string> results = new List<string>();
            AppendLog("Optimization started after checklist confirmation.", Theme.AccentBlue);
            try
            {
                if (autoDetectGameCheck.Checked)
                    AutoDetectGame();

                if (plan.BoostGamePriority)
                {
                    GameProcessOption selected = gameProcessBox.SelectedItem as GameProcessOption;
                    if (selected != null && selected.ProcessId > 0)
                    {
                        try
                        {
                            using (Process process = Process.GetProcessById(selected.ProcessId))
                            {
                                process.PriorityClass = ProcessPriorityClass.AboveNormal;
                                results.Add("✓ " + selected.ProcessName + " priority set to Above normal");
                                AppendLog("Game priority boosted: " + selected.ProcessName + ".", Theme.Good);
                            }
                        }
                        catch (Exception ex) { results.Add("! Game priority: " + ex.Message); }
                    }
                    else
                        results.Add("• No game process selected; priority was unchanged");
                }

                if (plan.VisualMode != VisualEffectsMode.Unchanged)
                {
                    string name = VisualEffects.Describe(plan.VisualMode);
                    if (VisualEffects.Apply(plan.VisualMode))
                    {
                        results.Add("✓ Windows visual effects set to " + name);
                        AppendLog("Windows visual effects set to " + name + ".", Theme.Good);
                    }
                    else
                    {
                        results.Add("! Windows visual effects: some settings were not accepted (" + name + ")");
                        AppendLog("Windows accepted only some visual-effects settings (" + name + ").", Theme.Warning);
                    }
                }

                if (results.Count == 0)
                    results.Add("• No changes selected");
                MessageBox.Show(this, String.Join(Environment.NewLine, results), "LagWatch optimization",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("Optimization error: " + ex.Message, Theme.Bad);
                MessageBox.Show(this, ex.Message, "Optimization could not finish",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { optimizeButton.Enabled = true; }
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

            string oldTarget = GetSelectedPingTarget();
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
                ShowTierRequirement("Session export", SubscriptionTier.Plus);
                return;
            }
            if (samples.Count == 0)
            {
                MessageBox.Show(this, "There are no samples to export yet.", "Nothing to export",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string path;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Export session CSV";
                dialog.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
                dialog.DefaultExt = "csv";
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.FileName = "LagWatch-session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv";
                dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                path = dialog.FileName;
            }
            string fileName = Path.GetFileName(path);
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
                AppendLog("Session exported → " + path, Theme.Good);
                MessageBox.Show(this, "Saved to:" + Environment.NewLine + path,
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

        private void ApplySubscriptionTier(bool announce = true)
        {
#if DEV_BUILD
            tierFreeButton.AccentColor = SubscriptionPolicy.IdentityColor(SubscriptionTier.Free);
            tierPlusButton.AccentColor = SubscriptionPolicy.IdentityColor(SubscriptionTier.Plus);
            tierProButton.AccentColor = SubscriptionPolicy.IdentityColor(SubscriptionTier.Pro);
            tierFreeButton.Primary = subscriptionTier == SubscriptionTier.Free;
            tierPlusButton.Primary = subscriptionTier == SubscriptionTier.Plus;
            tierProButton.Primary = subscriptionTier == SubscriptionTier.Pro;
#endif
            sidebarFeatureList.Visible = true;
            sidebarToggle.Enabled = true;
            sidebarTitle.Text = "MENU";
            Color identity = SubscriptionPolicy.IdentityColor(subscriptionTier);
            Color plusColor = SubscriptionPolicy.IdentityColor(SubscriptionTier.Plus);
            Color proColor = SubscriptionPolicy.IdentityColor(SubscriptionTier.Pro);

            // Progressive disclosure: what a plan doesn't include is hidden, not shown locked.
            ShowIfAllowed(historyButton, Feature.ExportHistory, plusColor);
            ShowIfAllowed(toolsButton, Feature.PathCheck, plusColor);
            ShowIfAllowed(workspaceButton, Feature.WorkspaceOptimization, proColor);
            RefreshPagesForTier();

            tierBanner.Text = SubscriptionPolicy.Banner(subscriptionTier);
#if DEV_BUILD
            tierBanner.Text = "DEV BUILD · " + tierBanner.Text;
#endif
            tierBanner.ForeColor = identity;
            if (tierBanner.Parent != null) PositionHeaderControls(tierBanner.Parent);
            sidebarTierName.Text = subscriptionTier.ToString().ToUpperInvariant();
            sidebarTierName.ForeColor = identity;
            sidebarTierSummary.Text = SubscriptionPolicy.ShortSummary(subscriptionTier);
            optionsButton.AccentColor = Theme.Accent;
            optionsButton.Primary = false;

            // Leaving Pro turns the background watch off.
            if (backgroundWatchEnabled && !SubscriptionPolicy.Allows(subscriptionTier, Feature.BackgroundWatch))
                SetBackgroundWatch(false);
            if (forecaster != null) BeginInvokeWhenReady(async delegate { await RefreshForecastAsync(); });
            else if (forecastStrip != null) forecastStrip.Visible = false;

            if (announce)
            {
                AppendLog("Tier: " + subscriptionTier.ToString().ToUpperInvariant() +
                    " · " + SubscriptionPolicy.Summary(subscriptionTier) + ".", Theme.AccentBlue);
                if (subscriptionTier == SubscriptionTier.Pro)
                    AppendLog("Adaptive traffic control is a monitor-only preview; no Windows network limits are applied.", Theme.Warning);
            }
            if (monitoring) UpdateMetrics();
        }

        private void ShowIfAllowed(AccentButton button, Feature feature, Color tierColor)
        {
            button.Visible = SubscriptionPolicy.Allows(subscriptionTier, feature);
            button.AccentColor = tierColor;
            button.Primary = false;
        }

        // MARK: Network Doctor (NetFix)

        private Doctor.DoctorSession DoctorSession
        {
            get
            {
                if (doctorSession == null) doctorSession = new Doctor.DoctorSession();
                return doctorSession;
            }
        }

        private string BuildLagWatchSummary()
        {
            if (samples.Count == 0) return "No live latency session.";
            NetworkSnapshot s = StatisticsCalculator.Calculate(samples);
            return "Target " + GetSelectedPingTarget() + " · " + s.TotalSamples + " samples · avg " + s.AverageMs.ToString("0.0") +
                " ms · jitter " + s.JitterMs.ToString("0.0") + " ms · loss " + s.LossPercent.ToString("0.0") + "% · " + s.Quality;
        }

        private void ToggleBackgroundWatch()
        {
            if (!SubscriptionPolicy.Allows(subscriptionTier, Feature.BackgroundWatch))
            {
                ShowTierRequirement("Background watch", SubscriptionTier.Pro);
                return;
            }
            SetBackgroundWatch(!backgroundWatchEnabled);
            ApplySubscriptionTier();
        }

        /// <summary>
        /// Pro: Network Doctor rechecks the network quietly every 5 minutes and whenever the connection changes, and
        /// shows a notification-area alert when a new problem appears. Network checks only — no fixes run by themselves.
        /// </summary>
        private void SetBackgroundWatch(bool enabled)
        {
            backgroundWatchEnabled = enabled;
            if (backgroundWatchTimer == null)
            {
                backgroundWatchTimer = new Timer();
                backgroundWatchTimer.Interval = 5 * 60 * 1000;
                backgroundWatchTimer.Tick += async delegate { await RunBackgroundWatchAsync(); };
                DoctorSession.Connectivity.Changed += delegate
                {
                    if (backgroundWatchEnabled && IsHandleCreated)
                        BeginInvoke(new Action(async delegate { await Task.Delay(5000); await RunBackgroundWatchAsync(); }));
                };
            }
            if (enabled)
            {
                lastWatchProblems = "";
                backgroundWatchTimer.Start();
                AppendLog("Background watch on: Network Doctor rechecks every 5 minutes and when the connection changes.", Theme.AccentBlue);
                BeginInvoke(new Action(async delegate { await RunBackgroundWatchAsync(); }));
            }
            else
            {
                backgroundWatchTimer.Stop();
                AppendLog("Background watch off.", Theme.TextMuted);
            }
        }

        private async Task RunBackgroundWatchAsync()
        {
            if (!backgroundWatchEnabled || backgroundWatchRunning || DoctorSession.IsRunning) return;
            backgroundWatchRunning = true;
            try
            {
                Netfix.Model.CheckupResult result = await DoctorSession.QuietNetworkScan();
                var problems = result.Issues.Where(i => i.Severity == Netfix.Model.Severity.Problem).ToList();
                string key = String.Join("|", problems.Select(p => p.Id).OrderBy(id => id));
                if (key == lastWatchProblems) return;
                lastWatchProblems = key;
                if (problems.Count == 0)
                {
                    AppendLog("Background watch: network looks healthy again.", Theme.Good);
                    return;
                }
                string title = problems.Count == 1 ? problems[0].Title : problems.Count + " network problems found";
                AppendLog("Background watch: " + String.Join("; ", problems.Select(p => p.Title)) + ".", Theme.Bad);
                if (trayIcon != null)
                {
                    pendingBalloonClick = delegate { RestoreFromTray(); ShowPage(AppPage.CheckFix); };
                    trayIcon.BalloonTipTitle = "LagWatch · Network Doctor";
                trayIcon.BalloonTipText = title + ". Open Troubleshoot in LagWatch to fix it.";
                    trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
                    trayIcon.ShowBalloonTip(6000);
                }
            }
            catch (Exception ex)
            {
                AppendLog("Background watch could not finish a check: " + ex.Message, Theme.Warning);
            }
            finally { backgroundWatchRunning = false; }
        }

        /// <summary>Runs on the UI thread after the current event (or right away before the window exists).</summary>
        private void BeginInvokeWhenReady(Action action)
        {
            if (IsHandleCreated) BeginInvoke(action);
            else action();
        }

        // MARK: Latency forecast (Plus)

        /// <summary>Recomputes "usual now" and the next 6 hours in the background (cached file read), then shows them.</summary>
        private async Task RefreshForecastAsync()
        {
            bool allowed = SubscriptionPolicy.CanUsePredictions(subscriptionTier);
            if (forecastStrip != null) forecastStrip.Visible = allowed;
            if (!allowed)
            {
                forecast = ForecastSnapshot.Empty;
                if (chart != null) chart.SetUsual(null);
                return;
            }
            if (forecaster == null || forecastRefreshing) return;
            forecastRefreshing = true;
            string target = GetSelectedPingTarget();
            try
            {
                ForecastSnapshot fresh = await Task.Run(() => forecaster.Build(DateTime.Now, target, 6, 30));
                if (IsDisposed || !SubscriptionPolicy.CanUsePredictions(subscriptionTier)) return;
                forecast = fresh;
                forecastStrip.SetSnapshot(fresh);
                ShowOutlook();
                chart.SetUsual(fresh.Now);
                if (monitoring) UpdateMetrics();
            }
            catch (Exception ex)
            {
                AppendLog("Latency forecast could not be updated: " + ex.Message, Theme.Warning);
            }
            finally { forecastRefreshing = false; }
        }

        /// <summary>The latency card's caption on Plus: how this reply compares with what's usual at this time of day.</summary>
        private string CompareWithUsual(long currentMs)
        {
            ForecastSlot now = forecast.Now;
            if (now == null || !String.Equals(forecast.Target, GetSelectedPingTarget(), StringComparison.OrdinalIgnoreCase))
                return "latest reply";
            if (!now.Ready) return "learning what's usual · " + Math.Min(now.Days, LatencyForecaster.RequiredDays) + "/" +
                LatencyForecaster.RequiredDays + " days";
            if (now.FailurePercent >= 10) return "often drops at this time (" + now.FailurePercent.ToString("0") + "%)";
            double difference = currentMs - now.MedianMs;
            double tolerance = Math.Max(3, now.MedianMs * 0.25);
            if (Math.Abs(difference) <= tolerance) return "about usual (≈" + now.MedianMs.ToString("0") + " ms)";
            return difference > 0
                ? difference.ToString("0") + " ms above usual (≈" + now.MedianMs.ToString("0") + ")"
                : (-difference).ToString("0") + " ms better than usual";
        }

        internal void SetPreviewTier(SubscriptionTier tier)
        {
            subscriptionTier = tier;
            ApplySubscriptionTier();
        }

        internal void SetPreviewSidebarCollapsed()
        {
            sidebarAnimationTimer.Stop();
            sidebarExpanded = false;
            sidebarFeaturePanel.Visible = false;
            sidebarTierPanel.Visible = false;
            sidebarTitle.Visible = false;
            SetSidebarWidth(Theme.Px(this, SidebarCollapsedWidth));
        }

        private async Task RecordGatewayEvidenceAsync(DateTime timestamp)
        {
            if (lastGatewayEvidence.Date == timestamp.Date && lastGatewayEvidence.Hour == timestamp.Hour &&
                lastGatewayEvidence.Minute == timestamp.Minute) return;
            lastGatewayEvidence = timestamp;
            string gateway = NetworkDiagnostics.GetDefaultGateway();
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
            Doctor.TierPrompt.Show(this, feature, required, subscriptionTier);
        }

        private string GetSelectedPingTarget()
        {
            PingTargetOption option = targetBox.SelectedItem as PingTargetOption;
            return option == null ? targetBox.Text.Trim() : option.Target;
        }

        private void RefreshGatewayTargetOption()
        {
            PingTargetOption existing = targetBox.Items.OfType<PingTargetOption>()
                .FirstOrDefault(option => option.IsLocalGateway);
            string gateway = NetworkDiagnostics.GetDefaultGateway();

            if (existing != null && String.Equals(existing.Target, gateway, StringComparison.OrdinalIgnoreCase))
                return;

            bool selected = existing != null && Object.ReferenceEquals(targetBox.SelectedItem, existing);
            if (existing != null) targetBox.Items.Remove(existing);
            if (String.IsNullOrWhiteSpace(gateway)) return;

            PingTargetOption gatewayOption = new PingTargetOption
            {
                DisplayName = "Local gateway · " + gateway + " (LAN test)",
                Target = gateway,
                IsLocalGateway = true
            };
            targetBox.Items.Insert(0, gatewayOption);
            if (selected) targetBox.SelectedItem = gatewayOption;
        }

        private async Task StartGatewayMonitoringOnLaunchAsync()
        {
            RefreshGatewayTargetOption();
            PingTargetOption gateway = targetBox.Items.OfType<PingTargetOption>()
                .FirstOrDefault(option => option.IsLocalGateway);
            if (gateway == null)
            {
                AppendLog("No active local gateway was found at launch; automatic monitoring is waiting.", Theme.Warning);
                return;
            }

            targetBox.SelectedItem = gateway;
            AppendLog("Local gateway detected → " + gateway.Target + ". Starting automatic ping monitoring.", Theme.AccentBlue);
            await ToggleMonitoringAsync();
        }

        private void ShowOptions()
        {
            // Picking a theme previews it live on the whole app; Cancel (or closing the window) puts the old one back.
            string originalTheme = Theme.CurrentPreset;
            using (OptionsForm options = new OptionsForm(appSettings, ApplyThemePreset))
            {
                if (options.ShowDialog(this) != DialogResult.OK)
                {
                    if (Theme.CurrentPreset != originalTheme) ApplyThemePreset(originalTheme);
                    return;
                }
                AppSettings updated = options.SelectedSettings;
                string previousTheme = Theme.CurrentPreset;
                try
                {
                    SettingsStore.Save(updated);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not save options",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string startupError;
                bool startupChanged = StartupRegistration.SetEnabled(updated.LaunchAtStartup, out startupError);
                startupRegistered = updated.LaunchAtStartup && startupChanged;
                startupRegistrationError = startupError;
                appSettings = updated;
                if (trayStartupItem != null)
                    trayStartupItem.Checked = startupRegistered;

                if (!String.Equals(previousTheme, updated.ThemeName, StringComparison.OrdinalIgnoreCase))
                    ApplyThemePreset(updated.ThemeName);

                if (!startupChanged)
                    MessageBox.Show(this, "The options were saved, but Windows startup could not be updated:\n\n" + startupError,
                        "Startup setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                AppendLog("Options saved · startup " + (updated.LaunchAtStartup ? "on" : "off") +
                    " · launch polling " + (updated.AutoPollOnLaunch ? "on" : "off") +
                    " · " + updated.ThemeName + " theme.", Theme.AccentBlue);
            }
        }

        private void ApplyThemePreset(string themeName)
        {
            if (Theme.NormalizePreset(themeName) == Theme.CurrentPreset) return;
            ThemePalette previous = Theme.Capture();
            Theme.ApplyPreset(themeName);
            RecolorControlTree(this, previous);
            // Open dialogs (the Settings window during a live preview) change with the app.
            foreach (Form owned in OwnedForms)
            {
                RecolorControlTree(owned, previous);
                owned.Invalidate(true);
                owned.Refresh();
            }
            if (trayMenu != null)
            {
                trayMenu.BackColor = Theme.Card;
                trayMenu.ForeColor = Theme.Text;
            }
            statusBadge.BackColor = Theme.CardAlt;
            ApplySubscriptionTier(false);
            if (monitoring) UpdateMetrics();
            ApplyWindowChrome();
            Invalidate(true);
            Refresh();
        }

        private static void RecolorControlTree(Control control, ThemePalette previous)
        {
            AccentButton accentButton = control as AccentButton;
            if (accentButton != null)
            {
                if (accentButton.AccentColor.ToArgb() == previous.Accent.ToArgb())
                    accentButton.AccentColor = Theme.Accent;
                accentButton.Primary = accentButton.Primary;
            }
            else
            {
                control.BackColor = MapThemeColor(control.BackColor, previous);
                control.ForeColor = MapThemeColor(control.ForeColor, previous);
            }

            foreach (Control child in control.Controls)
                RecolorControlTree(child, previous);
            control.Invalidate();
        }

        private static Color MapThemeColor(Color color, ThemePalette previous)
        {
            int value = color.ToArgb();
            if (value == previous.Background.ToArgb()) return Theme.Background;
            if (value == previous.Card.ToArgb()) return Theme.Card;
            if (value == previous.CardAlt.ToArgb()) return Theme.CardAlt;
            if (value == previous.Border.ToArgb()) return Theme.Border;
            if (value == previous.Grid.ToArgb()) return Theme.Grid;
            if (value == previous.Text.ToArgb()) return Theme.Text;
            if (value == previous.TextMuted.ToArgb()) return Theme.TextMuted;
            if (value == previous.TextFaint.ToArgb()) return Theme.TextFaint;
            if (value == previous.Accent.ToArgb()) return Theme.Accent;
            if (value == previous.AccentBlue.ToArgb()) return Theme.AccentBlue;
            if (value == previous.Good.ToArgb()) return Theme.Good;
            if (value == previous.Warning.ToArgb()) return Theme.Warning;
            if (value == previous.Bad.ToArgb()) return Theme.Bad;
            return color;
        }

        private void InitializeTrayIcon()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.BackColor = Theme.Card;
            trayMenu.ForeColor = Theme.Text;

            trayStatusItem = new ToolStripMenuItem("Status: starting");
            trayStatusItem.Enabled = false;
            trayMenu.Items.Add(trayStatusItem);
            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem openItem = new ToolStripMenuItem("Open LagWatch");
            openItem.Click += delegate { RestoreFromTray(); };
            trayMenu.Items.Add(openItem);

            ToolStripMenuItem doctorItem = new ToolStripMenuItem("Troubleshoot");
            doctorItem.Click += delegate
            {
                RestoreFromTray();
                ShowPage(AppPage.CheckFix);
            };
            trayMenu.Items.Add(doctorItem);

            trayMonitoringItem = new ToolStripMenuItem("Start monitoring");
            trayMonitoringItem.Click += async delegate
            {
                RestoreFromTray();
                await ToggleMonitoringAsync();
            };
            trayMenu.Items.Add(trayMonitoringItem);

            trayStartupItem = new ToolStripMenuItem("Run at Windows sign-in");
            trayStartupItem.Checked = startupRegistered;
            trayStartupItem.Click += delegate
            {
                RestoreFromTray();
                ShowOptions();
            };
            trayMenu.Items.Add(trayStartupItem);
            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit LagWatch");
            exitItem.Click += delegate { exitRequested = true; Close(); };
            trayMenu.Items.Add(exitItem);

            trayIcon = new NotifyIcon();
            trayIcon.Text = "LagWatch · starting";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { RestoreFromTray(); };
            trayIcon.BalloonTipClicked += delegate
            {
                Action click = pendingBalloonClick;
                pendingBalloonClick = null;
                if (click != null) click(); else RestoreFromTray();
            };
            try
            {
                using (Icon executableIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                {
                    if (executableIcon != null)
                    {
                        Icon = (Icon)executableIcon.Clone();
                        trayIcon.Icon = (Icon)executableIcon.Clone();
                    }
                    else
                        trayIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                trayIcon.Icon = SystemIcons.Application;
            }
            UpdateTrayState();
        }

        private void HideToTray()
        {
            if (IsDisposed) return;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Normal;
            Hide();
            UpdateTrayState();
            if (!startInBackground && !minimizedTipShown && trayIcon != null)
            {
                minimizedTipShown = true;
                pendingBalloonClick = null;
                trayIcon.BalloonTipTitle = "LagWatch is still monitoring";
                trayIcon.BalloonTipText = "Open LagWatch from the Windows notification area.";
                trayIcon.ShowBalloonTip(2500);
            }
        }

        internal void ExitNow()
        {
            exitRequested = true;
            Close();
        }

        private void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void UpdateTrayState()
        {
            if (trayIcon == null) return;
            string target = targetBox == null ? "" : GetSelectedPingTarget();
            string trayText = monitoring ? "LagWatch · monitoring " + target : "LagWatch · idle";
            trayIcon.Text = trayText.Length > 63 ? trayText.Substring(0, 63) : trayText;
            if (trayStatusItem != null)
                trayStatusItem.Text = monitoring ? "Monitoring: " + target : "Status: idle";
            if (trayMonitoringItem != null)
                trayMonitoringItem.Text = monitoring ? "Pause monitoring" : "Start monitoring";
        }

        private void SetStatus(string text, Color color)
        {
            statusBadge.Text = "  " + text + "  ";
            statusBadge.ForeColor = color;
            statusBadge.BackColor = Color.FromArgb(28, color);
            if (statusBadge.Parent != null)
                PositionHeaderControls(statusBadge.Parent);
            UpdateTrayState();
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
