using System.Net;

namespace PerfTuner.Core.Network;

/// <summary>A DNS choice. No servers = automatic (whatever the network's router hands out).</summary>
public sealed record DnsProvider(string Id, string Name, string Description, IReadOnlyList<string> Servers);

/// <summary>Well-known public DNS resolvers (IPv4 addresses as published by each operator). Only IPv4 DNS is changed; IPv6 DNS from the router stays in use where the network offers it.</summary>
public static class DnsPresets
{
    public static IReadOnlyList<DnsProvider> All { get; } =
    [
        new("auto", "Automatic", "Use the DNS servers your router or network provides (Windows' default).", []),
        new("cloudflare", "Cloudflare (1.1.1.1)", "Fast public resolver with a privacy commitment.", ["1.1.1.1", "1.0.0.1"]),
        new("cloudflare-security", "Cloudflare, malware blocking (1.1.1.2)", "Like 1.1.1.1, and blocks known malware sites.", ["1.1.1.2", "1.0.0.2"]),
        new("quad9", "Quad9 (9.9.9.9)", "Non-profit resolver that blocks known malicious domains.", ["9.9.9.9", "149.112.112.112"]),
        new("google", "Google Public DNS (8.8.8.8)", "Google's public resolver.", ["8.8.8.8", "8.8.4.4"]),
        new("adguard", "AdGuard DNS (94.140.14.14)", "Blocks ads and trackers for every app on this PC.", ["94.140.14.14", "94.140.15.15"]),
        new("opendns", "OpenDNS (208.67.222.222)", "Cisco's public resolver.", ["208.67.222.222", "208.67.220.220"]),
    ];

    public static DnsProvider? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The preset whose servers are exactly <paramref name="servers"/> (in order), or null for a custom set.</summary>
    public static DnsProvider? Match(IReadOnlyList<string> servers, bool automatic)
    {
        if (automatic) { return All[0]; }
        return All.Skip(1).FirstOrDefault(p => p.Servers.SequenceEqual(servers));
    }

    /// <summary>Every entry is a plain IPv4 or IPv6 address (used to check saved values before restoring them).</summary>
    public static bool AreAddresses(IEnumerable<string> servers) =>
        servers.All(s => s.Length <= 45 && IPAddress.TryParse(s, out var ip) && ip.ToString() == s);
}
