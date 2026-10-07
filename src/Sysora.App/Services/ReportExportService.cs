using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Sysora.App.ViewModels;
using Sysora.Core.Interfaces;
using Sysora.Core.Reports;
using Windows.Storage.Pickers;

namespace Sysora.App.Services;

/// <summary>
/// Opens the Windows file pickers for the pages (save a report, choose a folder to scan). View models never touch the
/// window; they ask this service.
/// </summary>
public sealed class PickerService(ILogger<PickerService> logger)
{
    private Window? _window;

    public void Attach(Window window) => _window = window;

    /// <summary>Asks where to save a file; null when cancelled or when the picker cannot be shown.</summary>
    public async Task<string?> PickSaveFileAsync(string suggestedName, IReadOnlyList<(string Label, string Extension)> types)
    {
        if (_window is null)
        {
            return null;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
            };
            foreach (var (label, extension) in types)
            {
                picker.FileTypeChoices.Add(label, [extension]);
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch (COMException ex)
        {
            // The pickers are not available when Sysora runs as administrator.
            logger.LogWarning(ex, "The save picker could not be shown.");
            return null;
        }
    }

    /// <summary>Asks for a folder; null when cancelled or when the picker cannot be shown.</summary>
    public async Task<string?> PickFolderAsync()
    {
        if (_window is null)
        {
            return null;
        }

        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (COMException ex)
        {
            logger.LogWarning(ex, "The folder picker could not be shown.");
            return null;
        }
    }
}

/// <summary>
/// Exports reports: asks where to save (HTML or JSON, chosen with the file type), writes the file off the UI thread and
/// says where it went. Reports are written locally only; nothing is sent anywhere.
/// </summary>
public sealed class ReportExportService(PickerService pickers, ISystemInfoProvider systemInfo, ShellViewModel shell, ILogger<ReportExportService> logger)
{
    private ReportSystemInfo? _system;

    /// <summary>The PC description shown at the top of reports (read once).</summary>
    public async Task<ReportSystemInfo?> SystemAsync()
    {
        if (_system is not null)
        {
            return _system;
        }

        try
        {
            _system = ReportSystemInfo.From(await systemInfo.GetAsync(CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug(ex, "System information is not available for the report.");
        }

        return _system;
    }

    /// <summary>Builds the report with the PC description, asks where to save it and writes it.</summary>
    public async Task ExportAsync(Func<ReportSystemInfo?, ReportDocument> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        ReportDocument report;
        try
        {
            var system = await SystemAsync();
            report = await Task.Run(() => build(system));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "The report could not be prepared.");
            shell.ShowMessage("The report could not be prepared. See the log for details.", null, isError: true);
            return;
        }

        var path = await pickers.PickSaveFileAsync(ReportWriter.FileName(report, ReportFormat.Html), [("Report (HTML page)", ".html"), ("Structured data (JSON)", ".json")]);
        if (path is null)
        {
            return;
        }

        try
        {
            var format = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ReportFormat.Json : ReportFormat.Html;
            var content = await Task.Run(() => ReportWriter.Write(report, format));
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            shell.ShowMessage($"Report saved: {Path.GetFileName(path)}", Path.GetDirectoryName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The report could not be saved to {Path}.", path);
            shell.ShowMessage($"The report could not be saved: {ex.Message}", null, isError: true);
        }
    }
}
