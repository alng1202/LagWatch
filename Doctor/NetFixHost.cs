using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Netfix.Model;

// The NetFix engine (NetFix/Model, NetFix/Diagnostics) is copied unchanged from Netfix for Windows so fixes there can
// be synced here file-for-file. The only thing it needs from Netfix's WPF app layer is where to save its two small
// files (call-picks.json, speed-history.json). LagWatch keeps them with its own data instead of %APPDATA%\Netfix.
namespace Netfix.App
{
    public static class Preferences
    {
        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LagWatch", "NetFix");
    }
}

namespace LagWatch.Doctor
{
    /// <summary>A text report of the last Network Doctor scan plus the live LagWatch session, to share with your internet provider.</summary>
    internal static class NetworkReport
    {
        public static string Text(CheckupResult result, IReadOnlyList<LayerReport> reports, string lagWatchSummary)
        {
            var sb = new StringBuilder();
            sb.AppendLine("LagWatch Network Doctor report");
            sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"PC: {Environment.MachineName} · {Environment.OSVersion.VersionString}");
            if (!string.IsNullOrWhiteSpace(lagWatchSummary))
            {
                sb.AppendLine();
                sb.AppendLine("Live latency monitor:");
                sb.AppendLine("  " + lagWatchSummary);
            }
            sb.AppendLine();
            if (result is null) sb.AppendLine("No scan has been run yet.");
            else
            {
                sb.AppendLine($"Scan: {result.Scope.Title()} at {result.Date:HH:mm} — {result.Headline}");
                foreach (var issue in result.Issues)
                {
                    sb.AppendLine();
                    sb.AppendLine($"[{(issue.Severity == Severity.Problem ? "Problem" : "Warning")}] {issue.Title}");
                    sb.AppendLine($"  {issue.Explanation}");
                    if (issue.Technical is { } t) sb.AppendLine($"  Details: {t}");
                }
                if (result.Healthy.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Looks good:");
                    foreach (var h in result.Healthy) sb.AppendLine($"  {h.Topic.Name()}: {h.Detail}");
                }
                if (result.Blocked.Count > 0)
                    sb.AppendLine($"Not checked (fix the problem above first): {string.Join(", ", result.Blocked.Select(b => b.Name()))}");
            }
            if (reports.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Network details, layer by layer:");
                foreach (var r in reports.OrderBy(r => r.Layer))
                {
                    sb.AppendLine($"{r.Layer.Title()} — {r.Status}");
                    foreach (var f in r.Findings) sb.AppendLine($"  [{f.Status}] {f.Title}: {f.Detail}");
                }
            }
            return sb.ToString();
        }

        /// <summary>Saves the report on the Desktop and returns its path.</summary>
        public static string Save(CheckupResult result, IReadOnlyList<LayerReport> reports, string lagWatchSummary)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(desktop, $"LagWatch report {DateTime.Now:yyyy-MM-dd HH.mm}.txt");
            File.WriteAllText(path, Text(result, reports, lagWatchSummary), new UTF8Encoding(true));
            return path;
        }
    }
}
