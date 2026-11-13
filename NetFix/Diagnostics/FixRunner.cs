using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Netfix.Model;
using global::Windows.Devices.Radios;

namespace Netfix.Diagnostics;

/// <summary>
/// Runs one-click fixes. Only ever called after the user clicks (and confirms, where <see cref="Fix.Confirmation"/> says so).
/// Fixes that need an administrator ask Windows for one (UAC); saying no is not an error.
/// </summary>
public static class FixRunner
{
    public sealed record Outcome(bool Success, string Message);

    private static Outcome Ok(string m) => new(true, m);
    private static Outcome Failed(string m) => new(false, m);
    private static readonly Outcome Cancelled = new(false, "Cancelled. Nothing was changed.");

    public static async Task<Outcome> Run(Fix fix)
    {
        try { return await RunUnchecked(fix); }
        catch (Exception e) { return Failed($"That didn't work: {e.Message}"); }
    }

    private static async Task<Outcome> RunUnchecked(Fix fix)
    {
        switch (fix.Kind)
        {
            case FixKind.QuitApp:
            {
                var processes = Process.GetProcessesByName(fix.Arg);
                var asked = 0;
                // Asks each window to close, like clicking its X, so the app can offer to save work.
                foreach (var p in processes.Where(p => p.MainWindowHandle != IntPtr.Zero))
                {
                    if (p.CloseMainWindow()) asked++;
                }
                foreach (var p in processes) p.Dispose();
                return asked > 0 ? Ok($"Asked {fix.Name} to close.") : Failed($"{fix.Name} has no window to close. Close it yourself, or use Task Manager.");
            }
            case FixKind.TurnWiFiOn:
                return await TurnWiFiOn();
            case FixKind.RestartWiFi:
                return await RestartWiFi();
            case FixKind.FlushDNS:
            {
                var r = await Shell.Run("ipconfig.exe", new[] { "/flushdns" });
                return r.Ok ? Ok("Website lookups refreshed.") : Failed("Couldn't refresh website lookups.");
            }
            case FixKind.DisconnectVPN:
            {
                var r = await Shell.Run("rasdial.exe", new[] { fix.Arg, "/disconnect" }, 20);
                return r.Ok ? Ok($"{fix.Name} is off.") : Failed($"Couldn't turn off {fix.Name}. Open the VPN app and click Disconnect.");
            }
            case FixKind.RenewAddress:
                return await Elevated($"ipconfig /release {Shell.PSQuote(fix.Arg)}\r\nipconfig /renew {Shell.PSQuote(fix.Arg)}",
                                      "Rejoined the network.");
            case FixKind.ResetNetworkPort:
                return await Elevated($"Restart-NetAdapter -Name {Shell.PSQuote(fix.Arg)} -Confirm:$false", "The cable connection was reset.");
            case FixKind.EnableAdapter:
                return await Elevated($"Enable-NetAdapter -Name {Shell.PSQuote(fix.Arg)} -Confirm:$false", $"{fix.Arg} is back on.");
            case FixKind.UseAutomaticAddress:
            {
                var old = (await Shell.Run("netsh.exe", new[] { "interface", "ipv4", "show", "config", $"name={fix.Arg}" })).Text.Trim();
                var outcome = await Elevated(
                    $"netsh interface ipv4 set address name={Shell.PSQuote(fix.Arg)} source=dhcp\r\n" +
                    $"netsh interface ipv4 set dnsservers name={Shell.PSQuote(fix.Arg)} source=dhcp\r\nipconfig /renew {Shell.PSQuote(fix.Arg)}",
                    "Your network settings are automatic now.");
                return outcome.Success ? Ok($"{outcome.Message}\n\nThe old settings, in case you need them:\n{old}") : outcome;
            }
            case FixKind.UseAutomaticDNS:
            {
                var old = string.Join(", ", await SystemChecks.ManualDnsServers(fix.Arg));
                var outcome = await Elevated(
                    $"netsh interface ipv4 set dnsservers name={Shell.PSQuote(fix.Arg)} source=dhcp\r\nipconfig /flushdns",
                    "Website lookups use your router now.");
                return outcome.Success && old.Length > 0 ? Ok($"{outcome.Message} The old setting was: {old}.") : outcome;
            }
            case FixKind.UnblockSites:
                return await Elevated(UnblockScript(fix.ArgList) + "\r\nipconfig /flushdns", "The block is turned off.");
            case FixKind.RemoveLeftoverRoutes:
                return await Elevated(string.Join("\r\n", fix.ArgList.Select(d => $"route delete {d} mask 128.0.0.0")), "Old VPN settings cleared.");
            case FixKind.UnmuteSpeakers:
                return Audio.Default(false) is { } speakers && Audio.TurnUp(speakers.Id, 0.5)
                    ? Ok($"Sound is on for {speakers.Name}.")
                    : Failed("Couldn't turn the sound on. Press the volume-up key, or use the sound icon next to the clock.");
            case FixKind.TurnUpMicrophone:
                return Audio.Default(true) is { } current && Audio.TurnUp(current.Id, 0.75)
                    ? Ok($"{current.Name} is turned up. Check you aren't muted in the call app too.")
                    : Failed("Couldn't turn the microphone up. Click Open Sound settings, pick your microphone, and drag its volume to the right.");
            case FixKind.UseSpeaker:
                return Audio.MakeDefault(fix.Arg)
                    ? Ok($"Sound now plays through {fix.Name}.")
                    : Failed($"Couldn't switch to {fix.Name}. Click the sound icon next to the clock, then the arrow, and pick it.");
            case FixKind.UsePCMicrophone:
            {
                if (Audio.Devices(true).FirstOrDefault(d => d.IsBuiltIn) is not { } mic || !Audio.MakeDefault(mic.Id))
                    return Failed("Couldn't switch. Open Sound settings and pick your PC's microphone under Input.");
                // Keep it that way: "switch back to my picks" would otherwise put the headphones' mic back.
                var picks = CallPicks.Load();
                picks.MicrophoneId = mic.Id;
                picks.MicrophoneName = mic.Name;
                picks.Save();
                return Ok($"Now using {mic.Name}. Your headphones keep their full sound. In a call app, pick it if it asks.");
            }
            case FixKind.UseCallPicks:
            {
                var switched = CallPicks.Load().Apply();
                return switched.Count > 0
                    ? Ok($"Switched to {string.Join(" and ", switched.Distinct())}.")
                    : Failed("Couldn't switch. Check it's plugged in, then pick it in Sound settings.");
            }
            case FixKind.RestartSound:
                return await Elevated("Restart-Service -Name Audiosrv -Force\r\nStart-Service -Name AudioEndpointBuilder", "Sound restarted.");
            case FixKind.TurnBluetoothOn:
                return await Bluetooth.SetPower(true)
                    ? Ok("Bluetooth is on. Your devices connect in a few seconds.")
                    : Failed("Windows didn't let Netfix turn Bluetooth on. Click Open Bluetooth settings and turn it on there.");
            case FixKind.RestartBluetooth:
                if (await Bluetooth.SetPower(false))
                {
                    await Task.Delay(2000);
                    if (await Bluetooth.SetPower(true)) return Ok("Restarted Bluetooth. Your devices reconnect in a few seconds.");
                }
                return Failed("Couldn't restart Bluetooth. Open Bluetooth settings, then turn Bluetooth off and on.");
            case FixKind.Open:
                Open(fix);
                return Ok("");
            default:
                return Failed("Netfix can't do that on Windows.");
        }
    }

    private static async Task<Outcome> Elevated(string script, string success)
    {
        var (ran, exit, output) = await Shell.RunElevated(script);
        if (!ran) return Cancelled;
        return exit == 0 ? Ok(success) : Failed("Windows couldn't make that change." + (output.Trim().Length > 0 ? $" ({output.Trim().Split('\n').Last().Trim()})" : ""));
    }

    /// <summary>
    /// A script that comments out hosts-file lines blocking these names, marked "Turned off by Netfix" so they can be
    /// put back. Only lines that point a name at a blocking address are touched.
    /// </summary>
    public static string UnblockScript(System.Collections.Generic.IEnumerable<string> names)
    {
        var list = string.Join(",", names.Select(Shell.PSQuote));
        return "$path = Join-Path $env:SystemRoot 'System32\\drivers\\etc\\hosts'\r\n" +
               $"$names = @({list})\r\n" +
               "$lines = Get-Content -LiteralPath $path\r\n" +
               "$out = foreach ($l in $lines) {\r\n" +
               "  $f = ($l -split '#')[0].Trim() -split '\\s+'\r\n" +
               "  $blocks = $f.Count -ge 2 -and ($f[0] -eq '0.0.0.0' -or $f[0] -like '127.*' -or $f[0] -eq '::' -or $f[0] -eq '::1')\r\n" +
               "  if ($blocks -and ($f[1..($f.Count-1)] | Where-Object { $names -contains $_.ToLower() })) { \"# Turned off by Netfix: $l\" } else { $l }\r\n" +
               "}\r\n" +
               "Set-Content -LiteralPath $path -Value $out -Encoding ASCII\r\n" +
               "exit 0";
    }

    public static void Open(Fix fix)
    {
        switch (fix.Destination)
        {
            case Destination.WifiSettings: Shell.Open("ms-settings:network-wifi"); break;
            case Destination.NetworkSettings: Shell.Open("ms-settings:network-status"); break;
            case Destination.ProxySettings: Shell.Open("ms-settings:network-proxy"); break;
            case Destination.StorageSettings: Shell.Open("ms-settings:storagesense"); break;
            case Destination.DateTimeSettings: Shell.Open("ms-settings:dateandtime"); break;
            case Destination.BatterySettings: Shell.Open("ms-settings:batterysaver"); break;
            case Destination.TaskManager: Shell.Open("taskmgr.exe"); break;
            case Destination.SignInPage: Shell.Open("http://www.msftconnecttest.com/redirect"); break;
            case Destination.SoundSettings: Shell.Open("ms-settings:sound"); break;
            case Destination.MicrophonePrivacy: Shell.Open("ms-settings:privacy-microphone"); break;
            case Destination.CameraPrivacy: Shell.Open("ms-settings:privacy-webcam"); break;
            case Destination.BluetoothSettings: Shell.Open("ms-settings:bluetooth"); break;
            case Destination.Fleet:
                if (Fleet.MyDevicePage() is { } page) Shell.Open(page);
                break;
        }
    }

    /// <summary>Turns the Wi‑Fi radio on with Windows' radio API; falls back to opening Wi‑Fi settings.</summary>
    private static async Task<Outcome> TurnWiFiOn()
    {
        try
        {
            if (await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
            {
                var radios = await Radio.GetRadiosAsync();
                var wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                if (wifi is not null && await wifi.SetStateAsync(RadioState.On) == RadioAccessStatus.Allowed)
                    return Ok("Wi‑Fi is on. Pick your network if it doesn't connect by itself.");
            }
        }
        catch (Exception) { }
        Shell.Open("ms-settings:network-wifi");
        return Failed("Windows didn't let Netfix turn Wi‑Fi on. Turn on Wi‑Fi in the settings that just opened.");
    }

    /// <summary>Disconnects and reconnects the current Wi‑Fi network (no administrator needed).</summary>
    private static async Task<Outcome> RestartWiFi()
    {
        var wlan = await NetworkInfo.Wlan();
        var wifi = wlan.Interfaces.FirstOrDefault(w => w.Connected) ?? wlan.Interfaces.FirstOrDefault();
        if (wifi is null) return Failed("This PC has no Wi‑Fi.");
        var profile = wifi.Profile ?? wifi.Ssid;
        await Shell.Run("netsh.exe", new[] { "wlan", "disconnect", $"interface={wifi.Name}" });
        await Task.Delay(2000);
        if (profile is null) return Failed("Wi‑Fi disconnected. Pick your network from the Wi‑Fi icon to reconnect.");
        var r = await Shell.Run("netsh.exe", new[] { "wlan", "connect", $"name={profile}", $"interface={wifi.Name}" }, 15);
        return r.Ok ? Ok($"Wi‑Fi reconnected to {profile}.") : Failed("Wi‑Fi disconnected but didn't reconnect. Pick your network from the Wi‑Fi icon.");
    }
}
