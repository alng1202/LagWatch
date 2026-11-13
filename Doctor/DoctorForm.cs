using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Netfix.Diagnostics;
using Netfix.Model;

namespace LagWatch.Doctor
{
    internal enum DoctorTab { Checkup, Details, Workspace }

    /// <summary>
    /// Network Doctor: NetFix's "Check My Device" inside LagWatch. Problems are always found and explained on every plan;
    /// which one-click repairs and which tabs are available depends on <see cref="SubscriptionPolicy"/>.
    /// Like NetFix, the screen is rebuilt from <see cref="DoctorSession"/> whenever it changes.
    /// </summary>
    internal sealed class DoctorForm : Form
    {
        private readonly DoctorSession session;
        private readonly Func<string> lagWatchSummary;
        private SubscriptionTier tier;
        private DoctorTab tab = DoctorTab.Checkup;

        private readonly Label tierLabel;
        private readonly Label connectionLabel;
        private readonly FlowLayoutPanel tabBar;
        private readonly FlowLayoutPanel content;
        private readonly Dictionary<DoctorTab, AccentButton> tabButtons = new Dictionary<DoctorTab, AccentButton>();
        private bool rebuildQueued;
        private TableLayoutPanel rootLayout;
        private readonly Label titleLabel;
        private bool embedded;

        /// <summary>
        /// Shown as a page inside LagWatch's main window: the sidebar picks the tab (so there's no tab row), the title
        /// names the page, and the plan label is left to the main header.
        /// </summary>
        public bool Embedded
        {
            get { return embedded; }
            set
            {
                embedded = value;
                rootLayout.Padding = value ? new Padding(0) : new Padding(20, 16, 20, 16);
                Rebuild();
            }
        }

        /// <summary>The page was opened: refresh finished results quietly, like NetFix does when its window comes forward.</summary>
        public async void PageShown()
        {
            if (session.Result != null) await session.Recheck(false);
        }

        // Workspace tab state (speaker/mic tester)
        private string testingId;
        private string selectedDeviceId;
        private readonly Dictionary<string, (bool Good, string Message)> testResults = new Dictionary<string, (bool, string)>();
        private ProgressBar micLevelBar;
        private CheckupResult workspaceHealth;
        private bool workspaceHealthRunning;

        public DoctorForm(DoctorSession session, SubscriptionTier tier, Func<string> lagWatchSummary)
        {
            Theme.BeginDpiLayout(this);
            this.session = session;
            this.tier = tier;
            this.lagWatchSummary = lagWatchSummary;

            Text = "LagWatch — Network Doctor";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 600);
            Size = new Size(900, 760);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.UiFont(9f);
            try
            {
                using (Icon sourceIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    if (sourceIcon != null) Icon = (Icon)sourceIcon.Clone();
            }
            catch { }

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(20, 16, 20, 16);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            Panel header = new Panel { Dock = DockStyle.Fill };
            titleLabel = new Label
            {
                Text = "NETWORK DOCTOR", AutoSize = true, Font = Theme.UiFont(15f, true),
                ForeColor = Theme.Text, Location = new Point(0, 0), UseMnemonic = false
            };
            header.Controls.Add(titleLabel);
            connectionLabel = new Label
            {
                AutoSize = true, ForeColor = Theme.TextMuted, Location = new Point(2, 34)
            };
            header.Controls.Add(connectionLabel);
            tierLabel = new Label
            {
                AutoSize = true, Font = Theme.UiFont(8.5f, true), Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            header.Controls.Add(tierLabel);
            header.Resize += delegate { tierLabel.Location = new Point(Math.Max(0, header.ClientSize.Width - tierLabel.Width), 6); };
            root.Controls.Add(header, 0, 0);

            tabBar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            AddTab(DoctorTab.Checkup, "TROUBLESHOOT");
            AddTab(DoctorTab.Details, "DETAILS");
            AddTab(DoctorTab.Workspace, "WORKSPACE");
            root.Controls.Add(tabBar, 0, 1);
            rootLayout = root;

            content = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
                BackColor = Theme.Background, Padding = new Padding(0, 6, 0, 12)
            };
            content.Resize += delegate { ResizeChildren(); };
            root.Controls.Add(content, 0, 2);

            session.Changed += OnSessionChanged;
            FormClosed += delegate { session.Changed -= OnSessionChanged; };
            Activated += async delegate
            {
                // Like NetFix: coming back to the window refreshes finished results quietly (at most every 15 s).
                if (session.Result != null) await session.Recheck(false);
            };
            Rebuild();
            Theme.EndDpiLayout(this);
        }

        public void SetTier(SubscriptionTier newTier)
        {
            tier = newTier;
            if (!SubscriptionPolicy.Allows(tier, RequiredFeature(tab))) tab = DoctorTab.Checkup;
            Rebuild();
        }

        public void ShowTab(DoctorTab target)
        {
            if (!SubscriptionPolicy.Allows(tier, RequiredFeature(target)))
            {
                TierPrompt.Show(this, TabName(target), SubscriptionPolicy.RequiredTier(RequiredFeature(target)), tier);
                return;
            }
            tab = target;
            Rebuild();
        }

        private static Feature RequiredFeature(DoctorTab t)
        {
            switch (t)
            {
                case DoctorTab.Details: return Feature.NetworkDetails;
                case DoctorTab.Workspace: return Feature.WorkspaceOptimization;
                default: return Feature.Checkup;
            }
        }

        private static string TabName(DoctorTab t)
        {
            switch (t)
            {
                case DoctorTab.Details: return "Network details";
                case DoctorTab.Workspace: return "Workspace optimization";
                default: return "Troubleshoot";
            }
        }

        private void AddTab(DoctorTab t, string text)
        {
            AccentButton button = new AccentButton { Text = text, Height = 34, Margin = new Padding(0, 4, 8, 0) };
            button.Width = TextRenderer.MeasureText(text, button.Font).Width + 30;
            button.Click += delegate { ShowTab(t); };
            tabButtons[t] = button;
            tabBar.Controls.Add(button);
        }

        private void OnSessionChanged()
        {
            // Coalesce bursts of changes (progress steps) into one rebuild.
            if (rebuildQueued || IsDisposed || !IsHandleCreated) return;
            rebuildQueued = true;
            BeginInvoke(new Action(() => { rebuildQueued = false; if (!IsDisposed) Rebuild(); }));
        }

        // MARK: Building the screen

        private void Rebuild()
        {
            Color identity = SubscriptionPolicy.IdentityColor(tier);
            titleLabel.Text = !embedded ? "NETWORK DOCTOR"
                : tab == DoctorTab.Workspace ? "WORKSPACE"
                : tab == DoctorTab.Details ? "NETWORK DETAILS" : "TROUBLESHOOT";
            tierLabel.Visible = !embedded;
            tierLabel.Text = SubscriptionPolicy.Banner(tier);
            tierLabel.ForeColor = identity;
            tierLabel.Location = new Point(Math.Max(0, tierLabel.Parent.ClientSize.Width - tierLabel.Width), 6);
            connectionLabel.Text = session.Connectivity.Description;
            connectionLabel.ForeColor = session.Connectivity.IsOnline ? Theme.Good : Theme.Warning;
            // Only the plan's own tabs are shown. Free has just Troubleshoot, so the tab bar disappears.
            int visibleTabs = 0;
            foreach (var pair in tabButtons)
            {
                bool allowed = SubscriptionPolicy.Allows(tier, RequiredFeature(pair.Key));
                pair.Value.Visible = allowed;
                if (allowed) visibleTabs++;
                pair.Value.AccentColor = identity;
                pair.Value.Primary = pair.Key == tab;
            }
            bool showTabs = visibleTabs > 1 && !embedded;
            tabBar.Visible = showTabs;
            rootLayout.RowStyles[1].Height = showTabs ? Theme.Px(this, 46) : 0;

            int scroll = content.VerticalScroll.Value;
            content.SuspendLayout();
            foreach (Control c in content.Controls.Cast<Control>().ToList()) c.Dispose();
            content.Controls.Clear();
            micLevelBar = null;
            switch (tab)
            {
                case DoctorTab.Details: BuildDetails(); break;
                case DoctorTab.Workspace: BuildWorkspace(); break;
                default: BuildCheckup(); break;
            }
            ResizeChildren();
            content.ResumeLayout(true);
            try { content.VerticalScroll.Value = Math.Min(scroll, content.VerticalScroll.Maximum); content.PerformLayout(); } catch { }
        }

        private int ContentWidth { get { return Math.Max(300, content.ClientSize.Width - 8); } }

        /// <summary>Sizes every card to the window width and to the height of what's in it (labels wrap, rows of buttons wrap).</summary>
        private void ResizeChildren()
        {
            int width = ContentWidth;
            int inner = width - 34;
            content.SuspendLayout();
            foreach (Control c in content.Controls)
            {
                if (c is CardPanel card && card.Controls.Count > 0)
                {
                    FlowLayoutPanel stack = (FlowLayoutPanel)card.Controls[0];
                    foreach (Control child in stack.Controls)
                    {
                        if (child is Label l) l.MaximumSize = new Size(inner, 0);
                        else if (child is FlowLayoutPanel row) row.MaximumSize = new Size(inner, 0);
                    }
                    stack.MaximumSize = new Size(inner, 0);
                    Size preferred = stack.GetPreferredSize(new Size(inner, 0));
                    stack.Size = new Size(inner, preferred.Height);
                    card.Size = new Size(width, preferred.Height + card.Padding.Vertical);
                }
                else if (c is FlowLayoutPanel row)
                {
                    row.MaximumSize = new Size(width, 0);
                    row.Width = width;
                }
                else c.Width = width;
            }
            content.ResumeLayout(true);
        }

        // MARK: Checkup

        private void BuildCheckup()
        {
            FlowLayoutPanel actions = Row();
            bool needsGeneralCheck = session.Result == null || session.Result.Scope != CheckScope.Full;
            AccentButton full = ActionButton(session.Result == null ? "CHECK MY DEVICE" : needsGeneralCheck ? "RUN GENERAL CHECK" : "CHECK AGAIN", true);
            full.Height = 40;
            full.Width += 20;
            full.Enabled = !session.IsRunning;
            full.Click += async delegate { await session.RunCheckup(CheckScope.Full); };
            actions.Controls.Add(full);
            if (SubscriptionPolicy.Allows(tier, Feature.CustomScans))
            {
                // Plus: the four quicker scans live in one menu instead of four buttons.
                AccentButton quick = ActionButton("QUICK SCAN  ▾", false);
                quick.Height = 40;
                quick.Enabled = !session.IsRunning;
                quick.Click += delegate
                {
                    ContextMenuStrip menu = new ContextMenuStrip { BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.UiFont(10f), ShowImageMargin = false };
                    // Check My Device has its own button, so the menu holds only the quicker scans.
                    foreach (CheckScope scope in new[] { CheckScope.Performance, CheckScope.Network, CheckScope.Hardware, CheckScope.Sound })
                    {
                        CheckScope s = scope;
                        // "&" would be read as a keyboard shortcut marker and vanish ("Sound, camera & Bluetooth").
                        ToolStripMenuItem item = new ToolStripMenuItem(s.Title().Replace("&", "&&")) { Padding = new Padding(4, 6, 4, 6) };
                        item.Click += async delegate { await session.RunCheckup(s); };
                        menu.Items.Add(item);
                    }
                    menu.Closed += delegate { BeginInvoke(new Action(menu.Dispose)); };
                    menu.Show(quick, new Point(0, quick.Height));
                };
                actions.Controls.Add(quick);
            }
            content.Controls.Add(actions);

            if (session.IsRunning)
            {
                CardPanel card = Card();
                AddText(card, session.Step, 11f, Theme.Text, true);
                ProgressBar bar = new ProgressBar { Height = 10, Width = 400, Value = (int)(Math.Max(0, Math.Min(1, session.Progress)) * 100), Margin = new Padding(0, 6, 0, 0) };
                AddControl(card, bar);
                content.Controls.Add(card);
                return;
            }

            CheckupResult result = session.Result;
            if (result == null)
            {
                CardPanel intro = Card();
                AddText(intro, "Is something not working?", 13f, Theme.Text, true);
                AddText(intro, "LagWatch checks your PC, Wi‑Fi, router and internet, tells you what's wrong in plain words, " +
                    "and fixes it when you say so.", 10f, Theme.TextMuted);
                content.Controls.Add(intro);
                return;
            }

            CardPanel summary = Card();
            AddText(summary, result.Headline, 14f, result.ProblemCount > 0 ? Theme.Bad : result.Issues.Count > 0 ? Theme.Warning : Theme.Good, true);
            AddText(summary, result.Scope.Title() + " · " + result.Date.ToString("HH:mm"), 8.5f, Theme.TextFaint);
            var plan = DoctorSession.FixAllPlan(result.Issues, tier);
            if (SubscriptionPolicy.Allows(tier, Feature.FixAll) && plan.Count >= 2)
            {
                FlowLayoutPanel summaryActions = Row();
                summaryActions.Margin = new Padding(0, 8, 0, 0);
                AccentButton fixAll = ActionButton(session.IsFixingAll ? "FIXING…" : "FIX ALL (" + plan.Count + ")", true);
                fixAll.Enabled = !session.IsFixingAll;
                fixAll.Click += async delegate { await RunFixAll(result); };
                summaryActions.Controls.Add(fixAll);
                AddControl(summary, summaryActions);
            }
            content.Controls.Add(summary);

            foreach (Issue issue in result.Issues) content.Controls.Add(IssueCard(issue));

            foreach (Issue fixedIssue in session.ResolvedIssues)
            {
                CardPanel card = Card();
                AddText(card, "✓ FIXED · " + fixedIssue.Title, 10f, Theme.Good, true);
                content.Controls.Add(card);
            }

            if (result.Healthy.Count > 0)
            {
                CardPanel good = Card();
                AddText(good, "LOOKS GOOD", 8.5f, Theme.TextFaint, true);
                foreach (HealthItem h in result.Healthy)
                    AddText(good, "✓  " + h.Topic.Name() + " — " + h.Detail, 9f, Theme.Text);
                content.Controls.Add(good);
            }
            if (result.Blocked.Count > 0)
            {
                CardPanel blocked = Card();
                AddText(blocked, "NOT CHECKED YET", 8.5f, Theme.TextFaint, true);
                AddText(blocked, "Fix the problem above first, then these can be checked: " + string.Join(", ", result.Blocked.Select(b => b.Name())) + ".", 9f, Theme.TextMuted);
                content.Controls.Add(blocked);
            }
        }

        private CardPanel IssueCard(Issue issue)
        {
            CardPanel card = Card();
            Color color = issue.Severity == Severity.Problem ? Theme.Bad : Theme.Warning;
            AddText(card, (issue.Severity == Severity.Problem ? "PROBLEM" : "WORTH A LOOK") + " · " + issue.Topic.Name().ToUpperInvariant(), 8f, color, true);
            AddText(card, issue.Title, 11.5f, Theme.Text, true);
            if (!string.IsNullOrWhiteSpace(issue.Explanation)) AddText(card, issue.Explanation, 9f, Theme.TextMuted);

            // Only the fixes this plan includes are shown: the best one as a button, any others as quiet links.
            var allowedFixes = issue.Fixes.Where(f => SubscriptionPolicy.CanRunFix(tier, f)).ToList();
            var hiddenFix = issue.Fixes.FirstOrDefault(f => !SubscriptionPolicy.CanRunFix(tier, f));
            if (allowedFixes.Count > 0)
            {
                FlowLayoutPanel fixes = Row();
                fixes.Margin = new Padding(0, 8, 0, 0);
                for (int i = 0; i < allowedFixes.Count; i++)
                {
                    Fix f = allowedFixes[i];
                    FixState state;
                    session.FixStates.TryGetValue((issue.Id, f), out state);
                    bool running = state != null && state.Phase == FixPhase.Running;
                    Control control;
                    if (i == 0)
                    {
                        AccentButton button = ActionButton(running ? "WORKING…" : f.Label, true);
                        button.Enabled = !running;
                        button.Click += async delegate { await ClickFix(issue, f); };
                        control = button;
                    }
                    else
                    {
                        LinkLabel link = new LinkLabel
                        {
                            Text = running ? "Working…" : f.Label, AutoSize = true, Font = Theme.UiFont(9.5f),
                            LinkColor = Theme.AccentBlue, ActiveLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline,
                            Margin = new Padding(8, 9, 8, 0), UseMnemonic = false, Enabled = !running
                        };
                        link.LinkClicked += async delegate { await ClickFix(issue, f); };
                        control = link;
                    }
                    fixes.Controls.Add(control);
                }
                AddControl(card, fixes);
            }
            if (hiddenFix != null && allowedFixes.Count == 0)
            {
                SubscriptionTier needed = SubscriptionPolicy.RequiredTier(hiddenFix);
                AddText(card, "LagWatch " + needed + " can fix this for you in one click.", 8.5f, SubscriptionPolicy.IdentityColor(needed));
            }
            if (issue.Fixes.Count > 0)
            {
                foreach (Fix f in issue.Fixes)
                {
                    FixState state;
                    if (session.FixStates.TryGetValue((issue.Id, f), out state) && state.Phase != FixPhase.Running && !string.IsNullOrEmpty(state.Message))
                        AddText(card, (state.Phase == FixPhase.Done ? "✓ " : "✕ ") + state.Message, 9f, state.Phase == FixPhase.Done ? Theme.Good : Theme.Bad);
                }
            }
            // Steps that say "Click <a fix this plan hides>" would point at a button that isn't there.
            var hiddenLabels = issue.Fixes.Where(f => !SubscriptionPolicy.CanRunFix(tier, f)).Select(f => f.Label).ToList();
            var steps = issue.Steps.Where(s => !hiddenLabels.Any(l => s.IndexOf(l, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (steps.Count > 0)
            {
                AddText(card, allowedFixes.Count > 0 ? "OR DO IT YOURSELF" : "WHAT TO DO", 8f, Theme.TextFaint, true);
                int n = 1;
                foreach (string step in steps) AddText(card, (n++) + ".  " + step, 9f, Theme.Text);
            }
            if (!string.IsNullOrWhiteSpace(issue.Technical) && SubscriptionPolicy.Allows(tier, Feature.NetworkDetails))
                AddText(card, "Details: " + issue.Technical, 8f, Theme.TextFaint);
            return card;
        }

        private async Task ClickFix(Issue issue, Fix fix)
        {
            if (!SubscriptionPolicy.CanRunFix(tier, fix))
            {
                TierPrompt.Show(this, "“" + fix.Label + "”", SubscriptionPolicy.RequiredTier(fix), tier);
                return;
            }
            string confirmation = fix.Confirmation;
            if (confirmation != null || fix.NeedsAdmin)
            {
                string text = (confirmation ?? fix.Label + ".") + (fix.NeedsAdmin ? "\n\nWindows will ask you to allow changes (click Yes)." : "");
                if (MessageBox.Show(this, text, fix.Label, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            }
            await session.RunFix(issue, fix);
        }

        private async Task RunFixAll(CheckupResult result)
        {
            if (!SubscriptionPolicy.Allows(tier, Feature.FixAll)) { TierPrompt.Show(this, "Fix all", SubscriptionTier.Plus, tier); return; }
            var plan = DoctorSession.FixAllPlan(result.Issues, tier);
            if (plan.Count == 0) return;
            if (MessageBox.Show(this, DoctorSession.FixAllSummary(plan), "Fix all", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            await session.FixAll(plan);
        }

        private void SaveReport()
        {
            if (!SubscriptionPolicy.Allows(tier, Feature.NetworkReport)) { TierPrompt.Show(this, "Network report", SubscriptionTier.Pro, tier); return; }
            try
            {
                string path = NetworkReport.Save(session.Result, session.Reports, lagWatchSummary == null ? null : lagWatchSummary());
                MessageBox.Show(this, "Saved on your Desktop:\n" + path, "Network report", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not save the report", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // MARK: Network details (Pro)

        /// <summary>Whether Network details shows each OSI layer's card (kept across rebuilds).</summary>
        private bool osiLayersExpanded;

        private void BuildDetails()
        {
            FlowLayoutPanel actions = Row();
            AccentButton run = ActionButton(session.LayersRunning ? "CHECKING…" : "RUN ALL LAYERS", true);
            run.Enabled = !session.LayersRunning && !session.IsRunning;
            run.Click += async delegate { await session.RunAllLayers(); };
            actions.Controls.Add(run);
            AccentButton report = ActionButton("SAVE NETWORK REPORT", false);
            report.Click += delegate { SaveReport(); };
            actions.Controls.Add(report);
            content.Controls.Add(actions);

            CardPanel adapters = Card();
            AddText(adapters, "ADAPTERS", 8.5f, Theme.TextFaint, true);
            try
            {
                var all = NetworkInfo.Adapters();
                var primary = NetworkInfo.Primary(all);
                foreach (var a in all.OrderByDescending(a => a.Up).ThenBy(a => a.Name))
                {
                    string line = (a == primary ? "★ " : "   ") + a.Name + " · " + a.Kind + " · " + (a.Up ? "up" : "down") +
                        (a.SpeedMbps > 0 ? " · " + a.SpeedMbps + " Mbps" : "") +
                        (a.Address != null ? " · " + a.Address + "/" + a.PrefixLength : "") +
                        (a.Gateway != null ? " · gw " + a.Gateway : "") +
                        (a.Dns.Count > 0 ? " · DNS " + string.Join(", ", a.Dns) : "") +
                        (a.Up ? " · " + (a.Dhcp ? "DHCP" : "manual") : "") +
                        (a.ErrorsIn + a.ErrorsOut > 0 ? " · errors " + (a.ErrorsIn + a.ErrorsOut) : "");
                    AddText(adapters, line, 8.5f, a.Up ? Theme.Text : Theme.TextFaint);
                }
            }
            catch (Exception ex) { AddText(adapters, "Couldn't read adapters: " + ex.Message, 9f, Theme.Warning); }
            content.Controls.Add(adapters);

            if (session.Reports.Count == 0)
            {
                CardPanel empty = Card();
                AddText(empty, session.LayersRunning ? "Checking…" : "Run all layers to see every finding.", 9.5f, Theme.TextMuted);
                content.Controls.Add(empty);
                return;
            }

            // OSI LAYERS: the bottom-up summary, with each layer's findings in a dropdown (closed by default).
            CardPanel osi = Card();
            AddText(osi, "OSI LAYERS", 8.5f, Theme.TextFaint, true);
            AddText(osi, Diagnosis.Summarize(session.Reports), 9f, Theme.Text);
            AccentButton toggle = ActionButton(osiLayersExpanded ? "HIDE LAYERS  ▴" : "SHOW LAYERS  ▾", false);
            toggle.Margin = new Padding(0, 8, 0, 0);
            toggle.Click += delegate { osiLayersExpanded = !osiLayersExpanded; Rebuild(); };
            AddControl(osi, toggle);
            content.Controls.Add(osi);
            if (!osiLayersExpanded) return;

            foreach (LayerReport r in session.Reports.OrderBy(r => r.Layer))
            {
                CardPanel card = Card();
                AddText(card, r.Layer.Title().ToUpperInvariant() + (r.Layer.RequiresInternet() ? " · NEEDS INTERNET" : " · OFFLINE-CAPABLE"),
                    8.5f, StatusColor(r.Status), true);
                foreach (Finding f in r.Findings) AddFinding(card, f);
                content.Controls.Add(card);
            }
        }

        private void AddFinding(CardPanel card, Finding f)
        {
            AddText(card, StatusGlyph(f.Status) + "  " + f.Title + " — " + f.Detail, 9f, f.Status >= CheckStatus.Warning ? StatusColor(f.Status) : Theme.Text);
            if (f.Status >= CheckStatus.Warning && !string.IsNullOrWhiteSpace(f.Advice))
                AddText(card, "     ↳ " + f.Advice, 8.5f, Theme.TextMuted);
        }

        private static string StatusGlyph(CheckStatus s)
        {
            switch (s)
            {
                case CheckStatus.Pass: return "✓";
                case CheckStatus.Warning: return "!";
                case CheckStatus.Fail: return "✕";
                case CheckStatus.Skipped: return "–";
                default: return "·";
            }
        }

        private static Color StatusColor(CheckStatus s)
        {
            switch (s)
            {
                case CheckStatus.Pass: return Theme.Good;
                case CheckStatus.Warning: return Theme.Warning;
                case CheckStatus.Fail: return Theme.Bad;
                default: return Theme.TextMuted;
            }
        }

        // MARK: Workspace (Pro)

        private void BuildWorkspace()
        {
            CardPanel intro = Card();
            AddText(intro, "Workspace optimization", 12f, Theme.Text, true);
            AddText(intro, "Set the speaker and microphone your calls use (LagWatch reminds you if Windows switches away), " +
                "find apps slowing this PC down.", 9f, Theme.TextMuted);
            FlowLayoutPanel actions = Row();
            actions.Margin = new Padding(0, 8, 0, 0);
            AccentButton health = ActionButton(workspaceHealthRunning ? "CHECKING…" : "CHECK PC PERFORMANCE", true);
            health.Enabled = !workspaceHealthRunning;
            health.Click += async delegate { await RunWorkspaceHealth(); };
            actions.Controls.Add(health);
            AddControl(intro, actions);
            content.Controls.Add(intro);

            if (workspaceHealth != null)
            {
                if (workspaceHealth.Issues.Count == 0)
                {
                    CardPanel ok = Card();
                    AddText(ok, "This PC isn't being slowed down by anything right now.", 10f, Theme.Good, true);
                    content.Controls.Add(ok);
                }
                foreach (Issue issue in workspaceHealth.Issues) content.Controls.Add(IssueCard(issue));
                if (workspaceHealth.Healthy.Count > 0)
                {
                    CardPanel good = Card();
                    foreach (HealthItem h in workspaceHealth.Healthy) AddText(good, "✓  " + h.Topic.Name() + " — " + h.Detail, 9f, Theme.Text);
                    content.Controls.Add(good);
                }
            }

            CallPicks picks = CallPicks.Load();
            content.Controls.Add(DeviceCard("SPEAKERS", false, picks.SpeakerId));
            content.Controls.Add(DeviceCard("MICROPHONES", true, picks.MicrophoneId));
        }

        private async Task RunWorkspaceHealth()
        {
            workspaceHealthRunning = true;
            Rebuild();
            var pc = await Task.Run(() => PcHealth.Check(true, true));
            workspaceHealth = new CheckupResult { Issues = pc.Issues, Healthy = pc.Healthy, Scope = CheckScope.Performance };
            workspaceHealthRunning = false;
            if (!IsDisposed) Rebuild();
        }

        private CardPanel DeviceCard(string title, bool input, string pickId)
        {
            CardPanel card = Card();
            AddText(card, title, 8.5f, Theme.TextFaint, true);
            List<Audio.Device> devices;
            string defaultId;
            try { devices = Audio.Devices(input); defaultId = Audio.Default(input)?.Id; }
            catch (Exception ex) { AddText(card, "Could not read devices: " + ex.Message, 9f, Theme.Warning); return card; }
            if (devices.Count == 0) { AddText(card, "None found.", 9f, Theme.TextMuted); return card; }
            ComboBox picker = new ComboBox { Width = Math.Max(300, ContentWidth - 34), Height = 32, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.CardAlt, ForeColor = Theme.Text, Font = Theme.UiFont(9f), Margin = new Padding(0, 0, 0, 8) };
            foreach (Audio.Device device in devices)
            {
                string tags = (device.Id == defaultId ? "  · DEFAULT" : "") + (device.Muted == true ? "  · MUTED" : "") + (device.Id == pickId ? "  · CALLS" : "");
                picker.Items.Add(device.Name + tags);
            }
            int selected = devices.FindIndex(d => d.Id == selectedDeviceId);
            if (selected < 0) selected = devices.FindIndex(d => d.Id == testingId);
            if (selected < 0) selected = devices.FindIndex(d => d.Id == defaultId);
            if (selected < 0) selected = devices.FindIndex(d => d.Id == pickId);
            picker.SelectedIndex = selected < 0 ? 0 : selected;
            AddControl(card, picker);
            FlowLayoutPanel row = Row();
            AccentButton test = ActionButton(testingId != null ? (input ? "LISTENING…" : "PLAYING…") : input ? "TEST MIC" : "PLAY SOUND", false);
            AccentButton setDefault = ActionButton("SET AS DEFAULT", false);
            AccentButton useForCalls = ActionButton("USE FOR CALLS", false);
            Action updateActions = delegate
            {
                Audio.Device selectedDevice = picker.SelectedIndex >= 0 && picker.SelectedIndex < devices.Count ? devices[picker.SelectedIndex] : null;
                bool testing = testingId != null;
                test.Enabled = selectedDevice != null && !testing;
                setDefault.Enabled = selectedDevice != null && !testing && selectedDevice.Id != defaultId;
                useForCalls.Enabled = selectedDevice != null && !testing && selectedDevice.Id != pickId;
                if (input && testing && selectedDevice != null && selectedDevice.Id == testingId && micLevelBar == null)
                {
                    micLevelBar = new ProgressBar { Width = 160, Height = 12, Margin = new Padding(6, 12, 0, 0) };
                    row.Controls.Add(micLevelBar);
                }
            };
            test.Click += async delegate { if (picker.SelectedIndex >= 0 && picker.SelectedIndex < devices.Count) { selectedDeviceId = devices[picker.SelectedIndex].Id; await TestDevice(devices[picker.SelectedIndex]); } };
            setDefault.Click += delegate { if (picker.SelectedIndex >= 0 && picker.SelectedIndex < devices.Count) { selectedDeviceId = devices[picker.SelectedIndex].Id; SetDefaultDevice(devices[picker.SelectedIndex]); } };
            useForCalls.Click += delegate { if (picker.SelectedIndex >= 0 && picker.SelectedIndex < devices.Count) { selectedDeviceId = devices[picker.SelectedIndex].Id; UseForCalls(devices[picker.SelectedIndex]); } };
            picker.SelectedIndexChanged += delegate
            {
                if (picker.SelectedIndex >= 0 && picker.SelectedIndex < devices.Count) selectedDeviceId = devices[picker.SelectedIndex].Id;
                updateActions();
            };
            row.Controls.Add(test); row.Controls.Add(setDefault); row.Controls.Add(useForCalls);
            updateActions();
            AddControl(card, row);
            if (picker.SelectedIndex >= 0 && picker.SelectedIndex < devices.Count)
            {
                (bool Good, string Message) r;
                if (testResults.TryGetValue(devices[picker.SelectedIndex].Id, out r)) AddText(card, r.Message, 8.5f, r.Good ? Theme.Good : Theme.Warning);
            }
            return card;
        }

        private async Task TestDevice(Audio.Device device)
        {
            if (testingId != null) return;
            testingId = device.Id;
            Rebuild();
            if (device.Input)
            {
                double? loudest = await Audio.ListenToMicrophone(device.Id, 5, level =>
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(() => { if (micLevelBar != null && !micLevelBar.IsDisposed) micLevelBar.Value = (int)(Math.Max(0, Math.Min(1, level)) * 100); }));
                });
                testResults[device.Id] = loudest == null
                    ? (false, "Couldn't listen to it. If Windows blocks microphones in Privacy settings, LagWatch can't hear it either.")
                    : loudest >= Audio.HeardLevel ? (true, "Heard you. Click Use for calls to use it in calls.")
                    : (false, "Heard nothing. Is it muted, or switched off on the headset?");
            }
            else
            {
                bool played = await Audio.PlayTestSound(device.Id);
                testResults[device.Id] = played ? (true, "Played a chime. Click Set as default to use it in Windows.") : (false, "Couldn't play on it. Is it turned on and plugged in?");
            }
            testingId = null;
            if (!IsDisposed) Rebuild();
        }

        private void SetDefaultDevice(Audio.Device device)
        {
            bool ok = Audio.MakeDefault(device.Id);
            testResults[device.Id] = ok ? (true, "Windows now uses this device by default for apps and calls.")
                                        : (false, "Couldn't switch. Pick it in Sound settings instead.");
            Rebuild();
        }

        private void UseForCalls(Audio.Device device)
        {
            var picks = CallPicks.Load();
            if (device.Input) { picks.MicrophoneId = device.Id; picks.MicrophoneName = device.Name; }
            else { picks.SpeakerId = device.Id; picks.SpeakerName = device.Name; }
            picks.Save();
            testResults[device.Id] = (true, "Saved for calls. LagWatch will remind you if Windows switches away from it.");
            Rebuild();
        }

        // MARK: Little builders

        private static FlowLayoutPanel Row()
        {
            return new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 8)
            };
        }

        private static AccentButton ActionButton(string text, bool primary)
        {
            AccentButton b = new AccentButton { Text = text, Primary = primary, Height = 34, Margin = new Padding(0, 0, 8, 6) };
            b.Width = TextRenderer.MeasureText(text, b.Font).Width + 30;
            return b;
        }

        private static CardPanel Card()
        {
            CardPanel card = new CardPanel
            {
                AutoSize = false, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 10)
            };
            FlowLayoutPanel stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = false,
                Location = new Point(16, 12), BackColor = Theme.Card
            };
            card.Controls.Add(stack);
            return card;
        }

        private static FlowLayoutPanel Stack(CardPanel card) { return (FlowLayoutPanel)card.Controls[0]; }

        private void AddControl(CardPanel card, Control control) { Stack(card).Controls.Add(control); }

        private void AddText(CardPanel card, string text, float size, Color color, bool bold = false)
        {
            Label label = new Label
            {
                Text = text, AutoSize = true, ForeColor = color, Tag = "wrap", UseMnemonic = false,
                Font = Theme.UiFont(size, bold),
                MaximumSize = new Size(ContentWidth - 40, 0), Margin = new Padding(0, 2, 0, 2)
            };
            Stack(card).Controls.Add(label);
        }

    }

    /// <summary>The "this needs a plan" message, with what each plan includes.</summary>
    internal static class TierPrompt
    {
        public static void Show(IWin32Window owner, string feature, SubscriptionTier required, SubscriptionTier current)
        {
            string text = feature + " is part of LagWatch " + required.ToString().ToUpperInvariant() + ". You're on " +
                current.ToString().ToUpperInvariant() + ".\n\n" + required.ToString().ToUpperInvariant() + " adds:\n• " +
                string.Join("\n• ", SubscriptionPolicy.PlanLines(required)) +
#if DEV_BUILD
                "\n\nDEV BUILD: switch plans with the FREE / PLUS / PRO selector at the top right of the main window.";
#else
                "\n\nProblems are always found and explained on every plan; only this one-click action needs the upgrade.";
#endif
            MessageBox.Show(owner, text, "LagWatch " + required.ToString().ToUpperInvariant(), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}

