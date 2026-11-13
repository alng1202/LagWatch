using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Netfix.Diagnostics;

/// <summary>Runs built-in Windows tools (netsh, ipconfig, route, wevtutil…) and returns their output.</summary>
public static class Shell
{
    public sealed record Result(int ExitCode, string Text, bool TimedOut)
    {
        public bool Ok => ExitCode == 0 && !TimedOut;
    }

    static Shell() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Console tools write in the OEM code page, not UTF‑8.</summary>
    private static Encoding ConsoleEncoding
    {
        get
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.UTF8; }
        }
    }

    public static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static async Task<Result> Run(string exe, string[] args, double timeoutSeconds = 10)
    {
        var psi = new ProcessStartInfo(exe.Contains('\\') ? exe : SystemTool(exe))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new Result(-1, "", false);
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return new Result(-1, "", true);
            }
            var text = await output + await error;
            return new Result(p.ExitCode, text, false);
        }
        catch (Win32Exception e) { return new Result(-1, e.Message, false); }
    }

    /// <summary>Runs a PowerShell script as administrator (Windows shows its UAC prompt). False when the user said no.</summary>
    public static async Task<(bool Ran, int ExitCode, string Output)> RunElevated(string script, double timeoutSeconds = 120)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Netfix");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString("N");
        var file = Path.Combine(dir, $"fix-{id}.ps1");
        var log = Path.Combine(dir, $"fix-{id}.log");
        // The elevated window is hidden, so its output goes to a log Netfix reads back.
        await File.WriteAllTextAsync(file,
            "$ErrorActionPreference = 'Continue'\r\n& {\r\n" + script + "\r\n} *> '" + log.Replace("'", "''") + "'\r\nexit $LASTEXITCODE\r\n",
            new UTF8Encoding(true));
        var psi = new ProcessStartInfo(SystemTool(@"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"",
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, -1, "");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
            var output = File.Exists(log) ? await File.ReadAllTextAsync(log) : "";
            return (true, p.HasExited ? p.ExitCode : -1, output);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // the user clicked No
        {
            return (false, -1, "");
        }
        finally
        {
            try { File.Delete(file); File.Delete(log); } catch { }
        }
    }

    /// <summary>Opens a URL, a settings page (ms-settings:) or an app the way double-clicking would.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Quotes a value for a single-quoted PowerShell string.</summary>
    public static string PSQuote(string s) => "'" + s.Replace("'", "''") + "'";
}
