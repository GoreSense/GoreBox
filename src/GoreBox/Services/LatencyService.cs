using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GoreBox.Models;

namespace GoreBox.Services;

/// <summary>Измерение задержки до сервера профиля: TCP-коннект, при неудаче — ICMP.</summary>
public sealed class LatencyService
{
    public async Task<int> MeasureAsync(ProxyProfile profile, CancellationToken ct = default)
    {
        var node = profile.Node;
        if (string.IsNullOrWhiteSpace(node.Server) || node.Port <= 0) return -1;

        var udpBased = ProtocolIds.IsUdpBased(profile.Protocol);
        var tcp = await TryTcpAsync(node.Server, node.Port, TimeSpan.FromSeconds(4), ct);
        if (tcp >= 0) return tcp;

        var icmp = await TryIcmpAsync(node.Server, ct);
        if (icmp >= 0) return icmp;

        _ = udpBased;
        return -1;
    }

    public static async Task<int> TryTcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(host, out var direct)
                ? new[] { direct }
                : await Dns.GetHostAddressesAsync(host, ct);

            foreach (var address in addresses.Take(2))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                var sw = Stopwatch.StartNew();
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(timeout);
                    await socket.ConnectAsync(new IPEndPoint(address, port), linked.Token);
                    sw.Stop();
                    return (int)sw.ElapsedMilliseconds;
                }
                catch
                {
                    // пробуем следующий адрес
                }
                finally
                {
                    socket.Dispose();
                }
            }
        }
        catch { /* ignore */ }
        return -1;
    }

    public static async Task<int> TryIcmpAsync(string host, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 3000);
            if (reply.Status == IPStatus.Success) return (int)reply.RoundtripTime;
        }
        catch { /* ignore */ }
        return -1;
    }
}
