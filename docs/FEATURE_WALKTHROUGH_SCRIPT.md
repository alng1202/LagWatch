# LagWatch feature walkthrough script

Use this as a product demo, onboarding narration, or manual acceptance checklist. It describes the current application after removal of the standalone Security page.

## Opening

“LagWatch watches the network conditions that make online games and calls feel bad: latency, jitter, packet loss, route changes, and local connection health. It runs quietly in Windows, explains likely causes in plain language, and keeps fixes reversible.”

Launch `LagWatch-dev.exe` and show the main window.

“The header shows the current latency, quality, connection state, and subscription tier. The app can minimize to the notification area and keep monitoring.”

## Monitor: live connection quality

1. Open the **MONITOR** page.
2. Point out the selected target and the **START/PAUSE** control.
3. Start monitoring.

Say:

“LagWatch sends regular ICMP reference probes to the selected target. It reports current latency, average latency, jitter, packet loss, and a rolling timeline. ICMP is a reference signal; it is not presented as the game’s exact UDP transport latency.”

Select the local gateway, then a public reference such as `1.1.1.1`.

“The gateway helps separate the PC-to-router path from the wider internet. A healthy gateway does not guarantee that the provider or game route is healthy.”

## Game detection

1. Enable **Auto-detect running games**.
2. Show the process selector refreshing.
3. If a supported game is running, select it.

Say:

“LagWatch scans visible local processes and recognizes common game executables and launcher locations. When possible, it inspects established public TCP endpoints and uses a visible non-web endpoint as a reference target. It does not guess a UDP match server.”

If no game is running, select a process manually or explain that the target can be entered directly.

## Forecast and history

On Plus or Pro, show the forecast area.

“Successful samples are stored locally. After enough observations across multiple days, LagWatch shows what latency is usual at this time, the typical range, and the next six hours of expected conditions. It also flags recurring spotty periods using time-of-week history.”

Point out that early history shows a learning state rather than pretending one sample is a prediction.

## Connection heads-ups

Leave the app running or open the tray menu.

“In the background, LagWatch checks the local gateway and independent public references. It summarizes the recent five-minute window as healthy, spotty, a local outage, or a provider-side outage when the evidence supports that distinction.”

Explain the notifications:

- during a problem;
- after recovery;
- before a recurring bad period on Plus/Pro;
- when a public provider-outage feed reports an ASN-level event on Plus/Pro.

“Nothing is changed automatically. The notification explains the likely side and links to the relevant repair flow.”

## Optimize

Open **OPTIMIZE**.

“Optimize is deliberately narrow. It can set the selected game process to Above normal for this running session and change Windows visual effects. It does not apply registry tweaks, replace DNS, disable services, reset Winsock, or pretend to shape traffic.”

Open the profile or custom choices, review them, and show the confirmation step.

“The user reviews the changes before applying them, and Windows remains the source of truth for the settings.”

## Troubleshoot

Open **TROUBLESHOOT** and run the general check.

“Network Doctor combines PC health, sound, camera, Bluetooth, and network diagnosis. It checks from the local adapter and router upward through the network layers, then explains the lowest failing layer first.”

Show an issue card if available.

“Every issue explains what was found, why it matters, and what the proposed fix will change. Fixes that need administrator permission are gated and confirmed before Windows applies them.”

Mention examples:

- turn Wi-Fi or Bluetooth back on;
- restart Wi-Fi or sound;
- refresh DNS;
- repair automatic IP/DNS settings;
- remove stale VPN routes;
- unblock known sites in the hosts file;
- quit an eligible foreground resource hog;
- open the appropriate Windows Settings page.

“The former standalone Security page is gone. Connection-relevant findings remain part of Troubleshoot and Network Details; LagWatch is not presented as an antivirus or full security-audit product.”

## Workspace audio controls

On Pro, open **WORKSPACE**.

“Workspace is for call readiness and PC responsiveness. It can check PC performance and list every active speaker and microphone.”

For a speaker row:

1. Click **PLAY SOUND**.
2. Explain that the test targets that specific endpoint, not merely the current default.
3. Click **SET AS DEFAULT** if appropriate.

For a microphone row:

1. Click **TEST MIC**.
2. Show the live level bar.
3. Explain the Heard you / Heard nothing result.

“SET AS DEFAULT changes Windows’ Console, Multimedia, and Communications roles. USE FOR CALLS saves LagWatch’s preferred call device separately, so the app can remind the user if Windows or a call app switches away from it.”

## Path Check

Open **TOOLS → PATH CHECK** on Plus.

“Path Check compares the local gateway, a public reference, and the selected target continuously. It shows latency, loss, and status for each row. This answers: which comparison path is degraded right now?”

Point out that this is the everyday comparison tool, not a hop-by-hop traceroute.

## Network Details

Open **TOOLS → NETWORK DETAILS** on Pro and run the layer checks.

“Network Details exposes the technician view: adapters, addresses, gateway, DNS, link information, and findings from the physical/link layers through TCP, TLS, DNS, and web checks.”

Expand the OSI sections.

“Offline-capable checks are still useful when the internet is down. Internet-dependent checks are marked skipped rather than incorrectly reported as failures.”

## Route Map

Open **TOOLS → ROUTE MAP** on Pro.

“Route Map runs a traceroute and lists each hop, address, device label, latency, and timeout. It answers: what path does traffic take, and where does elevated latency begin?”

Explain that a timeout does not automatically mean packet loss because routers commonly suppress traceroute replies. Compare more than one run before drawing a conclusion.

## Outage Map and Background Watch

Open **TOOLS → OUTAGE MAP** on Pro.

“Outage Map combines the local connection history, detected provider and ASN, public outage signals, and official provider status links. Cached external results are labeled as cached.”

Open or describe **BACKGROUND WATCH**.

“Background Watch keeps the connection-health classification active while the main window is hidden. The tray can reopen LagWatch, pause monitoring, resume it, or exit.”

## Export and reports

On Plus, click **EXPORT CSV**.

“Export saves the current monitoring session for later review or support escalation. Network reports can include the adapter and layer evidence needed to explain the problem to an ISP.”

## Plans

Show the developer tier selector if this is a dev build.

“Free includes monitoring, game detection, Safe Optimize, Troubleshoot, speed testing, and safe fixes. Plus adds forecasts, heads-ups, Path Check, exports, custom scans, and network repairs. Pro adds Network Details, Route Map, Outage Map, Background Watch, reports, and Workspace.”

“The tier selector is development-only. There is no billing or account system in the current application.”

## Closing

“LagWatch’s central promise is evidence before intervention: measure the path, distinguish local from upstream problems, explain the likely cause, and offer the smallest reversible next step. It does not claim to replace an ISP, a managed-device administrator, an antivirus, or a full security audit.”

## Acceptance checklist

- Monitor starts, pauses, changes targets, and shows latency/loss/jitter.
- Game detection refreshes and manual process selection works.
- Forecast shows learning state before enough history and predictions afterward.
- Connection heads-ups classify local/provider-side symptoms without changing settings automatically.
- Optimize requires review and changes only supported reversible settings.
- Troubleshoot produces plain-language issues and tier-appropriate fixes.
- Workspace lists every active audio endpoint.
- Speaker and microphone tests target the selected row.
- Set as default and Use for calls remain separate actions.
- Path Check compares gateway/public/selected targets.
- Network Details shows adapters and layer findings.
- Route Map shows hop-by-hop traceroute results.
- Outage Map labels live versus cached external data.
- Export produces the requested session/report artifact.

