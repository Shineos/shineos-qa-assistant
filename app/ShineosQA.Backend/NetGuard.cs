using System.Net;

namespace ShineosQA.Backend;

/// <summary>サーバ側外部HTTPリクエストのSSRFガード: http/httpsのみ・localhost/環回/プライベート/予約アドレス拒否</summary>
internal static class NetGuard
{
    public static void EnsurePublicHttp(Uri url)
    {
        if (url.Scheme != "http" && url.Scheme != "https")
            throw new InvalidOperationException($"scheme not allowed: {url.Scheme}");
        var host = url.Host;
        if (string.IsNullOrEmpty(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("localhost not allowed for external fetch");
        if (IPAddress.TryParse(host, out var literal))
        {
            if (!IsPublic(literal)) throw new InvalidOperationException($"non-public address not allowed: {host}");
            return;
        }
        foreach (var a in Dns.GetHostAddresses(host))
            if (!IsPublic(a)) throw new InvalidOperationException($"host resolves to non-public address: {host}");
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return false;
        var b = ip.GetAddressBytes();
        if (b.Length == 16)
        {
            if (b[0] == 0 && new byte[15].SequenceEqual(b.Skip(1).Take(14).ToArray()) && b[15] == 1) return false; // ::1
            return true;
        }
        if (b.Length != 4) return false;
        if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false; // CGNAT
        return true;
    }
}
