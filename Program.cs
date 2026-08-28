using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
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

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (HasArgument(args, "--render-preview"))
                return RenderPreview(GetArgumentValue(args, "--render-preview"));

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

            Application.Run(new MainForm());
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
                SubscriptionPolicy.CanUsePredictions(SubscriptionTier.Basic), "prediction tier policy");
            check(!SubscriptionPolicy.CanUseAdvancedDiagnostics(SubscriptionTier.Basic) &&
                SubscriptionPolicy.CanUseAdvancedDiagnostics(SubscriptionTier.Pro), "diagnostic tier policy");
            check(OutageMapForm.NormalizeProvider("COMCAST-7922 - Comcast Cable").Equals("Comcast"),
                "consumer ISP name normalization");
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

        private static int RenderPreview(string outputPath)
        {
            if (String.IsNullOrWhiteSpace(outputPath))
                return 2;

            try
            {
                using (MainForm form = new MainForm())
                {
                    form.Show();
                    Application.DoEvents();
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
}
