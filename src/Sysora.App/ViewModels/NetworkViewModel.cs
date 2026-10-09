using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sysora.App.Controls;
using Sysora.App.Services;
using Sysora.Core.Formatting;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Core.Settings;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

/// <summary>Local network status, throughput and interfaces.</summary>
public sealed partial class NetworkViewModel : PageViewModel
{
    private readonly SettingsService _settings;

    public NetworkViewModel(UiMetricsHub hub, SettingsService settings)
        : base(hub)
    {
        _settings = settings;
        ConnectionTitle = ConnectionDetail = Download = Upload = PrimaryName = PrimaryAddress = MetricFormatter.Pending;
        SelectedWindow = ChartWindowOption.FromSeconds(settings.Current.Monitoring.ChartWindowSeconds);
    }

    public ObservableCollection<NetworkInterfaceItemViewModel> Interfaces { get; } = [];

    public IReadOnlyList<ChartWindowOption> WindowOptions => ChartWindowOption.All;

    [ObservableProperty]
    public partial HealthStatus ConnectionStatus { get; set; }

    [ObservableProperty]
    public partial string ConnectionTitle { get; set; }

    [ObservableProperty]
    public partial string ConnectionDetail { get; set; }

    [ObservableProperty]
    public partial string Download { get; set; }

    [ObservableProperty]
    public partial string Upload { get; set; }

    [ObservableProperty]
    public partial string PrimaryName { get; set; }

    [ObservableProperty]
    public partial string PrimaryAddress { get; set; }

    [ObservableProperty]
    public partial TimeSeriesData? Chart { get; set; }

    [ObservableProperty]
    public partial bool IsDisabled { get; set; }

    [ObservableProperty]
    public partial ChartWindowOption SelectedWindow { get; set; }

    partial void OnSelectedWindowChanged(ChartWindowOption value)
    {
        if (value is null)
        {
            return;
        }

        _settings.Update(s => s with { Monitoring = s.Monitoring with { ChartWindowSeconds = value.Seconds } });
        if (IsActive)
        {
            Update(Hub.Snapshot, MetricKind.Network);
        }
    }

    protected override void OnActivated() =>
        SelectedWindow = ChartWindowOption.FromSeconds(_settings.Current.Monitoring.ChartWindowSeconds);

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        if ((updated & MetricKind.Network) == 0)
        {
            return;
        }

        IsDisabled = !_settings.Current.Monitoring.NetworkEnabled;
        if (snapshot.Network is not { } network)
        {
            var text = IsDisabled ? UiStrings.Dashboard_NetworkOff : Display.Format<NetworkMetrics>(snapshot, MetricKind.Network, null, _ => string.Empty);
            ConnectionStatus = HealthStatus.Unknown;
            ConnectionTitle = text;
            ConnectionDetail = string.Empty;
            Download = Upload = PrimaryName = PrimaryAddress = text;
            Interfaces.Clear();
            Chart = null;
            return;
        }

        (ConnectionStatus, ConnectionTitle, ConnectionDetail) = network.Connectivity switch
        {
            NetworkConnectivity.InternetAccess => (HealthStatus.Normal, UiStrings.Network_Connected, Strings.Event_AsReportedByWindows),
            NetworkConnectivity.ConstrainedInternetAccess => (HealthStatus.Warning, Strings.Diag_Net_LimitedTitle, UiStrings.Network_RestrictedDetail),
            NetworkConnectivity.LocalAccess => (HealthStatus.Warning, Strings.Indicator_NetworkNoInternet, UiStrings.Network_LocalOnly),
            NetworkConnectivity.None => (HealthStatus.Warning, Strings.Diag_Net_NoneTitle, UiStrings.Network_NoInterface),
            _ => (HealthStatus.Unknown, UiStrings.Network_StatusUnknown, UiStrings.Network_NoLevel),
        };

        Download = MetricFormatter.BitsPerSecond(network.ReceiveBitsPerSecond);
        Upload = MetricFormatter.BitsPerSecond(network.SendBitsPerSecond);
        var primary = network.PrimaryInterface;
        PrimaryName = primary?.Name ?? MetricFormatter.NotAvailable;
        var address = primary?.IPv4Addresses.FirstOrDefault() ?? primary?.IPv6Addresses.FirstOrDefault();
        PrimaryAddress = primary is null ? MetricFormatter.NotAvailable
            : address is null ? KindName(primary.Kind)
            : $"{address} · {KindName(primary.Kind)}";
        Chart = ChartFactory.BitRate(Hub.Monitor.History, SeriesKeys.NetworkReceive, SeriesKeys.NetworkSend, SelectedWindow, snapshot);

        var ordered = network.Interfaces.OrderByDescending(i => i.HasGateway).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        CollectionSync.Resize(Interfaces, ordered.Length, _ => new NetworkInterfaceItemViewModel(), (item, i) => item.Update(ordered[i]));
    }

    internal static string KindName(NetworkInterfaceKind kind) => kind switch
    {
        NetworkInterfaceKind.Ethernet => "Ethernet",
        NetworkInterfaceKind.WiFi => "Wi-Fi",
        NetworkInterfaceKind.Cellular => UiStrings.Network_Cellular,
        _ => Strings.LargeFiles_Cat_Other,
    };
}

/// <summary>One network interface card.</summary>
public sealed partial class NetworkInterfaceItemViewModel : ObservableObject
{
    public NetworkInterfaceItemViewModel()
    {
        Name = Description = Kind = Glyph = Speed = IPv4 = IPv6 = Receive = Send = Received = Sent = string.Empty;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial string Kind { get; set; }

    [ObservableProperty]
    public partial string Glyph { get; set; }

    [ObservableProperty]
    public partial bool HasGateway { get; set; }

    [ObservableProperty]
    public partial string Speed { get; set; }

    [ObservableProperty]
    public partial string IPv4 { get; set; }

    [ObservableProperty]
    public partial string IPv6 { get; set; }

    [ObservableProperty]
    public partial string Receive { get; set; }

    [ObservableProperty]
    public partial string Send { get; set; }

    [ObservableProperty]
    public partial string Received { get; set; }

    [ObservableProperty]
    public partial string Sent { get; set; }

    public void Update(NetworkInterfaceMetrics network)
    {
        Name = network.Name;
        Description = network.Description;
        Kind = NetworkViewModel.KindName(network.Kind) + (network.HasGateway ? " · " + UiStrings.Network_DefaultRoute : string.Empty);
        Glyph = network.Kind == NetworkInterfaceKind.WiFi ? "" : "";
        HasGateway = network.HasGateway;
        Speed = MetricFormatter.BitsPerSecond(network.LinkSpeedBitsPerSecond);
        IPv4 = network.IPv4Addresses.Count > 0 ? string.Join(", ", network.IPv4Addresses) : UiStrings.Network_NoAddress;
        IPv6 = network.IPv6Addresses.Count > 0 ? network.IPv6Addresses[0] : UiStrings.Network_NoAddress;
        Receive = network.ReceiveBitsPerSecond is { } rx ? MetricFormatter.BitsPerSecond(rx) : MetricFormatter.Pending;
        Send = network.SendBitsPerSecond is { } tx ? MetricFormatter.BitsPerSecond(tx) : MetricFormatter.Pending;
        Received = MetricFormatter.Bytes((ulong)Math.Max(network.BytesReceived, 0));
        Sent = MetricFormatter.Bytes((ulong)Math.Max(network.BytesSent, 0));
    }
}
