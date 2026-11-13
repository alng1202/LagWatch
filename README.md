# LagWatch

LagWatch is a lightweight Windows desktop app for watching the network conditions that affect online games: latency, jitter, packet loss, route stability, and the quality of the local adapter-to-router connection.

The Python engine now implements the monitoring and congestion-detection foundation: a protected rolling median baseline, RTT inflation and queue-delay estimates, robust jitter, explicitly labeled active-probe loss, passive interface throughput, hysteretic congestion states, and normalized SQLite session/event/action storage. ICMP remains a reference measurement and is never presented as authoritative game RTT.

Traffic shaping is intentionally monitor-only. `TrafficController` emits unapplied pacing requests so controller behavior can be tested without changing Windows networking. A production enforcement backend should be a separately privileged, fail-open WFP/QoS component with a kill switch and automatic cleanup.

## Automatic server reference and predictions

When game auto-detection finds a process, LagWatch now checks that process's established public TCP connections. A non-web remote endpoint can automatically become the ping target. The UI explicitly calls this an ICMP reference because it is not the game's own transport RTT. Windows does not expose a UDP connection's remote peer through the normal IP Helper tables, so UDP-heavy games such as Roblox require the future native WFP/ETW collector for reliable match-server discovery; LagWatch does not guess from launcher/CDN connections.

Successful samples train `lagwatch-history.csv` locally at most once per minute. LagWatch compares the same target and ±15-minute time window across prior days. It displays a median expected latency only after at least 12 matching observations across three different days, preventing a single session from being presented as a prediction.

**Latency forecast (Plus and Pro), shown live on the main window:**

- **Timeline:** a dashed "usual now ≈ N ms" line, with the typical range (25th–75th percentile) shaded behind the live latency line.
- **Next 6 hours:** a strip under the timeline with one bar per half hour, showing the usual latency at that time of day, its value, and its color (same thresholds as the status badge). It also names the best time coming up. Until a time of day has enough history, the strip shows a learning progress bar (days and samples).
- **Current latency card:** compares the latest reply with what's usual ("about usual", "N ms above usual", or "often drops at this time").

`Forecast.cs` (`LatencyForecaster`) reads the history file in the background, only when it has changed and at most once a minute, so the UI never re-reads it per ping. Free shows none of this.

## Connection heads-ups

`ConnectionWatch.cs` runs whenever LagWatch runs (including from the tray after Windows sign-in), whatever is selected on the Monitor page:

- **Collect (every plan):** every 30 s it pings the router and two public servers (1.1.1.1, 8.8.8.8) and classifies the last 5 minutes as healthy, spotty, down on this side of the router, or down on the provider's side. Router ICMP that's never answered is treated as "unknown", not a fault. One summary row per 5 minutes goes to `%LocalAppData%\LagWatch\network-health.csv` (kept 60 days), tagged with the provider name and ASN so an opt-in shared provider service can use the same data later.
- **Predict (Plus/Pro):** `RiskForecaster` flags coming half hours that were spotty or down on most comparable earlier days: the same weekday once there are 3+ of them, otherwise weekdays or weekends (3+ observed days, 2+ bad, ≥40%). It's a time-of-week pattern, not machine learning, and the wording says how many days it's based on. The Monitor page's forecast strip marks those half hours and names the next one; the Outage Map shows the next 12 hours.
- **Warn:** *during* (every plan) after two bad checks in a row, naming the likely side (Wi‑Fi/router vs provider), with "save your work" and, for problems on this side, a click through to Troubleshoot; *after* when it's been good for 2 minutes, with how long it lasted; *before* (Plus/Pro) 30 minutes ahead of a usually-spotty time; and *provider outage* (Plus/Pro) when IODA reports outage signals for the provider's ASN in the last 2 hours (checked every 15 minutes, at most one alert per 3 hours). Nothing is changed by itself; Options → "Notify me before, during and after spotty internet" turns the notifications off (the history is still kept).

## Network Doctor (merged from NetFix)

NetFix for Windows is merged into LagWatch as **Network Doctor** (sidebar → TROUBLESHOOT, or the tray menu). It finds out why the internet or the PC isn't working properly and fixes it with one click, after the user confirms: PC health (processor/memory hogs, storage, restarts, battery), sound/camera/Bluetooth, then the network bottom-up by OSI layer. Layers 1–4 are checked offline, against this PC and the router. Layers 5–7 need the internet and are skipped, not failed, when offline. The lowest failing layer is reported first.

- `NetFix/Model` and `NetFix/Diagnostics` are NetFix's engine, copied **unchanged** from `Netfix-alan-mac-build/Windows/Netfix`. When a check or fix changes in NetFix, copy the file over. The only shim is `Doctor/NetFixHost.cs`, which stores NetFix's two small files under `%LOCALAPPDATA%\LagWatch\NetFix`.
- `Doctor/` holds the integration: `Tiers.cs` (plans and every gate), `DoctorSession.cs` (NetFix's AppState ported to WinForms), and `DoctorForm.cs` (Checkup, Network details, Workspace).
- Not included: NetFix's WPF views and its macOS build. LagWatch's own WinForms UI replaces them.

## One window

Everything opens inside the main window. The header (title, live "N ms · QUALITY" badge, plan) stays on every page, and the sidebar switches the page below it:

| Page | Plan | What it is |
|---|---|---|
| MONITOR | Free | Live latency, jitter, loss, upload/download usage, timeline (+ forecast on Plus), session, event log |
| TROUBLESHOOT | Free | Network Doctor's checkup and fixes |
| WORKSPACE | Pro | Call devices, PC performance, visual effects |
| TOOLS | Plus | Path check (Plus); Network details, Route map, Outage map and the Background watch switch (Pro) |
| EXPORT CSV | Plus | Saves this session (an action, not a page) |
| SETTINGS | Free | The only popup. Picking a theme previews it live on the whole app, and Cancel puts the old one back. |

Optimize (review-then-apply) and fix confirmations are also short popups. The tool windows (`MultiTargetMonitor.cs`, `RouteMap.cs`, `OutageMap.cs`) are hosted as borderless pages (`MainPages.cs`). They implement `IEmbeddedTool.StartAsync` and stop when you leave them. Route map traces to 1.1.1.1 when the ping target is your router, since that route is always one hop.

LagWatch's original "Network Troubleshooter" and the separate adapter-diagnostics box were removed: Troubleshoot covers the same checks and repairs, and Network Details lists every adapter.
## Plans

Each plan includes everything below it. Problems are always detected and explained on every plan. A higher plan unlocks the one-click repair and the deeper tooling.

**The interface grows with the plan.** Features above the user's plan are hidden, not shown locked. Free sees three sidebar entries (MONITOR, TROUBLESHOOT, SETTINGS) and a Troubleshoot page with one button. Plus adds EXPORT CSV and TOOLS (path check), the latency forecast, and Quick scan and Fix all on Troubleshoot. Pro adds WORKSPACE and the rest of TOOLS (network details, route map, ISP outage map, background watch). When a fix needs a higher plan and nothing else can fix it, one short line says which plan can fix it. The app font is Bahnschrift, falling back to Segoe UI.

| | Free (end user) | Plus (network repair) | Pro (full metrics + workspace) |
|---|---|---|---|
| LagWatch | Live latency/jitter/loss monitor, graph, game auto-detection, Optimize | Path check, live latency forecast (usual-now line + next 6 hours), session CSV export | Tools page: network details, route map, ISP outage map, background watch; adaptive-control preview |
| Network Doctor | Check My Device (full checkup + speed test) | Custom scans (Performance, Network, Hardware, Sound), Fix all | **Network details** (every OSI-layer finding, adapters, bottom-up verdict, technical details on issues), **Background watch** (rechecks every 5 min and on connection change, tray alert), network report for your internet provider |
| One-click fixes | Safe, no-admin fixes: open settings, quit a hog, Wi‑Fi on/restart, refresh DNS cache, unmute/turn up mic, pick speaker/PC mic, Bluetooth on | Every admin (UAC) repair: rejoin network, reset adapter, automatic IP/DNS, re-enable adapter, turn off VPN, clear VPN routes, remove hosts-file block, restart sound/Bluetooth | Switch back to your call devices |
| Workspace | — | — | Workspace optimization: test and pick call speaker/mic, PC performance check, visual effects & game priority |

The gates are in `Doctor/Tiers.cs` (`SubscriptionPolicy.RequiredTier(Feature)` and `RequiredTier(Fix)`). Self-tests ensure that no admin-level repair lands in Free. There is no billing or account yet. The dev build has a FREE / PLUS / PRO selector at the top right. The main build starts on Free, and a licensing step still needs to set `subscriptionTier`.

The preview never claims to apply shaping because the native WFP/QoS backend is not implemented yet.

## Build

For the complete walkthrough script, presentation test cases, implementation reference, and manual equivalent of LagWatch's automated work, see [`docs/FEATURE_WALKTHROUGH_SCRIPT.md`](docs/FEATURE_WALKTHROUGH_SCRIPT.md), [`docs/PRESENTATION_TEST_CASES.md`](docs/PRESENTATION_TEST_CASES.md), [`docs/DEVELOPER_GUIDE.md`](docs/DEVELOPER_GUIDE.md), and [`docs/MANUAL_RUNBOOK.md`](docs/MANUAL_RUNBOOK.md).

LagWatch now needs the **.NET 8 SDK** to build, because NetFix's engine uses C# 12 and WinRT. The old `csc.exe` from .NET Framework can't compile it. `build.ps1` finds the SDK in `%USERPROFILE%\.dotnet` or `Program Files`.

- `build.ps1` builds `LagWatch-dev.exe`, with the developer tier selector, as a self-contained single file of about 75 MB that needs nothing installed. Then it runs the built-in self-tests.
- `build.ps1 -MainBuild` produces `LagWatch.exe` without the selector. Feature work should be verified in the dev executable first and promoted only when explicitly authorized.
- `-Small` builds a ~1 MB exe that needs the .NET 8 Desktop Runtime. `-NoTest` skips the self-tests.
- `dotnet test Tests\LagWatch.Tests.csproj` runs NetFix's engine tests (parsers, advisor, offline rule, sound/Bluetooth verdicts) plus the tier tests.
- UI check without screen recording: `LagWatch-dev.exe --render-doctor out.png --tier Plus --tab Details` (sample results; set `LAGWATCH_DOCTOR_LIVE=1` for a real read-only network scan). `--render-preview out.png --tier Pro` renders the main window.

All external data used by the outage center is free and requires no API token. ISP/ASN identification uses the public no-auth ASN lookup, and ASN-level outage alerts use Georgia Tech's public IODA v2 API. LagWatch does not contain paid API dependencies, token fields, or credential setup. Planned ISP maintenance remains unavailable unless an individual ISP publishes a free no-auth feed.

ASN organization names are normalized to customer-facing provider names (for example, Comcast rather than `COMCAST-7922`). `ISP network / Internet edge` means the path after the home gateway but before or within the wider Internet; it helps distinguish a healthy LAN from an ISP/upstream failure. For providers with an official address/account-based status service, Pro shows an `OPEN ISP STATUS CENTER` button. Comcast opens the official Xfinity outage map, where local and planned work can be matched to the subscriber's service address or signed-in account.

The outage center remains useful offline. Adapter state, gateway reachability, two independent public probes, and DNS resolution are classified locally. Non-healthy classifications are retained for 30 days in `%LOCALAPPDATA%\LagWatch\outage-events.csv`; repeated events within the same time window generate an offline recurring-risk warning. The last detected ISP and last successful IODA result are cached under `%LOCALAPPDATA%\LagWatch` and are visibly labeled cached when live services cannot be reached. Cached external data is never presented as a current confirmation.

Each tier has a visible identity in the header and left menu. Prediction history is pruned daily to a rolling 30-day window. The current prediction method is a robust time-of-day median rather than a trained neural network; calling it machine learning would overstate the implementation.

## Run it

Double-click **LagWatch.exe**. It runs as your normal Windows user and does not need an installer. On first launch it registers itself for the current user's Windows sign-in startup list; startup launches use `--background` and stay in the notification area rather than opening over the desktop.

1. LagWatch discovers the active local gateway and starts pinging it automatically. This isolates the PC-to-router/LAN path; a successful gateway ping alone does not confirm wider Internet access.
2. To use a different reference, pause monitoring and enter a public target such as `1.1.1.1`, or a game server address when you know it, then select **START** or press **F5**.
3. Watch the current latency, rolling average, jitter, loss, and two-minute timeline.
4. On Pro, open **TOOLS › NETWORK DETAILS** to see every adapter (address, router, DNS, link speed) and every layer check.

Minimizing the main window keeps it visible on the taskbar and keeps monitoring active. Closing the window hides LagWatch to the notification area by default; turn off “Keep LagWatch running when I close the window” in Settings if closing should exit the app and remove the tray icon. Use the LagWatch tray icon to reopen the window, pause or resume monitoring, or exit the app.

## Automatic game detection

LagWatch scans visible running applications every four seconds while **Auto-detect running games** is enabled. It recognizes a curated set of popular game executables and games launched from common Steam, Xbox, Epic, Riot, GOG, EA, and Ubisoft install locations. A detected game is selected automatically for the optional priority boost.

Detection happens entirely on the PC: process names, window titles, and executable paths are not uploaded or saved. Disable the checkbox to select a process manually. As with Discord's activity detection, unusual launchers or renamed executables may need manual selection.

## Optimize

**OPTIMIZE** opens a checklist (profiles: Performance, Balanced, Visual quality, Custom) with two limited, reversible items:

- Set the selected running game's process priority to **Above normal** for that game session.
- Set Windows visual effects (animations, fades, shadows, smooth scrolling) to Best performance, Best appearance, or Windows default. These are the same switches as System › Advanced › Performance Options, which is updated to match. ClearType text smoothing stays on.

Refreshing the DNS cache is **not** part of Optimize, because it doesn't change ping, jitter or loss. It's a fix for stale lookups, so it lives in Troubleshoot ("Refresh website lookups"), offered only when lookups are actually failing.

Apart from Windows' own visual-effects setting, it deliberately does not edit the registry, reset Winsock, replace DNS providers, disable Windows services, or change TCP/adapter settings. Those common “gaming optimizer” tweaks can break connectivity and usually do not improve the physical route to a game server.

If Windows protects the process or the game uses anti-cheat restrictions, LagWatch will report that it could not change priority and continue normally.

## Export and privacy

**EXPORT CSV** asks where to save the current session (defaulting to Documents). LagWatch's network traffic is: ICMP pings to the target you choose, to your router and to 1.1.1.1 / 8.8.8.8 (connection heads-ups); a provider lookup at asn.ipinfo.app at most every 6 hours; and, on Plus/Pro, IODA outage queries that send only your provider's ASN. Network Doctor's checks and speed test also reach the sites they test. All history stays on this PC. It has no account, analytics, ads, updater, or Windows service; background operation is handled by the signed-in user's notification-area app.

## Files

- `LagWatch.exe` — ready-to-run application
- `LagWatch.ico` — application icon
- `*.cs` — C# source code
- `app.manifest` — Windows runtime metadata
- `build.ps1` — repeatable local build script
- `network_sdk.py` — adaptive telemetry, baseline, congestion detector, and safe controller policy
- `write_data_to_database.py` — SQLite sessions, samples, flows, congestion events, and actions
- `test_network_sdk.py` — deterministic baseline, jitter, loss, hysteresis, safety, and database tests

To rebuild, right-click `build.ps1`, choose **Run with PowerShell**, or run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

LagWatch targets .NET 8 on Windows 10 2004+ / Windows 11 x64. The default build is self-contained, so nothing extra has to be installed.
