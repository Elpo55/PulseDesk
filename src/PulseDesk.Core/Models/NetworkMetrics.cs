namespace PulseDesk.Core.Models;

/// <summary>
/// Local network state. Built only from local operating-system data: PulseDesk never
/// contacts a remote host to produce these values.
/// </summary>
/// <param name="Connectivity">Connectivity level as determined by Windows.</param>
/// <param name="Interfaces">Network interfaces that are up (loopback and tunnels excluded).</param>
public sealed record NetworkMetrics(
    NetworkConnectivity Connectivity,
    IReadOnlyList<NetworkInterfaceMetrics> Interfaces)
{
    /// <summary>
    /// Receive rate in bits per second, summed over the interfaces that have a default gateway (the ones
    /// that reach other networks), or over all interfaces when none has one. Host-only virtual adapters
    /// (VirtualBox, Hyper-V) therefore do not inflate the figure.
    /// </summary>
    public double ReceiveBitsPerSecond => ExternalInterfaces.Sum(i => i.ReceiveBitsPerSecond ?? 0);

    /// <summary>Send rate in bits per second, computed like <see cref="ReceiveBitsPerSecond"/>.</summary>
    public double SendBitsPerSecond => ExternalInterfaces.Sum(i => i.SendBitsPerSecond ?? 0);

    private IEnumerable<NetworkInterfaceMetrics> ExternalInterfaces =>
        Interfaces.Any(i => i.HasGateway) ? Interfaces.Where(i => i.HasGateway) : Interfaces;

    /// <summary>The interface carrying the most traffic, or the first one when idle.</summary>
    public NetworkInterfaceMetrics? PrimaryInterface =>
        Interfaces.Count == 0
            ? null
            : Interfaces.MaxBy(i => (i.ReceiveBitsPerSecond ?? 0) + (i.SendBitsPerSecond ?? 0) + (i.HasGateway ? 1 : 0));
}

/// <summary>Connectivity level reported by Windows (Network Connectivity Status Indicator).</summary>
public enum NetworkConnectivity
{
    /// <summary>Windows did not report a connectivity level.</summary>
    Unknown,

    /// <summary>No network connection.</summary>
    None,

    /// <summary>Connected to a local network, but Windows reports no Internet access.</summary>
    LocalAccess,

    /// <summary>Internet access is limited (for example a captive portal).</summary>
    ConstrainedInternetAccess,

    /// <summary>Connected to the Internet.</summary>
    InternetAccess,
}

/// <summary>Kind of network interface.</summary>
public enum NetworkInterfaceKind
{
    Other,
    Ethernet,
    WiFi,
    Cellular,
}

/// <summary>
/// State and throughput of one network interface.
/// </summary>
/// <param name="Id">Interface identifier.</param>
/// <param name="Name">Friendly name (e.g. "Wi-Fi").</param>
/// <param name="Description">Adapter description (e.g. the device model).</param>
/// <param name="Kind">Interface type.</param>
public sealed record NetworkInterfaceMetrics(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceKind Kind)
{
    /// <summary>Negotiated link speed in bits per second, when known.</summary>
    public long? LinkSpeedBitsPerSecond { get; init; }

    /// <summary>Local IPv4 addresses.</summary>
    public IReadOnlyList<string> IPv4Addresses { get; init; } = [];

    /// <summary>Local IPv6 addresses.</summary>
    public IReadOnlyList<string> IPv6Addresses { get; init; } = [];

    /// <summary>True when the interface has a default gateway (i.e. likely carries Internet traffic).</summary>
    public bool HasGateway { get; init; }

    /// <summary>Bytes received since the interface came up.</summary>
    public long BytesReceived { get; init; }

    /// <summary>Bytes sent since the interface came up.</summary>
    public long BytesSent { get; init; }

    /// <summary>Receive rate in bits per second over the last interval (null on the first sample).</summary>
    public double? ReceiveBitsPerSecond { get; init; }

    /// <summary>Send rate in bits per second over the last interval (null on the first sample).</summary>
    public double? SendBitsPerSecond { get; init; }
}
