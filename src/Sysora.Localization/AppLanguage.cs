using System.Globalization;

namespace Sysora.Localization;

/// <summary>A language Sysora is translated into.</summary>
/// <param name="Code">Two-letter language code stored in the settings ("en", "fr").</param>
/// <param name="NativeName">Name of the language in that language ("English", "Français"), so anyone can find their own.</param>
public sealed record LanguageInfo(string Code, string NativeName);

/// <summary>
/// The languages Sysora is translated into, and the language this process uses. The language is chosen once, at
/// startup, before any text is produced: every text (interface, analysis, alerts, reports) then comes out in that
/// language, and a missing translation falls back to English.
/// </summary>
/// <remarks>
/// Only the interface language changes: dates, times and numbers keep following the regional format chosen in Windows.
/// </remarks>
public static class AppLanguage
{
    /// <summary>Preference value meaning "the language of Windows when Sysora is translated into it, otherwise English".</summary>
    public const string System = "";

    /// <summary>The fallback language: the text of the resources without a language suffix.</summary>
    public const string Fallback = "en";

    /// <summary>Supported languages, fallback first.</summary>
    public static IReadOnlyList<LanguageInfo> Supported { get; } =
    [
        new("en", "English"),
        new("fr", "Français"),
    ];

    /// <summary>The language of this process, as applied by <see cref="Apply"/> (English until then).</summary>
    public static CultureInfo Current { get; private set; } = CultureInfo.GetCultureInfo(Fallback);

    /// <summary>
    /// Brings a stored preference back to a supported value: a supported language code (any case, region ignored, so
    /// "FR-ca" becomes "fr"), or <see cref="System"/> for anything else.
    /// </summary>
    public static string Normalize(string? preference)
    {
        if (string.IsNullOrWhiteSpace(preference))
        {
            return System;
        }

        var code = preference.Trim();
        var separator = code.IndexOfAny(['-', '_']);
        if (separator > 0)
        {
            code = code[..separator];
        }

        return Find(code)?.Code ?? System;
    }

    /// <summary>
    /// The language to use: the preference when it is a supported language, otherwise the Windows display language when
    /// Sysora is translated into it, otherwise English.
    /// </summary>
    /// <param name="preference">The stored preference (<see cref="System"/> to follow Windows).</param>
    /// <param name="systemUiCulture">The display language of Windows (<see cref="CultureInfo.InstalledUICulture"/>).</param>
    public static CultureInfo Resolve(string? preference, CultureInfo systemUiCulture)
    {
        ArgumentNullException.ThrowIfNull(systemUiCulture);
        var code = Normalize(preference);
        if (code == System)
        {
            code = Find(systemUiCulture.TwoLetterISOLanguageName)?.Code ?? Fallback;
        }

        return CultureInfo.GetCultureInfo(code);
    }

    /// <summary>
    /// Makes <paramref name="culture"/> the interface language of the whole process: every thread, including the ones
    /// started later, and every resource lookup. Call it once, before any text is produced.
    /// </summary>
    public static void Apply(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        Current = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Strings.Culture = culture;
        UiStrings.Culture = culture;
    }

    /// <summary>The supported language with this two-letter code, or null.</summary>
    public static LanguageInfo? Find(string? code) =>
        Supported.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
}
