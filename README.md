# LagWatch

LagWatch is a lightweight Windows desktop app for watching the network conditions that affect online games: latency, jitter, packet loss, route stability, and the quality of the local adapter-to-router connection.

## Run it

Double-click **LagWatch.exe**. It runs as your normal Windows user and does not need an installer.

1. Enter a ping target. `1.1.1.1` is a useful internet baseline; a game server address is more representative when you know it.
2. Select **START** or press **F5**.
3. Watch the current latency, rolling average, jitter, loss, and two-minute timeline.
4. Select **DIAGNOSE** to inspect the active adapter, gateway, DNS servers, link speed, and target response.

## Automatic game detection

LagWatch scans visible running applications every four seconds while **Auto-detect running games** is enabled. It recognizes a curated set of popular game executables and games launched from common Steam, Xbox, Epic, Riot, GOG, EA, and Ubisoft install locations. A detected game is selected automatically for the optional priority boost.

Detection happens entirely on the PC: process names, window titles, and executable paths are not uploaded or saved. Disable the checkbox to select a process manually. As with Discord's activity detection, unusual launchers or renamed executables may need manual selection.

## Safe Optimize

**SAFE OPTIMIZE** does two limited, reversible things:

- Flushes the Windows DNS resolver cache, which can clear stale name-resolution entries.
- Sets the selected running game's process priority to **Above normal** for that game session.

It deliberately does not edit the registry, reset Winsock, replace DNS providers, disable Windows services, or change TCP/adapter settings. Those common “gaming optimizer” tweaks can break connectivity and usually do not improve the physical route to a game server.

If Windows protects the process or the game uses anti-cheat restrictions, LagWatch will report that it could not change priority and continue normally.

## Export and privacy

**EXPORT CSV** saves the current session beside `LagWatch.exe`. LagWatch sends only standard ICMP ping requests to the target you choose. It has no account, analytics, ads, updater, or background service.

## Files

- `LagWatch.exe` — ready-to-run application
- `LagWatch.ico` — application icon
- `*.cs` — C# source code
- `app.manifest` — Windows runtime metadata
- `build.ps1` — repeatable local build script

To rebuild, right-click `build.ps1`, choose **Run with PowerShell**, or run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

LagWatch targets the Windows .NET Framework desktop runtime included with current Windows 10 and Windows 11 systems.
