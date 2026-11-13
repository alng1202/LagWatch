using System.Collections.Generic;
using System.Linq;

namespace Netfix.Model;

/// <summary>The seven OSI layers Netfix troubleshoots.</summary>
/// <remarks>
/// Project rule: layers 1–4 can be troubleshot offline (they only touch this PC and the local network);
/// layers 5–7 require the internet to reach non-local servers.
/// </remarks>
public enum OSILayer
{
    Physical = 1,
    DataLink,
    Network,
    Transport,
    Session,
    Presentation,
    Application,
}

public static class OSILayers
{
    public static readonly IReadOnlyList<OSILayer> All =
        new[] { OSILayer.Physical, OSILayer.DataLink, OSILayer.Network, OSILayer.Transport,
                OSILayer.Session, OSILayer.Presentation, OSILayer.Application };

    /// <summary>Layers 5–7 need the internet; layers 1–4 never leave the local network. The single source of truth.</summary>
    public static bool RequiresInternet(this OSILayer layer) => (int)layer >= 5;

    public static IReadOnlyList<OSILayer> OfflineLayers => All.Where(l => !l.RequiresInternet()).ToList();
    public static IReadOnlyList<OSILayer> InternetLayers => All.Where(l => l.RequiresInternet()).ToList();

    public static string Name(this OSILayer layer) => layer switch
    {
        OSILayer.Physical => "Physical",
        OSILayer.DataLink => "Data Link",
        OSILayer.Network => "Network",
        OSILayer.Transport => "Transport",
        OSILayer.Session => "Session",
        OSILayer.Presentation => "Presentation",
        _ => "Application",
    };

    public static string Title(this OSILayer layer) => $"Layer {(int)layer} · {layer.Name()}";

    /// <summary>Short description of what Netfix checks at this layer.</summary>
    public static string Blurb(this OSILayer layer) => layer switch
    {
        OSILayer.Physical => "Link status of Wi‑Fi and Ethernet, Wi‑Fi signal, cable speed and errors.",
        OSILayer.DataLink => "MAC address and whether the router answers ARP.",
        OSILayer.Network => "A valid IP address, default gateway, DNS settings, and ping to the router.",
        OSILayer.Transport => "TCP handshakes with the router and whether local DNS answers.",
        OSILayer.Session => "Opening TCP sessions with servers on the internet.",
        OSILayer.Presentation => "TLS encryption handshakes and certificate validation.",
        _ => "DNS lookups, captive portals, proxies and real web requests.",
    };

    /// <summary>Longer plain-language explanation.</summary>
    public static string Explanation(this OSILayer layer) => layer switch
    {
        OSILayer.Physical => "Layer 1 (Physical) is the actual signal: the Ethernet cable, or the Wi‑Fi radio waves. Problems here look like unplugged cables, Wi‑Fi turned off, or a weak signal.",
        OSILayer.DataLink => "Layer 2 (Data Link) moves frames between devices on the same local network using MAC addresses. Netfix checks your PC has a MAC address and that your router answers ARP — if it doesn't, your PC can't hand any traffic to it.",
        OSILayer.Network => "Layer 3 (Network) is IP addressing and routing. Netfix checks your IP address is valid and on the same network as your router (not a 169.254.x.x self-assigned one), that there's a default gateway, that your DNS settings are usable, and that the router answers ping.",
        OSILayer.Transport => "Layer 4 (Transport) is TCP/UDP — ports and connections. Netfix tries TCP handshakes with your router to confirm connections can be set up on the local network, and checks that a DNS server on your local network answers.",
        OSILayer.Session => "Layer 5 (Session) is setting up and keeping conversations with remote machines. Netfix opens TCP sessions to well-known internet servers by IP address, so DNS isn't involved.",
        OSILayer.Presentation => "Layer 6 (Presentation) covers data formatting and encryption — in practice, TLS. Netfix performs HTTPS handshakes and validates certificates. Certificate errors often mean a wrong system clock, a captive portal, or a proxy intercepting traffic.",
        _ => "Layer 7 (Application) is what apps actually use: DNS names, HTTP, and the web. Netfix resolves domain names, checks for a captive (login) portal, and makes real web requests.",
    };
}
