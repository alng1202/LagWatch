using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Netfix.Diagnostics;

/// <summary>Opens a TCP connection and times it.</summary>
public static class TcpProbe
{
    public sealed record Outcome(bool Open, double Ms, string Description)
    {
        /// <summary>Something answered: the connection opened, or the host refused it (it's there, the port is closed).</summary>
        public bool Answered => Open || Description == "refused";
    }

    public static async Task<Outcome> Probe(string host, int port, double timeoutSeconds)
    {
        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return new Outcome(true, sw.Elapsed.TotalMilliseconds, "open");
        }
        catch (OperationCanceledException) { return new Outcome(false, 0, "timed out"); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused) { return new Outcome(false, 0, "refused"); }
        catch (SocketException e) { return new Outcome(false, 0, e.SocketErrorCode.ToString()); }
    }
}

/// <summary>Looks names up the way apps do (Windows' own DNS settings and cache).</summary>
public static class DnsProbe
{
    public sealed record Result(IReadOnlyList<string> Addresses, double Ms, string? Error);

    public static async Task<Result> Resolve(string host, double timeoutSeconds = 6)
    {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token);
            return new Result(addresses.Select(a => a.ToString()).ToList(), sw.Elapsed.TotalMilliseconds,
                              addresses.Length == 0 ? "No addresses" : null);
        }
        catch (OperationCanceledException) { return new Result(Array.Empty<string>(), 0, "Timed out"); }
        catch (SocketException e) { return new Result(Array.Empty<string>(), 0, e.Message); }
    }
}

/// <summary>Asks one DNS server a question directly over UDP (bypassing Windows' settings and cache).</summary>
public static class DnsServerProbe
{
    public enum Outcome { Answered, NoAnswer }

    /// <summary>A DNS query packet for <paramref name="name"/> (type A, or PTR for in-addr.arpa names).</summary>
    public static byte[] Query(string name, ushort id)
    {
        var packet = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        var type = name.EndsWith("in-addr.arpa", StringComparison.OrdinalIgnoreCase) ? (byte)12 : (byte)1;
        packet.AddRange(new byte[] { 0, 0, type, 0, 1 });
        return packet.ToArray();
    }

    /// <summary>The reverse-lookup name for an address: 192.168.1.1 → 1.1.168.192.in-addr.arpa.</summary>
    public static string ReverseName(IPAddress ip) => string.Join('.', ip.GetAddressBytes().Reverse()) + ".in-addr.arpa";

    /// <summary>Any reply counts, even "no such name": the server is there and answering.</summary>
    public static async Task<Outcome> Ask(IPAddress server, string name = "example.com", double timeoutSeconds = 3)
    {
        using var udp = new UdpClient(server.AddressFamily);
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        try
        {
            var query = Query(name, id);
            await udp.SendAsync(query, query.Length, new IPEndPoint(server, 53));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var reply = await udp.ReceiveAsync(cts.Token);
            return reply.Buffer.Length >= 12 && reply.Buffer[0] == (byte)(id >> 8) && reply.Buffer[1] == (byte)id
                ? Outcome.Answered : Outcome.NoAnswer;
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException) { return Outcome.NoAnswer; }
    }
}

/// <summary>Makes a web request and records what happened to the secure connection.</summary>
public static class HttpProbe
{
    public enum CertProblem { None, Date, Untrusted }

    public sealed record Result(int? StatusCode, double Ms, string Body, string? Error, CertProblem Cert, string? Issuer,
                                SslProtocols? Tls, DateTimeOffset? ServerDate);

    public static async Task<Result> Fetch(string url, string method = "GET", double timeoutSeconds = 10, bool useProxy = true)
    {
        var cert = CertProblem.None;
        string? issuer = null;
        SslProtocols? tls = null;
        var handler = new SocketsHttpHandler
        {
            UseProxy = useProxy,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            SslOptions = new SslClientAuthenticationOptions
            {
                // Watches the certificate check without changing it: an untrusted certificate still fails.
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                {
                    // (SslStream.SslProtocol can't be read here: the handshake isn't finished, and it throws.)
                    issuer = (certificate as X509Certificate2)?.Issuer ?? certificate?.Issuer;
                    if (errors == SslPolicyErrors.None) return true;
                    var dateProblem = chain?.ChainStatus.Any(c => c.Status is X509ChainStatusFlags.NotTimeValid) == true;
                    cert = dateProblem ? CertProblem.Date : CertProblem.Untrusted;
                    return false;
                },
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Netfix/1.0");
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            using var response = await client.SendAsync(request);
            var body = method == "HEAD" ? "" : await response.Content.ReadAsStringAsync();
            return new Result((int)response.StatusCode, sw.Elapsed.TotalMilliseconds, body, null, cert, issuer, tls, response.Headers.Date);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            var message = e is HttpRequestException { InnerException: { } inner } ? inner.Message : e.Message;
            if (e is TaskCanceledException or OperationCanceledException) message = "Timed out";
            return new Result(null, sw.Elapsed.TotalMilliseconds, "", message, cert, issuer, tls, null);
        }
    }

    /// <summary>The server's clock, from a plain web request's Date header (for "Your PC's clock is wrong").</summary>
    public static async Task<DateTimeOffset?> ServerTime()
    {
        var r = await Fetch("http://www.msftconnecttest.com/connecttest.txt", "HEAD", 6);
        return r.ServerDate;
    }
}

/// <summary>Pings with Windows' ICMP API (no administrator needed, and not limited to once a second).</summary>
public static class PingProbe
{
    public sealed record Result(int Sent, int Received, double AverageMs)
    {
        public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;
    }

    public static async Task<Result> Ping(IPAddress host, int count = 5, int timeoutMs = 1000, int intervalMs = 200)
    {
        using var ping = new Ping();
        var times = new List<long>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync(host, timeoutMs);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch (PingException) { }
            if (i < count - 1) await Task.Delay(intervalMs);
        }
        return new Result(count, times.Count, times.Count == 0 ? 0 : times.Average());
    }
}
