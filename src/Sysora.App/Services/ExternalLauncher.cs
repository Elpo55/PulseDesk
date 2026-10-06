using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Sysora.App.Services;

/// <summary>
/// Opens folders in File Explorer and links in the default browser, only in response to an explicit
/// user click. Sysora itself never contacts any web address.
/// </summary>
public sealed class ExternalLauncher(ILogger<ExternalLauncher> logger)
{
    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true });
    }

    public void OpenUri(Uri uri)
    {
        if (uri.Scheme is not ("https" or "http"))
        {
            return;
        }

        Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void Start(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not open {Target}.", startInfo.FileName);
        }
    }
}
