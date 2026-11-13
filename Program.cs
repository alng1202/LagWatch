using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LagWatch
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            if (HasArgument(args, "--self-test"))
                return RunSelfTests(GetArgumentValue(args, "--report"));

            try { SetProcessDPIAware(); }
            catch { }

            AppSettings settings = SettingsStore.Load();
            Theme.ApplyPreset(settings.ThemeName);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (HasArgument(args, "--render-optimize"))
                return RenderDialog(new OptimizationForm(), GetArgumentValue(args, "--render-optimize"));
            if (HasArgument(args, "--render-options"))
                return RenderDialog(new OptionsForm(settings), GetArgumentValue(args, "--render-options"));

            if (HasArgument(args, "--render-doctor"))
                return RenderDoctorPreview(GetArgumentValue(args, "--render-doctor"),
                    GetArgumentValue(args, "--tier"), GetArgumentValue(args, "--tab"));

            if (HasArgument(args, "--render-preview"))
                return RenderPreview(GetArgumentValue(args, "--render-preview"),
                    GetArgumentValue(args, "--tier"), HasArgument(args, "--collapse-sidebar"), GetArgumentValue(args, "--page"));

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                WriteCrashLog(e.Exception);
                MessageBox.Show("LagWatch encountered an unexpected error. A crash log was saved beside the app.\n\n" +
                    e.Exception.Message, "LagWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                WriteCrashLog(e.ExceptionObject as Exception);
            };

            string startupRegistrationError;
            bool startupOperationSucceeded = StartupRegistration.SetEnabled(
                settings.LaunchAtStartup, out startupRegistrationError);
            bool startupRegistered = settings.LaunchAtStartup && startupOperationSucceeded;
            Application.Run(new MainForm(
                HasArgument(args, "--background"), settings, startupRegistered, startupRegistrationError));
            return 0;
        }

        private static int RunSelfTests(string reportPath)
        {
            List<string> results = new List<string>();
            int failures = 0;
            Action<bool, string> check = delegate(bool condition, string name)
            {
                if (condition) results.Add("PASS  " + name);
                else { results.Add("FAIL  " + name); failures++; }
            };

            check(TargetValidator.IsValid("1.1.1.1"), "IPv4 target validation");
            check(TargetValidator.IsValid("game.example.net"), "DNS target validation");
            check(TargetValidator.IsValid("2606:4700:4700::1111"), "IPv6 target validation");
            check(!TargetValidator.IsValid("https://example.com/path"), "URL rejected as ping target");
            check(!TargetValidator.IsValid("example.com:443"), "host with port rejected");
            check(GameServerDiscovery.IsPublicAddress(System.Net.IPAddress.Parse("8.8.8.8")),
                "public game endpoint accepted");
            check(!GameServerDiscovery.IsPublicAddress(System.Net.IPAddress.Parse("192.168.1.10")),
                "private endpoint rejected");
            check(!SubscriptionPolicy.CanUsePredictions(SubscriptionTier.Free) &&
                SubscriptionPolicy.CanUsePredictions(SubscriptionTier.Plus), "prediction tier policy");
            check(!SubscriptionPolicy.CanUseAdvancedDiagnostics(SubscriptionTier.Plus) &&
                SubscriptionPolicy.CanUseAdvancedDiagnostics(SubscriptionTier.Pro), "diagnostic tier policy");
            check(SubscriptionPolicy.CanRunFix(SubscriptionTier.Free, Netfix.Model.Fix.FlushDNS) &&
                SubscriptionPolicy.CanRunFix(SubscriptionTier.Free, Netfix.Model.Fix.RestartWiFi) &&
                SubscriptionPolicy.CanRunFix(SubscriptionTier.Free, Netfix.Model.Fix.Open(Netfix.Model.Destination.WifiSettings)),
                "free tier keeps safe NetFix fixes");
            check(!SubscriptionPolicy.CanRunFix(SubscriptionTier.Free, Netfix.Model.Fix.UseAutomaticDNS("Wi-Fi")) &&
                SubscriptionPolicy.CanRunFix(SubscriptionTier.Plus, Netfix.Model.Fix.UseAutomaticDNS("Wi-Fi")) &&
                SubscriptionPolicy.CanRunFix(SubscriptionTier.Plus, Netfix.Model.Fix.UnblockSites(new[] { "example.com" })),
                "plus tier unlocks network repair fixes");
            check(!SubscriptionPolicy.CanRunFix(SubscriptionTier.Plus, Netfix.Model.Fix.UseCallPicks("A", "B")) &&
                SubscriptionPolicy.Allows(SubscriptionTier.Pro, Feature.WorkspaceOptimization) &&
                !SubscriptionPolicy.Allows(SubscriptionTier.Plus, Feature.NetworkDetails), "pro tier policy");
            bool everyAdminFixPaid = true;
            foreach (Netfix.Model.FixKind kind in Enum.GetValues(typeof(Netfix.Model.FixKind)))
            {
                Netfix.Model.Fix probe = new Netfix.Model.Fix(kind, "x", "x");
                if (probe.NeedsAdmin && SubscriptionPolicy.CanRunFix(SubscriptionTier.Free, probe)) everyAdminFixPaid = false;
            }
            check(everyAdminFixPaid, "no admin-level NetFix repair is in the free tier");
            check(Netfix.Model.OSILayers.All.Count(l => Netfix.Model.OSILayers.RequiresInternet(l)) == 3,
                "NetFix offline rule: only layers 5-7 need the internet");
            check(OutageMapForm.NormalizeProvider("COMCAST-7922 - Comcast Cable").Equals("Comcast"),
                "consumer ISP name normalization");
            check(OutageMapForm.NormalizeProvider("TDDE-ASN1 Telefonica Germany GmbH & Co.OHG").Equals("Telefonica Germany GmbH & Co.OHG"),
                "registry handle is dropped from the ISP name");
            {
                var dest = new[] { "1.1.1.1" };
                Func<int, string, string> role = (n, a) => HopClassifier.Role(
                    new RouteHop { Number = n, Address = a, Responded = true }, "192.168.1.1", dest, false);
                check(role(1, "192.168.1.1") == "Your router" && role(2, "100.70.0.1") == "ISP carrier-grade NAT" &&
                      role(3, "10.0.0.1").StartsWith("Private") && role(5, "1.1.1.1") == "Destination" &&
                      role(4, "80.81.192.1") == "Internet router",
                    "route map hop device labels");
            }
            check(OutageMapForm.ClassifyOffline(true, true, false, false, false) ==
                "ISP_OR_UPSTREAM_OUTAGE_SUSPECTED", "offline ISP outage inference");
            check(OutageMapForm.ClassifyOffline(false, false, false, false, false) ==
                "LOCAL_ADAPTER_OUTAGE", "offline local outage inference");
            string retentionTest = Path.Combine(Path.GetTempPath(), "lagwatch-retention-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                File.WriteAllText(retentionTest, "timestamp,target,latency_ms\n" +
                    DateTime.Now.AddDays(-31).ToString("o", CultureInfo.InvariantCulture) + ",\"test\",90\n" +
                    DateTime.Now.AddDays(-5).ToString("o", CultureInfo.InvariantCulture) + ",\"test\",20\n");
                new LatencyHistoryStore(retentionTest).Record(DateTime.Now, "test", 21);
                string retained = File.ReadAllText(retentionTest);
                check(!retained.Contains(",90") && retained.Contains(",20"), "30-day prediction retention");
            }
            finally { try { File.Delete(retentionTest); } catch { } }
            check(GameProcessService.ScoreCandidate("cs2", @"C:\Games\cs2.exe") >= 100,
                "known game auto-detection");
            check(GameProcessService.ScoreCandidate("ExampleGame", @"D:\Steam\steamapps\common\Example\game.exe") >= 70,
                "Steam path auto-detection");
            check(GameProcessService.ScoreCandidate("discord", @"C:\Apps\Discord\discord.exe") < 0,
                "desktop app excluded from game detection");
            check(StartupRegistration.BuildStartupCommand(@"C:\Program Files\LagWatch\LagWatch.exe") ==
                "\"C:\\Program Files\\LagWatch\\LagWatch.exe\" --background", "Windows startup command");
            check(Theme.NormalizePreset("midnight") == "Midnight" && Theme.NormalizePreset("unknown") == "Dark",
                "theme preset normalization");

            List<PingSample> samples = new List<PingSample>
            {
                new PingSample { Success = true, LatencyMs = 10 },
                new PingSample { Success = true, LatencyMs = 20 },
                new PingSample { Success = false, LatencyMs = 0 },
                new PingSample { Success = true, LatencyMs = 40 }
            };
            NetworkSnapshot snapshot = StatisticsCalculator.Calculate(samples);
            check(snapshot.TotalSamples == 4, "sample counting");
            check(snapshot.SuccessfulSamples == 3, "success counting");
            check(Math.Abs(snapshot.AverageMs - 23.3333) < 0.01, "average calculation");
            check(Math.Abs(snapshot.JitterMs - 15.0) < 0.01, "jitter calculation");
            check(Math.Abs(snapshot.LossPercent - 25.0) < 0.01, "packet-loss calculation");
            check(snapshot.Quality == "CRITICAL", "quality classification");

            string report = "LagWatch self-test " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture) +
                Environment.NewLine + String.Join(Environment.NewLine, results) + Environment.NewLine +
                (failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
            if (!String.IsNullOrWhiteSpace(reportPath))
                File.WriteAllText(Path.GetFullPath(reportPath), report, new UTF8Encoding(false));
            return failures == 0 ? 0 : 1;
        }

        private static int RenderPreview(string outputPath, string tierName, bool collapseSidebar, string page = null)
        {
            if (String.IsNullOrWhiteSpace(outputPath))
                return 2;

            try
            {
                using (MainForm form = new MainForm())
                {
                    SubscriptionTier previewTier;
                    if (Enum.TryParse(tierName, true, out previewTier))
                        form.SetPreviewTier(previewTier);
                    form.Show();
                    // Let first pings and the background forecast finish before capturing.
                    DateTime settle = DateTime.Now.AddSeconds(2.5);
                    while (DateTime.Now < settle) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                    string switchTheme = Environment.GetEnvironmentVariable("LAGWATCH_PREVIEW_THEME_SWITCH");
                    if (!String.IsNullOrWhiteSpace(switchTheme))
                    {
                        // Live theme preview: Settings opens over the window and a theme is picked there.
                        OptionsForm options = form.OpenSettingsForPreview(switchTheme);
                        for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                        using (Bitmap dialog = new Bitmap(options.Width, options.Height))
                        {
                            options.DrawToBitmap(dialog, new Rectangle(Point.Empty, options.Size));
                            dialog.Save(Path.GetFullPath(outputPath) + ".settings.png", ImageFormat.Png);
                        }
                        options.Close();
                    }
                    if (!String.IsNullOrWhiteSpace(page))
                    {
                        form.SetPreviewPage(page);
                        int seconds = page.StartsWith("Tools", StringComparison.OrdinalIgnoreCase) ? 8 : 2;
                        DateTime pageSettle = DateTime.Now.AddSeconds(seconds);
                        while (DateTime.Now < pageSettle) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                    }
                    if (collapseSidebar)
                    {
                        form.SetPreviewSidebarCollapsed();
                        Application.DoEvents();
                    }
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.GetFullPath(outputPath), ImageFormat.Png);
                    }
                    form.ExitNow();
                }
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.GetFullPath(outputPath) + ".error.txt", ex.ToString());
                return 3;
            }
        }

        /// <summary>Renders Network Doctor with sample results (no scan, nothing changed) to a PNG, for UI checks.</summary>
        private static int RenderDoctorPreview(string outputPath, string tierName, string tabName)
        {
            if (String.IsNullOrWhiteSpace(outputPath)) return 2;
            try
            {
                SubscriptionTier tier;
                if (!Enum.TryParse(tierName, true, out tier)) tier = SubscriptionTier.Free;
                Doctor.DoctorTab tab;
                if (!Enum.TryParse(tabName, true, out tab)) tab = Doctor.DoctorTab.Checkup;
                using (Doctor.DoctorSession session = new Doctor.DoctorSession())
                using (Doctor.DoctorForm form = new Doctor.DoctorForm(session, tier, null))
                {
                    form.Show();
                    if (Environment.GetEnvironmentVariable("LAGWATCH_DOCTOR_LIVE") == "1")
                    {
                        // A real, read-only network scan (layers 1–7, no fixes, no speed test).
                        System.Threading.Tasks.Task scan = session.RunCheckup(Netfix.Model.CheckScope.Network);
                        DateTime deadline = DateTime.Now.AddMinutes(2);
                        while (!scan.IsCompleted && DateTime.Now < deadline) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                    }
                    else session.ShowDemo(Doctor.DoctorSession.DemoResult());
                    form.ShowTab(tab);
                    for (int i = 0; i < 5; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.GetFullPath(outputPath), ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.GetFullPath(outputPath) + ".error.txt", ex.ToString());
                return 3;
            }
        }

        /// <summary>Renders a dialog to a PNG without running it (UI checks at the current display scaling).</summary>
        private static int RenderDialog(Form form, string outputPath)
        {
            if (String.IsNullOrWhiteSpace(outputPath)) { form.Dispose(); return 2; }
            try
            {
                using (form)
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(0, 0);
                    form.Show();
                    for (int i = 0; i < 5; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.GetFullPath(outputPath), ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.GetFullPath(outputPath) + ".error.txt", ex.ToString());
                return 3;
            }
        }

        private static bool HasArgument(string[] args, string name)
        {
            foreach (string arg in args)
                if (String.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string GetArgumentValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static void WriteCrashLog(Exception exception)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LagWatch-crash.log");
                string details = DateTime.Now.ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine +
                    (exception == null ? "Unknown fatal error" : exception.ToString()) + Environment.NewLine + Environment.NewLine;
                File.AppendAllText(path, details);
            }
            catch { }
        }
    }

    internal static class StartupRegistration
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "LagWatch";

        internal static string BuildStartupCommand(string executablePath)
        {
            return "\"" + executablePath + "\" --background";
        }

        internal static bool EnsureRegistered(out string error)
        {
            return SetEnabled(true, out error);
        }

        internal static bool SetEnabled(bool enabled, out string error)
        {
            error = null;
            try
            {
                if (enabled)
                {
                    string command = BuildStartupCommand(Application.ExecutablePath);
                    using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                    {
                        if (runKey == null)
                            throw new InvalidOperationException("The current-user startup registry key could not be opened.");
                        runKey.SetValue(ValueName, command, RegistryValueKind.String);
                    }
                }
                else
                {
                    using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                    {
                        if (runKey != null) runKey.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
