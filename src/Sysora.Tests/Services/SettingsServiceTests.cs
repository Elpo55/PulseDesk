using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Sysora.Core.Interfaces;
using Sysora.Core.Settings;

namespace Sysora.Tests.Services;

public sealed class SettingsServiceTests
{
    [Fact]
    public async Task Load_NoFile_UsesDefaults()
    {
        var store = new InMemorySettingsStore();
        var service = new SettingsService(store, NullLogger<SettingsService>.Instance);

        await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AppSettings.Default, service.Current);
    }

    [Fact]
    public async Task Load_ValidFile_AppliesIt()
    {
        var store = new InMemorySettingsStore { Content = """{ "general": { "theme": "Dark" } }""" };
        var service = new SettingsService(store, NullLogger<SettingsService>.Instance);

        await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ThemePreference.Dark, service.Current.General.Theme);
    }

    [Fact]
    public async Task Load_CorruptFile_IsQuarantinedAndDefaultsUsed()
    {
        var store = new InMemorySettingsStore { Content = "{ broken" };
        var service = new SettingsService(store, NullLogger<SettingsService>.Instance);

        await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AppSettings.Default, service.Current);
        Assert.True(store.Quarantined);
    }

    [Fact]
    public async Task Load_UnreadableFile_UsesDefaultsWithoutThrowing()
    {
        var store = new InMemorySettingsStore { ReadException = new UnauthorizedAccessException() };
        var service = new SettingsService(store, NullLogger<SettingsService>.Instance);

        await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AppSettings.Default, service.Current);
    }

    [Fact]
    public void Update_RaisesChangedWithPreviousAndCurrent()
    {
        var service = new SettingsService(new InMemorySettingsStore(), NullLogger<SettingsService>.Instance, new FakeTimeProvider());
        SettingsChangedEventArgs? raised = null;
        service.Changed += (_, e) => raised = e;

        service.Update(s => s with { General = s.General with { Theme = ThemePreference.Light } });

        Assert.NotNull(raised);
        Assert.Equal(ThemePreference.System, raised.Previous.General.Theme);
        Assert.Equal(ThemePreference.Light, raised.Current.General.Theme);
        Assert.Same(service.Current, raised.Current);
    }

    [Fact]
    public void Update_WithoutEffectiveChange_DoesNotRaise()
    {
        var service = new SettingsService(new InMemorySettingsStore(), NullLogger<SettingsService>.Instance, new FakeTimeProvider());
        var raised = 0;
        service.Changed += (_, _) => raised++;

        service.Update(s => s with { General = s.General with { Theme = ThemePreference.System } });

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Update_ValidatesValues()
    {
        var service = new SettingsService(new InMemorySettingsStore(), NullLogger<SettingsService>.Instance, new FakeTimeProvider());

        service.Update(s => s with { Monitoring = s.Monitoring with { CpuIntervalMs = 10 } });

        Assert.Equal(SettingsValidator.MinIntervalMs, service.Current.Monitoring.CpuIntervalMs);
    }

    [Fact]
    public async Task Update_IsSavedOnceAfterDelay_EvenWhenChangedRepeatedly()
    {
        var time = new FakeTimeProvider();
        var store = new InMemorySettingsStore();
        var service = new SettingsService(store, NullLogger<SettingsService>.Instance, time);

        for (var i = 1; i <= 5; i++)
        {
            service.Update(s => s with { Window = s.Window with { Width = 1000 + i } });
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(0, store.WriteCount);

        time.Advance(SettingsService.SaveDelay);
        await store.WaitForWritesAsync(1);

        Assert.Equal(1, store.WriteCount);
        Assert.Contains("\"width\": 1005", store.Content);
    }

    [Fact]
    public async Task Flush_WritesPendingChangesImmediately()
    {
        var store = new InMemorySettingsStore();
        var service = new SettingsService(store, NullLogger<SettingsService>.Instance, new FakeTimeProvider());
        service.Update(s => s with { General = s.General with { CloseBehavior = CloseBehavior.Quit } });

        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.WriteCount);
        Assert.True(SettingsSerializer.TryDeserialize(store.Content!, out var saved, out _));
        Assert.Equal(CloseBehavior.Quit, saved.General.CloseBehavior);
    }

    [Fact]
    public async Task Flush_WithoutChanges_DoesNotWrite()
    {
        var store = new InMemorySettingsStore();
        var service = new SettingsService(store, NullLogger<SettingsService>.Instance, new FakeTimeProvider());

        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public async Task FailedWrite_IsRetriedOnNextFlush()
    {
        var store = new InMemorySettingsStore { WriteException = new IOException("disk full") };
        var service = new SettingsService(store, NullLogger<SettingsService>.Instance, new FakeTimeProvider());
        service.Update(s => s with { General = s.General with { StartMinimized = true } });

        await service.FlushAsync(TestContext.Current.CancellationToken);
        store.WriteException = null;
        await service.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.WriteCount);
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        private readonly SemaphoreSlim _written = new(0);

        public string? Content { get; set; }

        public int WriteCount { get; private set; }

        public bool Quarantined { get; private set; }

        public Exception? ReadException { get; set; }

        public Exception? WriteException { get; set; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken) =>
            ReadException is { } ex ? Task.FromException<string?>(ex) : Task.FromResult(Content);

        public Task WriteAsync(string content, CancellationToken cancellationToken)
        {
            if (WriteException is { } ex)
            {
                return Task.FromException(ex);
            }

            Content = content;
            WriteCount++;
            _written.Release();
            return Task.CompletedTask;
        }

        public Task QuarantineAsync(CancellationToken cancellationToken)
        {
            Quarantined = true;
            Content = null;
            return Task.CompletedTask;
        }

        public async Task WaitForWritesAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(await _written.WaitAsync(TimeSpan.FromSeconds(5)), "Timed out waiting for a settings write.");
            }
        }
    }
}
