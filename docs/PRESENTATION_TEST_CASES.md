# LagWatch presentation test cases

These cases are designed for a live demo. They favor reversible actions and predictable results. Run them once before presenting and keep the fallback cases available.

## Demo setup

- Use LagWatch-dev.exe so the FREE / PLUS / PRO selector is available.
- Start with a stable internet connection.
- Have a browser, Task Manager, and optionally a headset available.
- Do not intentionally disconnect the network during the presentation.
- Keep one screenshot or preview image ready in case Windows blocks ICMP or an external service is unavailable.

## Recommended 10-minute demo sequence

### TC-01 — Start monitoring

Tier: Free  
Purpose: Show the core product immediately.

Steps:

1. Open LagWatch.
2. Confirm the Monitor page is visible.
3. Select the local gateway or 1.1.1.1.
4. Click START.

Expected:

- Samples begin arriving.
- Current latency, average latency, jitter, loss, quality, and timeline update.
- The app remains responsive while monitoring.

Talking point: “LagWatch turns a vague feeling of lag into a live set of measurable signals.”

### TC-02 — Compare local and public paths

Tier: Plus, or use the Monitor target selector manually  
Purpose: Explain local-versus-internet diagnosis.

Steps:

1. Open TOOLS → PATH CHECK.
2. Let it run for several polling cycles.
3. Point out the gateway, public reference, and selected-target rows.

Expected:

- Each row shows latency, loss, and status.
- The rows can differ, showing why comparing only one target can be misleading.

Talking point: “If the gateway is healthy but the public target is bad, the likely problem is beyond the PC and local router.”

Fallback: If Path Check is not available in the current tier, switch the Monitor target between the gateway and 1.1.1.1.

### TC-03 — Game process detection

Tier: Free  
Purpose: Show local game awareness without requiring a live game.

Steps:

1. Open the process selector.
2. Enable Auto-detect running games.
3. Start a supported game if available, or select a visible application manually.

Expected:

- The process list refreshes.
- A detected game is selected or the UI allows manual selection.
- No process names or paths are sent to an external service.

Talking point: “Detection happens locally. The app does not need an account or game integration.”

Fallback: Use a manually selected process and explain that the same target-selection flow is available.

### TC-04 — Optimize review and apply

Tier: Free  
Purpose: Demonstrate a safe, bounded optimization.

Steps:

1. Select a running application or game.
2. Open OPTIMIZE.
3. Choose a profile or custom settings.
4. Review the checklist.
5. Apply the visual-effects setting or process-priority change.
6. Reopen the dialog or Windows Performance Options to show the result.

Expected:

- The proposed changes are shown before application.
- Process priority changes only the running process session.
- Visual effects change through the supported Windows setting.
- No DNS, registry tweaks, services, or firewall changes are made.

Talking point: “Optimize is intentionally conservative and reversible.”

Fallback: Use the preview image or explain the review screen without applying a change.

### TC-05 — Troubleshoot health scan

Tier: Free  
Purpose: Show the diagnostic workflow.

Steps:

1. Open TROUBLESHOOT.
2. Run the general check.
3. Wait for the progress state and result cards.
4. Expand one healthy item or issue card.

Expected:

- The scan reports PC, sound/call, and network observations.
- Results are written in plain language.
- Fix cards explain what a proposed repair changes.
- The removed Security page is not present; relevant connectivity findings remain in the normal diagnosis flow.

Talking point: “The user gets a cause and a next action, not just a raw command output.”

Fallback: Use the built-in preview mode or a previously captured result screenshot.

### TC-06 — Speaker device listing and test

Tier: Pro  
Purpose: Demonstrate per-device audio testing.

Setup: Connect a headset or USB audio device before launching or reopening Workspace. At minimum, use the computer’s built-in speakers.

Steps:

1. Open WORKSPACE.
2. Find the SPEAKERS card.
3. Click PLAY SOUND on a device.
4. Point out the DEFAULT, MUTED, or CALLS tags.
5. If a second device is present, test both rows.

Expected:

- Every active output device appears as its own row.
- The test tone targets the selected row.
- The row’s result reports success or failure.

Fallback: If only one speaker exists, explain that the list enumerates all active Windows endpoints.

### TC-07 — Microphone test

Tier: Pro  
Purpose: Demonstrate live microphone feedback.

Steps:

1. In WORKSPACE, find MICROPHONES.
2. Click TEST MIC.
3. Speak normally for several seconds.
4. Show the live level bar and final result.

Expected:

- The level bar moves while speaking.
- The result says Heard you or explains that no usable level was detected.
- The test does not silently switch the Windows default device.

Fallback: If microphone privacy blocks access, use the failure message as a useful diagnostic example and open Windows microphone privacy settings.

### TC-08 — Separate call preference from Windows default

Tier: Pro  
Purpose: Demonstrate the product distinction recently added.

Steps:

1. Click USE FOR CALLS on a device.
2. Confirm the CALLS tag moves to that row.
3. Click SET AS DEFAULT on a different device.
4. Confirm the DEFAULT tag moves independently.

Expected:

- CALLS stores the LagWatch preference.
- DEFAULT changes Windows’ default device.
- The two tags can be on different rows.
- The UI explains the difference.

Talking point: “One choice tells LagWatch what to remember for calls; the other changes Windows itself.”

### TC-09 — Network Details

Tier: Pro  
Purpose: Show the technician-facing evidence.

Steps:

1. Open TOOLS → NETWORK DETAILS.
2. Run the layer checks.
3. Expand the OSI sections.
4. Show the adapter, gateway, DNS, and layer findings.

Expected:

- Adapter details are visible.
- Layer reports show pass, warning, failure, or skipped states.
- Offline-capable checks remain useful even if internet-dependent checks cannot run.

Talking point: “This is the evidence a support person normally has to collect manually.”

### TC-10 — Route Map

Tier: Pro  
Purpose: Explain route diagnosis.

Steps:

1. Select a public target such as 1.1.1.1.
2. Open TOOLS → ROUTE MAP.
3. Wait for the trace.
4. Point out hop number, address, device, latency, and timeouts.

Expected:

- A hop-by-hop route appears when the network and target allow traceroute replies.
- The summary reports observed hops and non-responding hops.
- A timeout is presented as a non-response, not automatically as proof of packet loss.

Talking point: “Path Check tells us which comparison target is unhealthy; Route Map shows the path that target takes.”

Fallback: Use the route-map preview or a prepared screenshot.

### TC-11 — Export a session

Tier: Plus  
Purpose: Show support handoff.

Steps:

1. Let Monitor collect several samples.
2. Click EXPORT CSV.
3. Open the saved file.

Expected:

- A readable CSV is created.
- The file contains session measurements suitable for later review or ISP support.

Talking point: “The user can hand over evidence instead of trying to describe a five-minute lag event from memory.”

### TC-12 — Tray/background behavior

Tier: Free  
Purpose: Show that monitoring continues outside the main window.

Steps:

1. Start monitoring.
2. Minimize the window.
3. Open the notification-area icon.
4. Reopen the window or pause/resume from the tray.

Expected:

- The app remains in the notification area.
- Monitoring continues while minimized.
- Tray commands can reopen, pause, resume, or exit.

## Tier demonstration

Use the developer tier selector and show the progressive feature surface:

| Tier | Demonstrate |
|---|---|
| Free | Monitor, game detection, Optimize, Troubleshoot, safe fixes |
| Plus | Forecast, heads-ups, Path Check, custom scans, network repairs, export |
| Pro | Network Details, Route Map, Outage Map, Background Watch, Workspace, reports |

Say: “The tiers add depth and automation around the same core diagnosis; they do not change the basic monitoring model.”

## Deliberate failure cases

Use these only if you want to show diagnostics rather than a happy path.

### Failure A — Invalid target

Enter a URL such as https://example.com where a host or IP is expected.

Expected: LagWatch rejects it instead of treating the URL as a ping target.

### Failure B — Microphone permission blocked

Temporarily deny microphone access in Windows Privacy settings, then run TEST MIC.

Expected: LagWatch reports that it could not listen and points toward privacy settings. Restore permission afterward.

### Failure C — No route response

Run Route Map against a target that filters traceroute replies.

Expected: Some hops may show TIMEOUT; the UI explains that non-response is not automatically packet loss.

### Failure D — Only one audio endpoint

Use a machine with no headset connected.

Expected: The speaker/microphone list still appears and accurately shows the active endpoints. Explain that plugging in a headset and rebuilding the page adds rows.

## Presenter recovery checklist

If something goes wrong:

1. Stop the current test rather than changing multiple settings.
2. Return to Monitor and select 1.1.1.1.
3. Confirm Windows is still online in a browser.
4. Restart the app if a page is stale.
5. Use the prepared preview image for unavailable Pro tools.
6. Avoid intentionally changing DNS, proxy, firewall, hosts, VPN, or adapter settings during the presentation.

