using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LagWatch
{
    /// <summary>The pages the sidebar switches between. Everything opens inside the main window.</summary>
    internal enum AppPage { Monitor, CheckFix, Workspace, Tools }

    /// <summary>The tools on the Tools page (Plus: path check; Pro: the full network toolset).</summary>
    internal enum ToolPage { PathCheck, Details, RouteMap, OutageMap }

    internal sealed partial class MainForm
    {
        private Panel pageHost;
        private Control monitorPage;
        private Panel toolsPage;
        private FlowLayoutPanel toolTabs;
        private Panel toolHost;
        private AccentButton monitorNavButton;
        private AccentButton watchToggle;
        private readonly Dictionary<ToolPage, AccentButton> toolButtons = new Dictionary<ToolPage, AccentButton>();
        private Doctor.DoctorForm doctorView;
        private Form activeTool;
        private AppPage currentPage = AppPage.Monitor;
        private ToolPage currentTool = ToolPage.PathCheck;

        private static Feature RequiredFeature(AppPage page)
        {
            switch (page)
            {
                case AppPage.Workspace: return Feature.WorkspaceOptimization;
                case AppPage.Tools: return Feature.PathCheck;
                case AppPage.CheckFix: return Feature.Checkup;
                default: return Feature.LiveMonitor;
            }
        }

        private static Feature RequiredFeature(ToolPage tool)
        {
            switch (tool)
            {
                case ToolPage.Details: return Feature.NetworkDetails;
                case ToolPage.RouteMap: return Feature.RouteMap;
                case ToolPage.OutageMap: return Feature.OutageCenter;
                default: return Feature.PathCheck;
            }
        }

        private static string ToolTitle(ToolPage tool)
        {
            switch (tool)
            {
                case ToolPage.Details: return "NETWORK DETAILS";
                case ToolPage.RouteMap: return "ROUTE MAP";
                case ToolPage.OutageMap: return "OUTAGE MAP";
                default: return "PATH CHECK";
            }
        }

        /// <summary>Network Doctor, shown inside the main window (one instance for Troubleshoot, Workspace, and Details).</summary>
        private Doctor.DoctorForm DoctorView
        {
            get
            {
                if (doctorView == null || doctorView.IsDisposed)
                {
                    doctorView = new Doctor.DoctorForm(DoctorSession, subscriptionTier, BuildLagWatchSummary);
                    doctorView.Embedded = true;
                    PrepareEmbedded(doctorView);
                }
                return doctorView;
            }
        }

        /// <summary>Turns a window into a borderless panel that fills its page.</summary>
        private static void PrepareEmbedded(Form form)
        {
            form.TopLevel = false;
            form.FormBorderStyle = FormBorderStyle.None;
            form.MinimumSize = Size.Empty;
            form.MaximumSize = Size.Empty;
            form.Dock = DockStyle.Fill;
            form.ShowInTaskbar = false;
        }

        private void ShowPage(AppPage page)
        {
            if (!SubscriptionPolicy.Allows(subscriptionTier, RequiredFeature(page))) page = AppPage.Monitor;
            if (page != AppPage.Tools) CloseActiveTool();
            currentPage = page;
            pageHost.SuspendLayout();
            monitorPage.Visible = page == AppPage.Monitor;
            toolsPage.Visible = page == AppPage.Tools;
            if (page == AppPage.CheckFix || page == AppPage.Workspace)
            {
                Doctor.DoctorForm view = DoctorView;
                if (view.Parent != pageHost) pageHost.Controls.Add(view);
                view.ShowTab(page == AppPage.Workspace ? Doctor.DoctorTab.Workspace : Doctor.DoctorTab.Checkup);
                view.Visible = true;
                view.Show();
                view.BringToFront();
                view.PageShown();
            }
            else if (doctorView != null && !doctorView.IsDisposed && doctorView.Parent == pageHost)
                doctorView.Visible = false;
            if (page == AppPage.Tools) ShowTool(currentTool);
            pageHost.ResumeLayout(true);
            RefreshNavigation();
        }

        /// <summary>Plan changed: show only this plan's pages and tools, and leave a page the plan no longer includes.</summary>
        private void RefreshPagesForTier()
        {
            if (doctorView != null && !doctorView.IsDisposed) doctorView.SetTier(subscriptionTier);
            foreach (var pair in toolButtons)
                pair.Value.Visible = SubscriptionPolicy.Allows(subscriptionTier, RequiredFeature(pair.Key));
            if (watchToggle != null) watchToggle.Visible = SubscriptionPolicy.Allows(subscriptionTier, Feature.BackgroundWatch);
            bool toolDropped = !SubscriptionPolicy.Allows(subscriptionTier, RequiredFeature(currentTool));
            if (toolDropped) currentTool = ToolPage.PathCheck;
            if (pageHost == null) return;
            if (!SubscriptionPolicy.Allows(subscriptionTier, RequiredFeature(currentPage))) ShowPage(AppPage.Monitor);
            // Re-open a tool only when the plan no longer includes it (a theme change mustn't restart a trace).
            else if (currentPage == AppPage.Tools && toolDropped) ShowTool(currentTool);
            RefreshNavigation();
        }

        /// <summary>The current page's sidebar button and tool tab are highlighted.</summary>
        private void RefreshNavigation()
        {
            if (monitorNavButton == null) return;
            var pages = new Dictionary<AccentButton, AppPage>
            {
                { monitorNavButton, AppPage.Monitor }, { doctorButton, AppPage.CheckFix },
                { workspaceButton, AppPage.Workspace }, { toolsButton, AppPage.Tools },
            };
            foreach (var pair in pages)
            {
                SubscriptionTier needs = SubscriptionPolicy.RequiredTier(RequiredFeature(pair.Value));
                pair.Key.AccentColor = needs == SubscriptionTier.Free ? Theme.Accent : SubscriptionPolicy.IdentityColor(needs);
                pair.Key.Primary = pair.Value == currentPage;
            }
            foreach (var pair in toolButtons)
            {
                pair.Value.AccentColor = SubscriptionPolicy.IdentityColor(SubscriptionPolicy.RequiredTier(RequiredFeature(pair.Key)));
                pair.Value.Primary = pair.Key == currentTool;
            }
            if (watchToggle != null)
            {
                watchToggle.Text = backgroundWatchEnabled ? "BACKGROUND WATCH: ON" : "BACKGROUND WATCH: OFF";
                watchToggle.AccentColor = SubscriptionPolicy.IdentityColor(SubscriptionTier.Pro);
                watchToggle.Primary = backgroundWatchEnabled;
            }
        }

        // MARK: Tools page

        private Panel CreateToolsPage()
        {
            Panel page = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Theme.Background };
            toolHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(0, 6, 0, 0) };
            toolTabs = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 46, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                BackColor = Theme.Background, Padding = new Padding(0, 4, 0, 0)
            };
            foreach (ToolPage tool in new[] { ToolPage.PathCheck, ToolPage.Details, ToolPage.RouteMap, ToolPage.OutageMap })
            {
                ToolPage t = tool;
                AccentButton tab = new AccentButton { Text = ToolTitle(t), Height = 34, Margin = new Padding(0, 0, 8, 0) };
                tab.Width = 120; // SizeToolTabs measures the real width after DPI scaling
                tab.Click += delegate { ShowTool(t); };
                toolButtons[t] = tab;
                toolTabs.Controls.Add(tab);
            }
            watchToggle = new AccentButton { Text = "BACKGROUND WATCH: OFF", Height = 34, Margin = new Padding(16, 0, 0, 0) };
            watchToggle.Click += delegate { ToggleBackgroundWatch(); };
            toolTabs.Controls.Add(watchToggle);
            page.Controls.Add(toolHost);
            page.Controls.Add(toolTabs);
            return page;
        }

        /// <summary>Sizes measured from text after DPI scaling (the text is already at the display's size).</summary>
        private void SizeToolTabs()
        {
            foreach (AccentButton b in toolButtons.Values.Concat(new[] { watchToggle }))
                b.Width = TextRenderer.MeasureText(b == watchToggle ? "BACKGROUND WATCH: OFF" : b.Text, b.Font).Width + Theme.Px(this, 34);
        }

        private void ShowTool(ToolPage tool)
        {
            if (!SubscriptionPolicy.Allows(subscriptionTier, RequiredFeature(tool))) tool = ToolPage.PathCheck;
            CloseActiveTool();
            currentTool = tool;
            toolHost.SuspendLayout();
            foreach (Control c in toolHost.Controls.Cast<Control>().ToList())
                if (c != doctorView) { toolHost.Controls.Remove(c); c.Dispose(); }
            if (doctorView != null && doctorView.Parent == toolHost) doctorView.Visible = false;

            string target = GetSelectedPingTarget();
            switch (tool)
            {
                case ToolPage.Details:
                {
                    Doctor.DoctorForm view = DoctorView;
                    if (view.Parent != toolHost) toolHost.Controls.Add(view);
                    view.ShowTab(Doctor.DoctorTab.Details);
                    view.Visible = true;
                    view.Show();
                    view.PageShown();
                    break;
                }
                case ToolPage.RouteMap:
                {
                    // A route to your own router is always one hop, so trace to a public server instead.
                    PingTargetOption option = targetBox.SelectedItem as PingTargetOption;
                    if (option != null && option.IsLocalGateway)
                        OpenTool(new RouteMapForm("1.1.1.1", " · your router is one hop away, so this traces to a public server"));
                    else if (!TargetValidator.IsValid(target))
                        toolHost.Controls.Add(ToolMessage("Enter a valid ping target on the Monitor page, then open Route map again."));
                    else
                        OpenTool(new RouteMapForm(target));
                    break;
                }
                case ToolPage.OutageMap:
                    OpenTool(new OutageMapForm());
                    break;
                default:
                    OpenTool(new MultiTargetMonitorForm(target));
                    break;
            }
            toolHost.ResumeLayout(true);
            RefreshNavigation();
        }

        private void OpenTool(Form form)
        {
            PrepareEmbedded(form);
            toolHost.Controls.Add(form);
            form.Show();
            activeTool = form;
            IEmbeddedTool tool = form as IEmbeddedTool;
            if (tool != null) BeginInvoke(new Action(async delegate { await tool.StartAsync(); }));
        }

        /// <summary>Stops the open tool (its timers and traces) when leaving it.</summary>
        private void CloseActiveTool()
        {
            if (activeTool == null) return;
            Form tool = activeTool;
            activeTool = null;
            try { tool.Close(); } catch { }
            if (tool.Parent != null) tool.Parent.Controls.Remove(tool);
            tool.Dispose();
        }

        private Control ToolMessage(string text)
        {
            CardPanel card = new CardPanel { Dock = DockStyle.Top, Height = 72, Padding = new Padding(18, 14, 18, 14) };
            card.Controls.Add(new Label
            {
                Text = text, Dock = DockStyle.Fill, ForeColor = Theme.TextMuted, Font = Theme.UiFont(10f), UseMnemonic = false
            });
            return card;
        }

        /// <summary>Render previews: opens Settings over the window and picks a theme there, like a user would.</summary>
        internal OptionsForm OpenSettingsForPreview(string theme)
        {
            OptionsForm options = new OptionsForm(appSettings, ApplyThemePreset);
            options.StartPosition = FormStartPosition.Manual;
            options.Location = new Point(Left + Theme.Px(this, 80), Top + Theme.Px(this, 80));
            options.Show(this);
            options.SelectThemeForPreview(theme);
            return options;
        }

        /// <summary>Render previews: opens a page (and tool) by name, e.g. "Tools:RouteMap".</summary>
        internal void SetPreviewPage(string name)
        {
            if (String.IsNullOrWhiteSpace(name)) return;
            string[] parts = name.Split(':');
            AppPage page;
            if (!Enum.TryParse(parts[0], true, out page)) return;
            ToolPage tool;
            if (parts.Length > 1 && Enum.TryParse(parts[1], true, out tool)) currentTool = tool;
            ShowPage(page);
        }
    }

    /// <summary>A tool window that can live inside the main window: its work starts when it's opened there.</summary>
    internal interface IEmbeddedTool
    {
        System.Threading.Tasks.Task StartAsync();
    }
}
