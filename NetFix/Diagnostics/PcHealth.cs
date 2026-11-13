using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Netfix.Model;

namespace Netfix.Diagnostics;

/// <summary>
/// Finds local reasons a PC feels slow: apps hogging the processor or memory, a full disk, battery saver, or a
/// long time since the last restart. Entirely offline. (Windows has no reliable temperature reading without
/// vendor tools, so temperature is skipped.)
/// </summary>
public static class PcHealth
{
    /// <summary>Several processes grouped under the app (or Windows service) that owns them.</summary>
    public sealed record Usage(string Key, string Name, double CpuPercentOfPC, long MemoryBytes, bool Quittable, string? ServiceNote);

    /// <summary>Plain explanations for well-known Windows background work (by process name).</summary>
    public static readonly Dictionary<string, (string Name, string Note)> ServiceNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MsMpEng"] = ("Microsoft Defender", "Microsoft Defender is scanning your PC for viruses. It slows things down for a while, then stops on its own."),
        ["TiWorker"] = ("Windows Update", "Windows is installing updates in the background. It finishes on its own; a restart afterwards completes it."),
        ["TrustedInstaller"] = ("Windows Update", "Windows is installing updates in the background. It finishes on its own; a restart afterwards completes it."),
        ["wuauserv"] = ("Windows Update", "Windows is checking for updates in the background. It finishes on its own."),
        ["SearchIndexer"] = ("Windows Search", "Windows is updating its search index (the list it uses to find your files). It finishes on its own."),
        ["SearchProtocolHost"] = ("Windows Search", "Windows is updating its search index (the list it uses to find your files). It finishes on its own."),
        ["CompatTelRunner"] = ("Windows compatibility check", "Windows is checking your apps for an upgrade. It finishes on its own."),
        ["dwm"] = ("Screen drawing", "The part of Windows that draws your screen is busy. Many open windows, animations or a second monitor can cause it."),
        ["MsSense"] = ("Microsoft Defender", "Microsoft Defender for Endpoint is scanning your PC. It slows things down for a while, then stops on its own."),
    };

    /// <summary>Never offered for quitting: part of Windows itself.</summary>
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "services", "lsass", "svchost", "System", "Idle", "smss", "wininit",
        "fontdrvhost", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "TextInputHost", "ApplicationFrameHost", "SystemSettings", "Netfix", "Registry", "Memory Compression",
    };

    public static async Task<(List<Issue> Issues, List<HealthItem> Healthy)> Check(bool performance = true, bool hardware = true)
    {
        var issues = new List<Issue>();
        var healthy = new List<HealthItem>();
        void Add(Topic topic, Issue? issue, string detail)
        {
            if (issue is not null) issues.Add(issue); else healthy.Add(new HealthItem(topic, detail));
        }

        if (performance)
        {
            var apps = await SampleApps();
            var totalBusy = Math.Min(100, apps.Sum(a => a.CpuPercentOfPC));
            Add(Topic.Processor, ProcessorIssue(apps, totalBusy), $"{totalBusy:0}% busy");
            var memory = Native.Memory();
            int? free = memory is { Total: > 0 } m ? (int)(m.Free * 100 / m.Total) : null;
            Add(Topic.Memory, MemoryIssue(apps, free), free is { } f ? $"{f}% free" : "OK");
            if (Native.Power() is { BatterySaver: true })
                issues.Add(new Issue
                {
                    Id = "batterySaver", Severity = Severity.Warning, Topic = Topic.Battery,
                    Title = "Battery saver is on",
                    Explanation = "It saves battery by slowing your PC down and pausing things in the background.",
                    Fixes = { Fix.Open(Destination.BatterySettings) },
                    Steps = { "Click Open Power & battery settings.", "Next to Energy saver (or Battery saver), click Turn off now." },
                });
        }
        if (hardware)
        {
            var disk = SystemDisk();
            Add(Topic.Storage, StorageIssue(disk), disk is { } d ? $"{FormatBytes(d.Free)} free" : "OK");
            Add(Topic.Upkeep, UpkeepIssue(TimeSpan.FromMilliseconds(Environment.TickCount64)), $"Restarted {Days(Environment.TickCount64)} day(s) ago");
            if (Native.Power() is { HasBattery: true } power)
                healthy.Add(new HealthItem(Topic.Battery, power.Percent is { } p ? $"{p}% charged" + (power.OnBattery ? ", on battery" : ", plugged in") : "OK"));
        }
        return (issues, healthy);
    }

    private static int Days(long ms) => (int)(ms / 86_400_000);

    /// <summary>
    /// Samples every process's processor time twice, 1.5 s apart, and groups processes by name (Chrome, Teams… run
    /// many). Percent is of the whole PC (all cores).
    /// </summary>
    public static async Task<List<Usage>> SampleApps()
    {
        static Dictionary<int, (string Name, TimeSpan Cpu, long Memory, bool Window)> Snapshot()
        {
            var d = new Dictionary<int, (string, TimeSpan, long, bool)>();
            foreach (var p in Process.GetProcesses())
            {
                try { d[p.Id] = (p.ProcessName, p.TotalProcessorTime, p.WorkingSet64, p.MainWindowHandle != IntPtr.Zero); }
                catch (Exception) { /* system processes we can't read */ }
                finally { p.Dispose(); }
            }
            return d;
        }
        var first = await Task.Run(Snapshot);
        var sw = Stopwatch.StartNew();
        await Task.Delay(1500);
        var second = await Task.Run(Snapshot);
        var elapsed = sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount;

        return second.Where(kv => kv.Value.Name != "Idle")
            .GroupBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var cpu = g.Sum(kv => first.TryGetValue(kv.Key, out var before) ? Math.Max(0, (kv.Value.Cpu - before.Cpu).TotalMilliseconds) : 0);
                var note = ServiceNotes.GetValueOrDefault(g.Key);
                var quittable = !SystemProcesses.Contains(g.Key) && note == default && g.Any(kv => kv.Value.Window);
                return new Usage(g.Key, note.Name ?? FriendlyName(g.Key, g.First().Key), cpu * 100 / elapsed, g.Sum(kv => kv.Value.Memory), quittable, note.Note);
            }).ToList();
    }

    /// <summary>"chrome" → "Google Chrome", from the program's own description when Windows lets us read it.</summary>
    private static string FriendlyName(string processName, int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var description = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description) && description.Length <= 40) return description.Trim();
        }
        catch (Exception) { }
        return processName;
    }

    public static Issue? ProcessorIssue(IReadOnlyList<Usage> apps, double totalBusy)
    {
        var top = apps.OrderByDescending(a => a.CpuPercentOfPC).FirstOrDefault();
        if (top is null) return null;
        var share = top.CpuPercentOfPC;
        if (!(share >= 25 || (totalBusy >= 85 && share >= 10))) return null;
        var issue = new Issue
        {
            Id = $"cpu-{top.Key}", Severity = share >= 50 || totalBusy >= 85 ? Severity.Problem : Severity.Warning, Topic = Topic.Processor,
            Title = $"{top.Name} is working your PC hard",
            Explanation = $"It's using about {share:0}% of your PC's power, so everything else feels slow.",
            Technical = $"{top.Key}: {share:0}% CPU (of {Environment.ProcessorCount} logical processors); whole PC {totalBusy:0}% busy",
        };
        if (top.ServiceNote is { } note)
        {
            issue.Explanation = note;
            issue.Steps = new() { "Give it a while; it finishes on its own.", "Still busy after an hour? Restart your PC." };
        }
        else if (top.Quittable)
        {
            issue.Fixes = new() { Fix.QuitApp(top.Name, top.Key) };
            issue.Steps = new() { $"Close the tabs or windows you aren't using in {top.Name}.", $"Still slow? Close {top.Name}, then open it again." };
        }
        else
        {
            issue.Fixes = new() { Fix.Open(Destination.TaskManager) };
            issue.Steps = new() { "Give it a few minutes; it usually finishes on its own.", "Still busy after an hour? Restart your PC." };
        }
        return issue;
    }

    public static Issue? MemoryIssue(IReadOnlyList<Usage> apps, int? freePercent)
    {
        if (freePercent is not { } free || free >= 20) return null;
        var list = string.Join(", ", apps.OrderByDescending(a => a.MemoryBytes).Take(3).Select(a => $"{a.Name} ({FormatBytes(a.MemoryBytes)})"));
        var issue = new Issue
        {
            Id = "memory", Severity = free < 10 ? Severity.Problem : Severity.Warning, Topic = Topic.Memory,
            Title = "Your PC is running low on memory",
            Explanation = $"Your PC is short on memory, so it slows down. Using the most: {list}.",
            Steps = { "Close apps you aren't using.", "Close browser tabs you don't need.", "Still slow? Restart your PC." },
            Technical = $"{free}% of physical memory free",
        };
        var biggest = apps.OrderByDescending(a => a.MemoryBytes).FirstOrDefault(a => a.Quittable);
        if (biggest is not null) issue.Fixes.Add(Fix.QuitApp(biggest.Name, biggest.Key));
        issue.Fixes.Add(Fix.Open(Destination.TaskManager));
        return issue;
    }

    public static (long Free, long Total)? SystemDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var d = new DriveInfo(root);
            return (d.AvailableFreeSpace, d.TotalSize);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Warning under 10% or 15 GB free; problem under 5% or 5 GB (same limits as the Mac).</summary>
    public static Issue? StorageIssue((long Free, long Total)? disk)
    {
        if (disk is not { Total: > 0 } d) return null;
        var percent = d.Free * 100.0 / d.Total;
        var gb = d.Free / 1_000_000_000.0;
        if (percent >= 10 && gb >= 15) return null;
        return new Issue
        {
            Id = "storage", Severity = percent < 5 || gb < 5 ? Severity.Problem : Severity.Warning, Topic = Topic.Storage,
            Title = "Your PC is almost out of storage",
            Explanation = $"Only {FormatBytes(d.Free)} is free. Your PC needs free space to run smoothly and install updates.",
            Fixes = { Fix.Open(Destination.StorageSettings) },
            Steps = { "Empty the Recycle Bin.", "Click Open Storage settings.", "Click Temporary files, then Remove files.",
                      "Delete big files you don't need from Downloads.", "Still full? In Storage settings, turn on Storage Sense so Windows clears space for you." },
            Technical = $"{percent:0.0}% free ({FormatBytes(d.Free)} of {FormatBytes(d.Total)})",
        };
    }

    /// <summary>Warning after 14 days without a restart. Shutting down doesn't count on Windows (Fast startup).</summary>
    public static Issue? UpkeepIssue(TimeSpan uptime)
    {
        var days = (int)uptime.TotalDays;
        if (days < 14) return null;
        return new Issue
        {
            Id = "uptime", Severity = Severity.Warning, Topic = Topic.Upkeep,
            Title = $"Your PC hasn't restarted in {days} days",
            Explanation = "Restarting clears out stuck apps and finishes updates. It often fixes slowness by itself.",
            Steps = { "Save your work.", "Click Start, then the power button, then Restart.",
                      "Use Restart, not Shut down: on Windows, shutting down doesn't clear things out the same way." },
        };
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.#} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0} MB",
        _ => $"{bytes / 1000.0:0} KB",
    };
}
