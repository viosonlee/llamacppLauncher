using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace LlamaManager.Services;

/// <summary>Bounded local HTTP/TCP readiness; reserved-but-unbound ports are never considered ready.</summary>
internal static class BackendReadinessProbe
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<bool> QueryAsync(int port, string type, string path, CancellationToken ct, string? host = null)
    {
        ct.ThrowIfCancellationRequested();
        if (port is < 1 or > 65535) return false;
        // Probe the engine's bind address. Wildcards map to the corresponding loopback family;
        // do not introduce a blocking DNS lookup for arbitrary plugin names.
        var address = IPAddress.TryParse(host?.Trim('[', ']'), out var bound) ? bound : IPAddress.Loopback;
        if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
        if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.IPv6Loopback;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (type.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            {
                using var socket = new TcpClient(address.AddressFamily);
                await socket.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                return true;
            }
            if (!type.Equals("http", StringComparison.OrdinalIgnoreCase)) return false;
            // Path is origin-form only; manifests cannot turn this local probe into a remote request.
            if (string.IsNullOrEmpty(path) || !path.StartsWith('/') || path.StartsWith("//") || path.Contains('\\')) return false;
            var authority = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            using var response = await Http.GetAsync($"http://{authority}:{port}{path}",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}
