using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Models;
using WinRtConnectivityLevel = Windows.Networking.Connectivity.NetworkConnectivityLevel;
using WinRtNetworkInformation = Windows.Networking.Connectivity.NetworkInformation;

namespace PulseDesk.Infrastructure.Network;

/// <summary>
/// Local network state: interfaces, addresses and throughput from the IP Helper API, and the
/// connectivity level Windows itself determined (Network Connectivity Status Indicator).
/// </summary>
/// <remarks>
/// PulseDesk sends no network traffic to produce these values: "Internet unavailable" is what Windows
/// already concluded from its own periodic checks.
/// </remarks>
public sealed class WindowsNetworkMetricProvider : INetworkMetricProvider, IDisposable
{
    /// <summary>
    /// Interface lists, addresses and connectivity change rarely; they are re-read at most this often
    /// (and immediately when Windows reports a network change). Only byte counters are read every sample.
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);

    private readonly ILogger<WindowsNetworkMetricProvider> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Counters> _previous = new(StringComparer.Ordinal);
    private List<TrackedInterface> _interfaces = [];
    private long _interfacesReadAt;
    private NetworkConnectivity _connectivity = NetworkConnectivity.Unknown;
    private long _connectivityReadAt;
    private bool _connectivityErrorLogged;

    public WindowsNetworkMetricProvider(ILogger<WindowsNetworkMetricProvider> logger)
    {
        _logger = logger;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    public Task<NetworkMetrics> CollectAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            if (_interfacesReadAt == 0 || Stopwatch.GetElapsedTime(_interfacesReadAt, now) > CacheDuration)
            {
                _interfaces = ReadInterfaces();
                _interfacesReadAt = now;
            }

            if (_connectivityReadAt == 0 || Stopwatch.GetElapsedTime(_connectivityReadAt, now) > CacheDuration)
            {
                _connectivity = ReadConnectivity();
                _connectivityReadAt = now;
            }

            var metrics = new List<NetworkInterfaceMetrics>(_interfaces.Count);
            foreach (var tracked in _interfaces)
            {
                if (ReadCounters(tracked, now) is { } item)
                {
                    metrics.Add(item);
                }
            }

            return Task.FromResult(new NetworkMetrics(_connectivity, metrics));
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
    }

    private static List<TrackedInterface> ReadInterfaces()
    {
        var result = new List<TrackedInterface>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || nic.IsReceiveOnly)
                {
                    continue;
                }

                // Windows also lists hidden filter drivers (QoS, WFP) and WAN miniports as interfaces. They carry
                // no IP address; skipping them avoids showing the same adapter several times.
                var properties = nic.GetIPProperties();
                if (properties.UnicastAddresses.Count == 0)
                {
                    continue;
                }

                result.Add(new TrackedInterface(nic, new NetworkInterfaceMetrics(nic.Id, nic.Name, nic.Description, ToKind(nic.NetworkInterfaceType))
                {
                    LinkSpeedBitsPerSecond = nic.Speed > 0 ? nic.Speed : null,
                    IPv4Addresses = Addresses(properties, AddressFamily.InterNetwork),
                    IPv6Addresses = Addresses(properties, AddressFamily.InterNetworkV6),
                    HasGateway = properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)),
                }));
            }
            catch (NetworkInformationException)
            {
                // The interface disappeared during enumeration.
            }
        }

        return result;
    }

    private NetworkInterfaceMetrics? ReadCounters(TrackedInterface tracked, long now)
    {
        try
        {
            var statistics = tracked.Nic.GetIPStatistics();
            var received = statistics.BytesReceived;
            var sent = statistics.BytesSent;

            double? receiveRate = null, sendRate = null;
            if (_previous.TryGetValue(tracked.Info.Id, out var previous))
            {
                var seconds = Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds;
                if (seconds > 0 && received >= previous.Received && sent >= previous.Sent)
                {
                    receiveRate = (received - previous.Received) * 8 / seconds;
                    sendRate = (sent - previous.Sent) * 8 / seconds;
                }
            }

            _previous[tracked.Info.Id] = new Counters(received, sent, now);
            return tracked.Info with
            {
                BytesReceived = received,
                BytesSent = sent,
                ReceiveBitsPerSecond = receiveRate,
                SendBitsPerSecond = sendRate,
            };
        }
        catch (NetworkInformationException)
        {
            // The interface disappeared since the last enumeration.
            return null;
        }
    }

    private NetworkConnectivity ReadConnectivity()
    {
        try
        {
            var profile = WinRtNetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return NetworkInterface.GetIsNetworkAvailable() ? NetworkConnectivity.LocalAccess : NetworkConnectivity.None;
            }

            return profile.GetNetworkConnectivityLevel() switch
            {
                WinRtConnectivityLevel.InternetAccess => NetworkConnectivity.InternetAccess,
                WinRtConnectivityLevel.ConstrainedInternetAccess => NetworkConnectivity.ConstrainedInternetAccess,
                WinRtConnectivityLevel.LocalAccess => NetworkConnectivity.LocalAccess,
                WinRtConnectivityLevel.None => NetworkConnectivity.None,
                _ => NetworkConnectivity.Unknown,
            };
        }
        catch (Exception ex)
        {
            if (!_connectivityErrorLogged)
            {
                _logger.LogWarning(ex, "Windows connectivity status could not be read.");
                _connectivityErrorLogged = true;
            }

            return NetworkConnectivity.Unknown;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _interfacesReadAt = 0;
            _connectivityReadAt = 0;
        }
    }

    private static IReadOnlyList<string> Addresses(IPInterfaceProperties properties, AddressFamily family) =>
        properties.UnicastAddresses
            .Where(a => a.Address.AddressFamily == family)
            .OrderBy(a => a.Address.IsIPv6LinkLocal)
            .Select(a => a.Address.ToString())
            .ToArray();

    private static NetworkInterfaceKind ToKind(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.GigabitEthernet => NetworkInterfaceKind.Ethernet,
        NetworkInterfaceType.Wireless80211 => NetworkInterfaceKind.WiFi,
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => NetworkInterfaceKind.Cellular,
        _ => NetworkInterfaceKind.Other,
    };

    private sealed record TrackedInterface(NetworkInterface Nic, NetworkInterfaceMetrics Info);

    private readonly record struct Counters(long Received, long Sent, long Timestamp);
}
