# LagWatch

LagWatch is a lightweight Windows desktop app for watching the network conditions that affect online games: latency, jitter, packet loss, route stability, and the quality of the local adapter-to-router connection.

The Python engine now implements the monitoring and congestion-detection foundation: a protected rolling median baseline, RTT inflation and queue-delay estimates, robust jitter, explicitly labeled active-probe loss, passive interface throughput, hysteretic congestion states, and normalized SQLite session/event/action storage. ICMP remains a reference measurement and is never presented as authoritative game RTT.

Traffic shaping is intentionally monitor-only. `TrafficController` emits unapplied pacing requests so controller behavior can be tested without changing Windows networking. A production enforcement backend should be a separately privileged, fail-open WFP/QoS component with a kill switch and automatic cleanup.

## Automatic server reference and predictions

When game auto-detection finds a process, LagWatch now checks that process's established public TCP connections. A non-web remote endpoint can automatically become the ping target. The UI explicitly calls this an ICMP reference because it is not the game's own transport RTT. Windows does not expose a UDP connection's remote peer through the normal IP Helper tables, so UDP-heavy games such as Roblox require the future native WFP/ETW collector for reliable match-server discovery; LagWatch does not guess from launcher/CDN connections.

Successful samples train `lagwatch-history.csv` locally at most once per minute. LagWatch compares the same target and ±15-minute time window across prior days. It displays a median expected latency only after at least 12 matching observations across three different days, preventing a single session from being presented as a prediction.

## Subscription direction

A sensible product boundary is to keep monitoring, the graph, safe optimization, automatic game detection, and basic endpoint selection in Free. Historical predictions and deeper session comparisons can form a paid analytics tier, while validated adaptive background-traffic control can form a higher tier once the native controller is proven. Network safety, kill switches, exports of a user's own measurements, and truthful measurement labels should not be paywalled.

The developer build includes a `TIER` toolbar button that cycles through all plans without billing or an account. Free enables monitoring, game detection, the latency graph, and Safe Optimize. Basic additionally enables historical predictions and CSV session exports. Pro additionally enables advanced diagnostics, the outage center, and the adaptive-controller preview. The preview never claims to apply shaping because the native WFP/QoS backend is not implemented yet.

`build.ps1` builds `LagWatch-dev.exe` by default with the developer tier switch. Production promotion is explicit: `build.ps1 -MainBuild` produces `LagWatch.exe` without the developer switch. Feature work should be verified in the dev executable first and promoted only when explicitly authorized.

All external data used by the outage center is free and requires no API token. ISP/ASN identification uses the public no-auth ASN lookup, and ASN-level outage alerts use Georgia Tech's public IODA v2 API. LagWatch does not contain paid API dependencies, token fields, or credential setup. Planned ISP maintenance remains unavailable unless an individual ISP publishes a free no-auth feed.

ASN organization names are normalized to customer-facing provider names (for example, Comcast rather than `COMCAST-7922`). `ISP network / Internet edge` means the path after the home gateway but before or within the wider Internet; it helps distinguish a healthy LAN from an ISP/upstream failure. For providers with an official address/account-based status service, Pro shows an `OPEN ISP STATUS CENTER` button. Comcast opens the official Xfinity outage map, where local and planned work can be matched to the subscriber's service address or signed-in account.

The outage center remains useful offline. Adapter state, gateway reachability, two independent public probes, and DNS resolution are classified locally. Non-healthy classifications are retained for 30 days in `%LOCALAPPDATA%\LagWatch\outage-events.csv`; repeated events within the same time window generate an offline recurring-risk warning. The last detected ISP and last successful IODA result are cached under `%LOCALAPPDATA%\LagWatch` and are visibly labeled cached when live services cannot be reached. Cached external data is never presented as a current confirmation.

Each tier has a visible identity in the header and toolbar. Basic adds a `PREDICTIONS` control that displays the exact local dataset path, target sample count, number of training days, and readiness. Pro additionally adds gold outage and controller controls. Prediction history is pruned daily to a rolling 30-day window. The current prediction method is a robust time-of-day median rather than a trained neural network; calling it machine learning would overstate the implementation.

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
- `network_sdk.py` — adaptive telemetry, baseline, congestion detector, and safe controller policy
- `write_data_to_database.py` — SQLite sessions, samples, flows, congestion events, and actions
- `test_network_sdk.py` — deterministic baseline, jitter, loss, hysteresis, safety, and database tests

To rebuild, right-click `build.ps1`, choose **Run with PowerShell**, or run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

LagWatch targets the Windows .NET Framework desktop runtime included with current Windows 10 and Windows 11 systems.
