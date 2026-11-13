using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using Netfix.Model;

namespace Netfix.Diagnostics;

/// <summary>Fleet (device management): its server, read from the Fleet (Orbit) service, and whether it answers.</summary>
public static class Fleet
{
    /// <summary>The server after <c>--fleet-url</c> in the Orbit service's command line. Null when Fleet isn't installed.</summary>
    public static Uri? Server()
    {
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return null;
            foreach (var name in services.GetSubKeyNames())
            {
                using var key = services.OpenSubKey(name);
                if (key?.GetValue("ImagePath") is string path && path.Contains("orbit", StringComparison.OrdinalIgnoreCase)
                    && ParseServer(path) is { } server) return server;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    public static Uri? ParseServer(string commandLine)
    {
        var m = Regex.Match(commandLine, @"--fleet-url[= ]""?(https?://[^\s""]+)", RegexOptions.IgnoreCase);
        return m.Success && Uri.TryCreate(m.Groups[1].Value.TrimEnd('/'), UriKind.Absolute, out var u) ? u : null;
    }

    /// <summary>Fleet's "My device" page: the server plus this PC's device token, when Orbit has saved it.</summary>
    public static string? MyDevicePage()
    {
        if (Server() is not { } server) return null;
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Orbit", "identifier");
        try
        {
            var token = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
            return token.Length > 0 ? $"{server.ToString().TrimEnd('/')}/device/{token}" : server.ToString();
        }
        catch (Exception) { return server.ToString(); }
    }

    /// <summary>Loads the server's health page; null when Fleet isn't installed.</summary>
    public static async Task<Finding?> Check()
    {
        if (Server() is not { } server) return null;
        var url = server.ToString().TrimEnd('/') + "/healthz";
        var r = await HttpProbe.Fetch(url, timeoutSeconds: 8);
        if (r.StatusCode is { } code && code < 500)
            return new Finding("Fleet", CheckStatus.Pass, $"{server.Host} answered ({code}) in {r.Ms:0} ms");
        return new Finding("Fleet", CheckStatus.Fail, $"{server.Host} didn't answer: {r.Error ?? $"HTTP {r.StatusCode}"}",
            advice: "Fleet can't reach its server. Turn off any VPN and reload Fleet's My device page.",
            code: IssueCode.FleetUnreachable, fixes: new[] { Fix.Open(Destination.Fleet) });
    }
}
