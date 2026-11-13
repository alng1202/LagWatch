# LagWatch manual runbook

This is the human equivalent of the routine work LagWatch performs automatically or guides the user through. It is intended for support staff, technically experienced users, and anyone who needs to reproduce a diagnosis without the app.

## 1. Start-up and basic monitoring

1. Open LagWatch normally. It runs as the current Windows user; administrator rights are not required for monitoring.
2. If enabled, Windows starts LagWatch at sign-in in background/tray mode.
3. Confirm the selected target on Monitor. The default local gateway is useful for isolating the PC-to-router path. A public reference such as `1.1.1.1` tests beyond the router.
4. Press **START** or **F5** after changing the target. Use **PAUSE** before changing targets if you want a clean comparison.
5. Watch current latency, average latency, jitter, packet loss, and the two-minute timeline.
6. Watch the session card’s aggregate upload and download Mbps values while testing an active download or upload.
7. Minimize the window to keep it on the taskbar while monitoring continues. Close the window to hide LagWatch to the notification area by default, or disable the close-to-tray option in Settings when close should exit the app.

Manual equivalent: open Command Prompt or PowerShell and run repeated `ping` commands against both the default gateway and a public reference. Record latency and failures over several minutes rather than judging from one reply.

```powershell
ipconfig
ping <default-gateway>
ping 1.1.1.1
```

Do not treat a successful gateway ping as proof that the wider internet is healthy, or a failed gateway ping as proof that the router is broken; some routers suppress ICMP.

## 2. Compare local versus internet problems

LagWatch’s automatic connection watch repeats this comparison every thirty seconds and summarizes the last five minutes.

Manual procedure:

1. Find the **Default Gateway** with `ipconfig`.
2. Ping the gateway and one or two independent public references.
3. Compare loss and latency:
   - gateway bad and public bad: investigate Wi-Fi, cable, adapter, or router;
   - gateway good and public bad: investigate modem/ISP/upstream, DNS, VPN, or provider outage;
   - both good but the game is bad: test the game target or route specifically.
4. Repeat during the actual lag window. A clean test after the event is not evidence that the earlier event did not happen.

## 3. Identify the active game and target

LagWatch refreshes visible processes every four seconds and can recognize common game installations.

Manual equivalent:

1. Open Task Manager and find the game process.
2. Open Resource Monitor → Network, or use an approved connection viewer, to inspect the game’s established remote endpoints.
3. Ignore launcher/CDN/web connections when selecting a measurement target.
4. If the game is UDP-heavy, do not assume a TCP endpoint is the match server. The Windows user-space tables do not reliably expose the UDP peer used by every game.

## 4. Understand latency, jitter, and loss

LagWatch calculates current/average/minimum/maximum latency, loss, and average absolute changes between successful samples.

Manual equivalent:

1. Collect at least 30–60 samples.
2. Separate timeouts from high but successful replies.
3. Compare the median/average with the normal baseline.
4. Look for bursts rather than isolated spikes.
5. Test while idle and while uploading/downloading. A large increase under load suggests queueing or bufferbloat.

PowerShell’s `Test-Connection` can provide basic samples, but it does not reproduce all of LagWatch’s rolling calculations.

## 5. Use Path Check

Path Check continuously pings three comparison targets: the local gateway, a public reference, and the selected target.

Manual equivalent:

1. Run parallel or repeated pings to all three targets.
2. Record latency and loss for each.
3. Compare the rows over time.

Path Check answers “which comparison path is degraded right now?” It does not show every router hop.

## 6. Use Route Map

Route Map runs a traceroute and displays hop number, address, device label, latency, and timeout state.

Manual equivalent:

```powershell
tracert -d -h 20 -w 800 <target>
```

Interpretation:

- `* * *` means that hop did not answer the probe; it does not automatically mean packets are being dropped.
- A later hop responding normally can mean an earlier hop simply filters traceroute replies.
- Look for latency that remains elevated from a hop onward, not one isolated slow row.
- Compare multiple runs and test from more than one time period.

Route Map is explanatory and mostly one-shot. Path Check is the better tool for continuous health.

## 7. Run Troubleshoot

Open **TROUBLESHOOT** and run the general check. LagWatch performs or guides these manual categories:

- inspect CPU/memory pressure and visible app resource use;
- inspect storage/restart/battery-related PC conditions;
- inspect Wi-Fi, adapter, gateway, ARP, IP, DNS, TCP, TLS, proxy, hosts, and web reachability;
- inspect sound, microphones, cameras, Bluetooth, and call-device state;
- run a speed test only when the network state makes the result meaningful.

Manual Windows locations and commands:

| LagWatch diagnosis | Manual equivalent |
|---|---|
| CPU or memory pressure | Task Manager → Processes / Performance |
| Adapter state | Settings → Network & internet → Advanced network settings; `ipconfig /all` |
| Gateway/IP/DNS | `ipconfig /all`, `arp -a`, `route print` |
| Wi-Fi signal/interface | `netsh wlan show interfaces` |
| Nearby Wi-Fi/channel crowding | `netsh wlan show networks mode=bssid` |
| VPN sessions | `rasdial` and Settings → Network & internet → VPN |
| DNS lookup | `nslookup <name>` or `Resolve-DnsName <name>` |
| TCP reachability | `Test-NetConnection <host> -Port 443` |
| Secure website/TLS | Open the site in a browser and inspect certificate warnings/details |
| Proxy | Settings → Network & internet → Proxy; inspect Internet Options proxy settings |
| Hosts-file blocks | Inspect `%WINDIR%\System32\drivers\etc\hosts` as administrator |
| Firewall policy | Windows Security → Firewall & network protection; `netsh advfirewall show allprofiles firewallpolicy` |
| System network events | Event Viewer → Windows Logs → System |

Make a backup before manually editing DNS, proxy, hosts, routes, or adapter settings.

## 8. Sound, microphone, and call devices

Workspace lists all active endpoints. For manual reproduction:

1. Open Settings → System → Sound.
2. Under Output, select each speaker/headset and use the test control.
3. Under Input, select each microphone and watch the input meter.
4. Use **Set as default** in Sound settings to change Windows’ default output/input.
5. Open the call application and set its speaker/microphone separately; many call apps retain their own choice instead of following Windows.
6. If Windows keeps switching devices, check Bluetooth connection state, USB power, exclusive-mode settings, and the app’s device preference.

LagWatch’s two buttons have different meanings:

- **SET AS DEFAULT** changes Windows’ Console, Multimedia, and Communications roles.
- **USE FOR CALLS** saves LagWatch’s preferred call device so Checkup can warn if Windows changes away from it.

For microphone privacy problems, check Settings → Privacy & security → Microphone and allow desktop apps to access the microphone. For camera problems, use the corresponding Camera privacy page.

## 9. Optimize a game session

LagWatch’s Optimize flow performs two reversible changes after review:

1. Select the game process.
2. Choose a profile or custom settings.
3. Allow the game process priority to be set to **Above normal** for that running process.
4. Optionally change Windows visual effects under System → Advanced system settings → Performance Options.

Manual equivalent:

- Task Manager → Details → game process → Set priority → Above normal.
- Run `sysdm.cpl`, Advanced → Performance → Settings, then choose visual effects.

Do not assume generic registry “gaming tweaks,” disabling services, changing TCP autotuning, or replacing DNS will improve game latency. Those changes can break connectivity and are outside LagWatch’s implemented Optimize behavior.

## 10. Recurring spotty periods and provider outages

LagWatch stores local five-minute health buckets and compares similar times on earlier days. Manual equivalent:

1. Keep a dated log of gateway/public latency, loss, and the game’s symptoms.
2. Record the time window, Wi-Fi/cable state, VPN state, and active downloads/uploads.
3. Compare the same weekday/time across several days.
4. Check the ISP’s official outage page.
5. Optionally check a public outage monitor such as IODA for the provider ASN, understanding that aggregate ASN signals do not prove an outage at one subscriber address.

## 11. Network Details and reports

Network Details shows the same broad evidence a technician would collect: adapters, addresses, gateways, DNS, link information, and OSI-layer findings.

Manual collection:

```powershell
ipconfig /all
route print
arp -a
netsh wlan show interfaces
netsh interface ipv4 show dnsservers
rasdial
netsh advfirewall show allprofiles firewallpolicy
```

Keep the outputs with timestamps and the target used. When escalating to an ISP, include the route, repeated ping results, outage time, adapter type, and whether the gateway remained reachable.

## 12. Data, privacy, and cleanup

LagWatch keeps its monitoring history locally. To inspect or remove it:

1. Exit LagWatch from the notification area.
2. Open `%LOCALAPPDATA%\LagWatch`.
3. Back up files you want to retain.
4. Remove old history/cache files only when the app is closed.
5. Reopen LagWatch and confirm that monitoring still starts.

The app does not need an account or API token. External checks use public endpoints and should be treated as network traffic visible to the relevant service.

## 13. Safety boundaries

Do not manually apply a fix just because a command is available. Confirm:

- the affected adapter, target, and time window;
- whether the setting was intentional (corporate proxy, VPN, hosts blocker, static DNS);
- whether a work-managed device policy controls it;
- whether you have a backup or rollback path.

LagWatch’s purpose is to reduce routine diagnosis and reversible repair work. It is not a replacement for an ISP escalation, managed-device administrator, malware investigation, or a full security audit.
