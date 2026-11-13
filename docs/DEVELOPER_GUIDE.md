# LagWatch developer guide

This document describes the implementation currently in this repository. It is intentionally source-oriented: names below refer to the C# types and files that own the behavior.

## Product shape

LagWatch is a Windows desktop monitor for the network conditions that affect games and calls: latency, jitter, packet loss, route stability, local adapter/router health, and recurring connection problems. It is a normal-user application, not a kernel network filter, antivirus, or traffic-shaping product.

The repository contains two related implementations:

1. The shipped Windows desktop application is the C#/.NET WinForms program in the repository root, `Doctor/`, and `NetFix/`.
2. The Python telemetry prototype (`app.py`, `network_sdk.py`, `write_data_to_database.py`) is a separate experimental SDK/dashboard path. It is not the runtime used by `LagWatch.exe`.

## Runtime stack

| Layer | Implementation |
|---|---|
| Operating system | Windows 10 version 2004 / build 19041 or newer; x64 |
| Runtime | .NET 8 Windows Desktop, target `net8.0-windows10.0.19041.0` |
| UI | Windows Forms, `System.Drawing`, custom controls and layout in `Theme.cs` |
| Language | C# with `LangVersion=latest`; nullable annotations enabled, implicit usings disabled |
| Packaging | Release, `win-x64`, self-contained single-file executable; developer and production build constants |
| Windows APIs | .NET networking/process APIs, Registry, Win32 P/Invoke, Core Audio COM, WinRT device APIs, and built-in command-line tools |
| Test framework | xUnit 2.9.2, xUnit Visual Studio adapter 2.8.2, Microsoft.NET.Test.Sdk 17.11.1 |
| Python prototype | Flask, Python standard library, optional `psutil` for interface counters, SQLite |

The main project has no direct NuGet package references. Its Windows SDK and runtime dependencies come from the .NET SDK/Windows target framework. The test project references xUnit and the test SDK.

## Startup and application lifecycle

`Program.cs` is the process entry point. It handles:

- command-line modes such as normal launch, background/tray launch, self-test, and preview rendering;
- developer versus production tier behavior through `DEV_BUILD`;
- startup registration for the current Windows user;
- creation of `MainForm` and the tray icon.

`MainForm.cs` owns the main window, tray behavior, monitor controls, game detection, the tier selector in developer builds, notification settings, export, and the long-lived timers. `MainPages.cs` embeds the Doctor and tool pages inside the main window instead of opening separate top-level windows.

The normal monitoring timers are:

- one-second target ping samples for the live monitor;
- four-second running-process/game detection refresh;
- one-minute forecast refresh;
- five-minute background-watch refresh;
- thirty-second connection-health probes in `ConnectionWatch`.

The app can minimize to the notification area and continue monitoring. It does not require administrator privileges for normal monitoring.

## Feature inventory

### Monitor

The Monitor page selects an IP address or DNS target and sends ICMP echo requests using `System.Net.NetworkInformation.Ping`. `Models.cs` computes:

- current and average round-trip time;
- minimum and maximum successful latency;
- packet loss from failed samples;
- average absolute change between successful samples as the UI jitter value;
- a quality classification: `EXCELLENT`, `GOOD`, `FAIR`, `POOR`, or `CRITICAL`.

The UI stores samples in memory for the active session and renders them through `LatencyChart.cs`. ICMP is explicitly a reference signal; it is not claimed to be the game’s own UDP transport latency.

The Monitor session card also shows aggregate upload and download throughput. `NetworkUsageSampler` samples byte counters from active non-loopback, non-tunnel adapters through `NetworkInterface.GetIPv4Statistics`; it measures rates only and does not inspect packet contents.

### Game detection and target selection

`Models.cs` enumerates visible processes, filters out common Windows/system and utility processes, scores likely games, and recognizes common Steam, Xbox, Epic, Riot, GOG, EA, and Ubisoft locations. `MainForm.cs` refreshes this list every four seconds.

When a game is selected, `GameServerDiscovery` inspects established public TCP connections through IP Helper APIs (`iphlpapi.dll`). A visible non-web endpoint may become the ping target. UDP match-server discovery is deliberately not guessed.

### Forecasting

`Forecast.cs` stores successful target samples in `lagwatch-history.csv` and computes a robust time-of-day median. Forecasts require at least twelve matching observations across three different days. The UI shows:

- a usual-now line and interquartile range;
- a six-hour half-hour forecast strip;
- learning progress until enough history exists;
- a comparison of current latency to the usual value.

`ConnectionWatch.cs` separately learns recurring spotty/outage windows from five-minute health buckets. This is a rules-based time-of-week comparison, not a machine-learning model.

### Connection heads-ups

`ConnectionWatch.cs` pings the local gateway and two public references (`1.1.1.1` and `8.8.8.8`) every thirty seconds. It classifies the recent five-minute window as healthy, spotty, local outage, or provider outage. It distinguishes local problems only when gateway evidence is available; routers that never answer ICMP are treated as unknown rather than automatically faulty.

It can notify on:

- a problem in progress after two bad probes;
- recovery after two good minutes;
- a predicted recurring bad period on Plus/Pro;
- provider outage signals from IODA on Plus/Pro.

The provider check sends the detected ASN to the public Georgia Tech IODA v2 endpoint. It does not send account credentials or user traffic.

### Optimize

`OptimizationForm.cs` provides review-before-apply profiles. The implemented changes are intentionally limited and reversible:

- set the selected game process to `AboveNormal` for the current process session;
- set Windows visual-effects policy to best performance, best appearance, or default.

It does not change DNS, Winsock, TCP tuning, services, firewall policy, or traffic shaping. The native WFP/QoS backend described by the Python prototype is not implemented.

### Network Doctor / Troubleshoot

`Doctor/DoctorSession.cs` coordinates NetFix checks and fixes. `Doctor/DoctorForm.cs` renders the current result, issue cards, progress, plans, device testing, and workspace tools. `Doctor/Tiers.cs` centralizes feature and fix gates.

The Checkup can cover:

- processor and memory pressure, storage/restart/battery state;
- sound, camera, Bluetooth, and call-device conditions;
- OSI-layer network checks;
- speed testing when the network is healthy enough to interpret it.

The standalone Security page has been removed. Security-related network findings remain part of ordinary network diagnosis and Network Details where they are relevant to connection failures.

### Workspace audio controls

The Pro Workspace page lists every active Core Audio render/capture endpoint returned by `Audio.Devices`. Each row can:

- play a speaker-specific test tone or listen to a microphone-specific endpoint;
- show `DEFAULT`, `MUTED`, and `CALLS` state;
- set all three Windows default roles through `IPolicyConfig` (`Console`, `Multimedia`, and `Communications`);
- save a separate LagWatch call preference through `CallPicks`.

`Audio.cs` uses Core Audio COM interfaces (`IMMDeviceEnumerator`, endpoint volume, `IAudioClient`, render/capture clients) and the undocumented but stable `IPolicyConfig` interface for default switching. Microphone tests update the level bar while listening.

### Tools

`MainPages.cs` embeds the tools as pages:

- **Path Check** (`MultiTargetMonitor.cs`, Plus): repeatedly compares the local gateway, a public reference, and the selected target using ping, latency, loss, and quality.
- **Network Details** (Pro): displays adapters and OSI-layer reports.
- **Route Map** (`RouteMap.cs`, Pro): runs `tracert.exe -d -h 20 -w 800`, parses hop addresses/latencies/timeouts, and labels responding hops.
- **Outage Map** (`OutageMap.cs`, Pro): combines local cached history, detected provider/ASN data, IODA alerts, and provider status links.
- **Background Watch** (Pro): keeps the connection-health timer active and presents the same heads-up classification from the tray/main UI.

## NetFix diagnostics architecture

`NetFix/Model` is the plain-language model:

- `Issue.cs`: issue topics, severity, fix kinds, destinations, explanations, confirmations, and labels;
- `LayerReport.cs`: findings, statuses, issue codes, reports, and check scopes;
- `OSILayer.cs`: physical through application layer names and explanations;
- `Support.cs`: shared model helpers.

`NetFix/Diagnostics` supplies the checks and fixes:

- `DiagnosticEngine.cs`: runs layer checks and returns reports;
- `LayerChecks.cs`: Wi-Fi, adapter, ARP, IP, DNS, TCP, TLS, HTTP, proxy, hosts, firewall, and related checks;
- `NetworkInfo.cs`: adapters, gateways, routes, DNS, Wi-Fi parsing, VPN information, and hosts-file handling;
- `NetworkAdvisor.cs`: turns reports into prioritized user-facing issues and remediation steps;
- `FixRunner.cs`: executes gated fixes and returns success/failure messages;
- `PcHealth.cs`: process/resource and hardware-adjacent checks;
- `Calls.cs`: audio/camera/privacy/call-device checks and `CallPicks` persistence;
- `Audio.cs`: Core Audio enumeration, testing, volume, mute, and default switching;
- `Bluetooth.cs`: Bluetooth radio/headphone checks and fixes;
- `Probes.cs`: ICMP, TCP, DNS, HTTP, TLS, and server-time probes;
- `SpeedTest.cs`: download measurement and verdicts;
- `Shell.cs`: safe wrappers around Windows tools and elevation-confirmed scripts;
- `Fleet.cs`: local Fleet/service-related observations used by diagnostics;
- `Native.cs`: native/WinRT interop helpers.

## External and operating-system interfaces

The app uses these Windows facilities rather than third-party runtime services:

- `Ping`, TCP sockets, DNS resolution, HTTP, TLS, and certificate validation from the .NET BCL;
- `Process` and `ProcessStartInfo` for process inspection and controlled tool execution;
- Registry reads for Windows settings, app privacy, and startup-related state;
- `dwmapi.dll` and `iphlpapi.dll` through P/Invoke;
- `netsh`, `route`, `arp`, `ipconfig`, `rasdial`, `netsh advfirewall`, `wevtutil`, and PowerShell for Windows diagnostics/fixes;
- Core Audio COM for endpoint enumeration and testing;
- WinRT `Windows.Devices.Enumeration` for device-related checks;
- public references including Cloudflare/Google/Quad9 DNS, `cloudflare.com`, ASN lookup, IODA, and provider status pages.

All shell calls are centralized through `Shell.cs`, use timeouts, capture output, and report failures. Admin-required repairs are tier-gated and normally ask Windows for UAC confirmation.

## Persistence and privacy

The C# app writes local data under `%LOCALAPPDATA%\LagWatch` and the application directory where the current code explicitly does so:

- `lagwatch-history.csv`: successful latency history for forecasting;
- `network-health.csv`: five-minute connection buckets, retained for sixty days;
- outage-event history and cached provider/IODA data;
- `NetFix` settings and `call-picks.json` for preferred call devices;
- exported CSV/report files when the user requests them.

Game detection is local. Process names, paths, and window titles are not uploaded by the C# app. The app uses public network probes and sends only what the relevant external endpoint requires, such as an ASN to IODA.

## Build and verification

The supported build entry point is `build.ps1`:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
powershell -ExecutionPolicy Bypass -File .\build.ps1 -MainBuild
powershell -ExecutionPolicy Bypass -File .\build.ps1 -NoTest
```

The script locates .NET 8, generates the icon, publishes a self-contained single file, copies the output to `LagWatch-dev.exe` or `LagWatch.exe`, then launches `--self-test` unless `-NoTest` is supplied. Close the running tray executable before publishing so Windows does not lock the destination.

The built-in self-test covers target validation, tier policy, NetFix rules, offline behavior, forecast/statistics helpers, game detection, theme normalization, and quality calculations. The xUnit project covers NetFix parsers/advisors, sound/Bluetooth verdicts, forecast history, heads-up classification, and tier behavior.

Preview modes include `--render-preview` for the main window and `--render-doctor` for a Doctor page. The current Doctor preview tabs are `Checkup`, `Details`, and `Workspace`.

## Design constraints and known limits

- ICMP measurements are reference measurements, not authoritative game transport RTT.
- UDP game endpoint discovery is not implemented.
- Traffic shaping is monitor-only; no WFP/QoS enforcement exists.
- A router that ignores ping cannot be classified as broken solely from missing ICMP replies.
- Provider outage data depends on public internet access and may be cached/stale; cached results are labeled.
- The app is not antivirus, EDR, firewall management, or a complete security audit tool.
- The current C# app and Python telemetry prototype are separate paths; the Python dashboard does not drive the shipped WinForms UI.
