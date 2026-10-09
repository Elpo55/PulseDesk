using Sysora.Core.Interfaces;
using Sysora.Core.Simulation;

namespace Sysora.Tests.Simulation;

public sealed class DemoModeTests
{
    [Fact]
    public async Task DemoSettings_StartFromTheStoredSettings()
    {
        var stored = new RecordingStore { Content = "{\"stored\":true}" };
        var store = new DemoSettingsStore(stored);

        Assert.Equal("{\"stored\":true}", await store.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DemoSettings_KeepChangesInMemory()
    {
        var stored = new RecordingStore { Content = "{\"stored\":true}" };
        var store = new DemoSettingsStore(stored);

        await store.WriteAsync("{\"demo\":true}", TestContext.Current.CancellationToken);

        Assert.Equal("{\"demo\":true}", await store.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, stored.WriteCount);
        Assert.Equal("{\"stored\":true}", stored.Content);
    }

    [Fact]
    public async Task DemoSettings_NeverSetTheStoredFileAside()
    {
        var stored = new RecordingStore { Content = "not json" };
        var store = new DemoSettingsStore(stored);

        await store.QuarantineAsync(TestContext.Current.CancellationToken);

        Assert.False(stored.Quarantined);
        Assert.Null(await store.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DemoSettings_UnreadableStoredFile_StartsFromDefaults()
    {
        var store = new DemoSettingsStore(new RecordingStore { ReadException = new IOException("locked") });

        Assert.Null(await store.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DemoStartupRegistration_ChangesOnlyTheSession()
    {
        var registration = new SimulatedStartupRegistration();

        Assert.Equal(StartupState.Disabled, registration.GetState());
        Assert.Equal(StartupState.Enabled, registration.SetEnabled(true));
        Assert.Equal(StartupState.Enabled, registration.GetState());
        Assert.Equal(StartupState.Disabled, registration.SetEnabled(false));
    }

    private sealed class RecordingStore : ISettingsStore
    {
        public string? Content { get; set; }

        public Exception? ReadException { get; set; }

        public int WriteCount { get; private set; }

        public bool Quarantined { get; private set; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken) =>
            ReadException is { } ex ? Task.FromException<string?>(ex) : Task.FromResult(Content);

        public Task WriteAsync(string content, CancellationToken cancellationToken)
        {
            WriteCount++;
            Content = content;
            return Task.CompletedTask;
        }

        public Task QuarantineAsync(CancellationToken cancellationToken)
        {
            Quarantined = true;
            return Task.CompletedTask;
        }
    }
}
