using System.Globalization;
using System.Text;
using Sysora.Core.Interfaces;

namespace Sysora.Infrastructure.Settings;

/// <summary>
/// Stores settings in <c>%LOCALAPPDATA%\Sysora\settings.json</c>. Writes go to a temporary file that
/// then replaces the real one, so a crash or power loss never leaves a truncated settings file.
/// </summary>
public sealed class JsonFileSettingsStore(SysoraPaths paths) : ISettingsStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        var file = paths.SettingsFile;
        return File.Exists(file)
            ? await File.ReadAllTextAsync(file, Utf8NoBom, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task WriteAsync(string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var file = paths.SettingsFile;
        var temporary = file + ".tmp";
        await File.WriteAllTextAsync(temporary, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, file, overwrite: true);
    }

    public Task QuarantineAsync(CancellationToken cancellationToken)
    {
        var file = paths.SettingsFile;
        if (File.Exists(file))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Move(file, Path.Combine(paths.DataDirectory, $"settings.invalid-{stamp}.json"), overwrite: true);
        }

        return Task.CompletedTask;
    }
}
