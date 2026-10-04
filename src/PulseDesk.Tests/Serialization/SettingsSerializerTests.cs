using PulseDesk.Core.Settings;

namespace PulseDesk.Tests.Serialization;

public sealed class SettingsSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesAllValues()
    {
        var settings = new AppSettings
        {
            General = new GeneralSettings { Theme = ThemePreference.Dark, CloseBehavior = CloseBehavior.Quit, StartMinimized = true },
            Monitoring = new MonitoringSettings { CpuIntervalMs = 500, ProcessIntervalMs = 5000, ChartWindowSeconds = 300, GpuEnabled = false },
            Alerts = new AlertSettings { CpuWarningPercent = 70, CpuCriticalPercent = 90, DiskWarningPercent = 80 },
            Diagnostics = new DiagnosticsSettings { LogLevel = LogVerbosity.Debug },
            Window = new WindowSettings { Width = 1600, Height = 900, IsMaximized = true },
        };

        var json = SettingsSerializer.Serialize(settings);
        Assert.True(SettingsSerializer.TryDeserialize(json, out var restored, out var error), error);

        Assert.Equal(settings, restored);
    }

    [Fact]
    public void Serialize_WritesReadableJsonWithEnumNames()
    {
        var json = SettingsSerializer.Serialize(AppSettings.Default with
        {
            General = new GeneralSettings { Theme = ThemePreference.Light },
        });

        Assert.Contains("\"theme\": \"Light\"", json);
        Assert.Contains("\"closeBehavior\": \"MinimizeToTray\"", json);
        Assert.Contains("\"schemaVersion\": 1", json);
    }

    [Fact]
    public void Deserialize_MissingProperties_UseDefaults()
    {
        Assert.True(SettingsSerializer.TryDeserialize("""{ "general": { "theme": "Dark" } }""", out var settings, out _));

        Assert.Equal(ThemePreference.Dark, settings.General.Theme);
        Assert.Equal(CloseBehavior.MinimizeToTray, settings.General.CloseBehavior);
        Assert.Equal(new MonitoringSettings(), settings.Monitoring);
    }

    [Fact]
    public void Deserialize_UnknownPropertiesCommentsAndTrailingCommas_AreTolerated()
    {
        const string json = """
            {
              // written by a future version
              "futureFeature": { "enabled": true },
              "general": { "theme": "Light", },
            }
            """;

        Assert.True(SettingsSerializer.TryDeserialize(json, out var settings, out var error), error);
        Assert.Equal(ThemePreference.Light, settings.General.Theme);
    }

    [Fact]
    public void Deserialize_NullSections_AreReplacedByDefaults()
    {
        Assert.True(SettingsSerializer.TryDeserialize("""{ "monitoring": null, "alerts": null }""", out var settings, out _));

        Assert.NotNull(settings.Monitoring);
        Assert.Equal(new AlertSettings(), settings.Alerts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("{ not json")]
    [InlineData("""{ "general": { "theme": "Purple" } }""")]
    [InlineData("""{ "monitoring": { "cpuIntervalMs": "fast" } }""")]
    public void Deserialize_InvalidDocument_FailsWithMessage(string json)
    {
        Assert.False(SettingsSerializer.TryDeserialize(json, out var settings, out var error));
        Assert.Null(settings);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Deserialize_OutOfRangeValues_AreClamped()
    {
        const string json = """
            {
              "monitoring": { "cpuIntervalMs": 1, "processIntervalMs": 999999, "storageIntervalSeconds": 0, "chartWindowSeconds": 70, "maxSelfCpuPercent": 400 },
              "alerts": { "cpuWarningPercent": 99, "cpuCriticalPercent": 60, "diskWarningPercent": -5, "memoryCriticalPercent": 250 },
              "window": { "width": 10, "height": 100000 }
            }
            """;

        Assert.True(SettingsSerializer.TryDeserialize(json, out var settings, out _));

        Assert.Equal(SettingsValidator.MinIntervalMs, settings.Monitoring.CpuIntervalMs);
        Assert.Equal(30_000, settings.Monitoring.ProcessIntervalMs);
        Assert.Equal(5, settings.Monitoring.StorageIntervalSeconds);
        Assert.Equal(60, settings.Monitoring.ChartWindowSeconds);
        Assert.Equal(25, settings.Monitoring.MaxSelfCpuPercent);
        Assert.Equal(60, settings.Alerts.CpuWarningPercent);
        Assert.Equal(99, settings.Alerts.CpuCriticalPercent);
        Assert.Equal(1, settings.Alerts.DiskWarningPercent);
        Assert.Equal(100, settings.Alerts.MemoryCriticalPercent);
        Assert.Equal(640, settings.Window.Width);
        Assert.Equal(16_384, settings.Window.Height);
    }

    [Fact]
    public void Deserialize_UndefinedNumericEnum_FallsBackToDefault()
    {
        Assert.True(SettingsSerializer.TryDeserialize("""{ "general": { "theme": 42 } }""", out var settings, out _));

        Assert.Equal(ThemePreference.System, settings.General.Theme);
    }

    [Fact]
    public void Validator_ZeroCpuBudget_MeansUnlimited()
    {
        var normalized = SettingsValidator.Normalize(new AppSettings { Monitoring = new MonitoringSettings { MaxSelfCpuPercent = 0 } });

        Assert.Equal(0, normalized.Monitoring.MaxSelfCpuPercent);
    }

    [Fact]
    public void Validator_DefaultsAreAlreadyValid()
    {
        Assert.Equal(AppSettings.Default, SettingsValidator.Normalize(AppSettings.Default));
    }
}
