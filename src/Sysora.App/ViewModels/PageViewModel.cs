using CommunityToolkit.Mvvm.ComponentModel;
using Sysora.App.Services;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;

namespace Sysora.App.ViewModels;

/// <summary>
/// Base class of page view models. A view model only listens to metric updates while its page is
/// displayed (<see cref="Activate"/> / <see cref="Deactivate"/>), so hidden pages cost nothing.
/// </summary>
public abstract class PageViewModel(UiMetricsHub hub) : ObservableObject
{
    protected UiMetricsHub Hub { get; } = hub;

    public bool IsActive { get; private set; }

    /// <summary>Called when the page is shown.</summary>
    public void Activate()
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        Hub.Updated += OnHubUpdated;
        Hub.HealthChanged += OnHubHealthChanged;
        OnActivated();
        Update(Hub.Snapshot, MetricKind.All);
        OnHealthChanged(Hub.Health);
    }

    /// <summary>Called when the page is left.</summary>
    public void Deactivate()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        Hub.Updated -= OnHubUpdated;
        Hub.HealthChanged -= OnHubHealthChanged;
        OnDeactivated();
    }

    /// <summary>Applies new metrics. Called on the UI thread with the metrics that changed.</summary>
    protected abstract void Update(SystemSnapshot snapshot, MetricKind updated);

    protected virtual void OnActivated()
    {
    }

    protected virtual void OnDeactivated()
    {
    }

    protected virtual void OnHealthChanged(HealthReport report)
    {
    }

    private void OnHubUpdated(object? sender, MetricKind updated) => Update(Hub.Snapshot, updated);

    private void OnHubHealthChanged(object? sender, HealthReport report) => OnHealthChanged(report);
}
