using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PulseDesk.Core.Interfaces;

namespace PulseDesk.Infrastructure.Gaming;

/// <summary>
/// The games Windows recognizes for the current user: the executables Game Bar matched to a known game
/// (<c>HKCU\System\GameConfigStore\Children\*\MatchedExeFullPath</c>). Read-only; PulseDesk never changes these settings.
/// </summary>
public sealed class WindowsGameLibrary(ILogger<WindowsGameLibrary> logger) : IGameLibrary
{
    private const string ChildrenKey = @"System\GameConfigStore\Children";
    private const string MatchedPathValue = "MatchedExeFullPath";
    private const int MaxCachedProductNames = 512;

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string?> _productNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _recognized = Empty;
    private bool _errorLogged;

    public IReadOnlySet<string> RecognizedGames => Volatile.Read(ref _recognized);

    public bool Refresh()
    {
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(ChildrenKey);
            foreach (var name in children?.GetSubKeyNames() ?? [])
            {
                using var child = children!.OpenSubKey(name);
                if (child?.GetValue(MatchedPathValue) is string { Length: > 0 } path)
                {
                    games.Add(path.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            if (!_errorLogged)
            {
                _errorLogged = true;
                logger.LogWarning(ex, "The list of games recognized by Windows could not be read.");
            }

            return false;
        }

        if (games.SetEquals(RecognizedGames))
        {
            return false;
        }

        Volatile.Write(ref _recognized, games);
        return true;
    }

    public string? GetProductName(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (_productNames.TryGetValue(executablePath, out var cached))
        {
            return cached;
        }

        string? name = null;
        try
        {
            name = FileVersionInfo.GetVersionInfo(executablePath).ProductName?.Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No version information: the name comes from the folder or the executable.
        }

        if (_productNames.Count >= MaxCachedProductNames)
        {
            _productNames.Clear();
        }

        name = string.IsNullOrEmpty(name) ? null : name;
        _productNames[executablePath] = name;
        return name;
    }
}
