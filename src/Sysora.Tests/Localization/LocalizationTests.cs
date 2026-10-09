using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Sysora.Core.Settings;
using Sysora.Localization;

namespace Sysora.Tests.Localization;

public sealed partial class LocalizationTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    public static TheoryData<string> ResourceSets => new() { nameof(Strings), nameof(UiStrings) };

    [Theory]
    [MemberData(nameof(ResourceSets))]
    public void EveryTextHasAFrenchTranslation(string set)
    {
        var english = Read(Manager(set), CultureInfo.InvariantCulture);
        var french = Read(Manager(set), French);

        var missing = english.Keys.Where(k => !french.ContainsKey(k) || string.IsNullOrWhiteSpace(french[k])).ToArray();
        Assert.True(missing.Length == 0, $"{set}: no French text for {string.Join(", ", missing)}");
        Assert.Empty(french.Keys.Except(english.Keys));
    }

    [Theory]
    [MemberData(nameof(ResourceSets))]
    public void TranslationsUseOnlyThePlaceholdersTheCodeProvides(string set)
    {
        var english = Read(Manager(set), CultureInfo.InvariantCulture);
        var french = Read(Manager(set), French);
        foreach (var (key, text) in english)
        {
            var count = MaxPlaceholder(text) + 1;
            var args = Enumerable.Range(0, count).Select(i => (object)i).ToArray();

            // The code fills the English placeholders: a translation must format with the same arguments.
            _ = string.Format(CultureInfo.InvariantCulture, text, args);
            if (french.TryGetValue(key, out var translation))
            {
                Assert.True(MaxPlaceholder(translation) < count, $"{set}.{key}: the French text uses a placeholder the code does not provide.");
                _ = string.Format(French, translation, args);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ResourceSets))]
    public void NoTextShowsItsResourceName(string set)
    {
        foreach (var culture in new[] { CultureInfo.InvariantCulture, French })
        {
            foreach (var (key, text) in Read(Manager(set), culture))
            {
                Assert.False(string.IsNullOrWhiteSpace(text), $"{set}.{key} is empty ({culture.Name}).");
                Assert.NotEqual(key, text);
                Assert.DoesNotContain("⟦", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void FrenchTextsAreLoadedFromTheSatelliteAssembly()
    {
        Assert.Equal("Not available", Strings.ResourceManager.GetString(nameof(Strings.Common_NotAvailable), CultureInfo.InvariantCulture));
        Assert.Equal("Non disponible", Strings.ResourceManager.GetString(nameof(Strings.Common_NotAvailable), French));
        Assert.Equal("Paramètres", UiStrings.ResourceManager.GetString(nameof(UiStrings.Settings_Settings), French));
    }

    [Fact]
    public void AMissingTranslationFallsBackToEnglish()
    {
        // A language Sysora is not translated into resolves every text to the English one.
        var german = CultureInfo.GetCultureInfo("de");
        Assert.Equal("Not available", Strings.ResourceManager.GetString(nameof(Strings.Common_NotAvailable), german));
    }

    [Theory]
    [InlineData("", "fr-FR", "fr")]
    [InlineData("", "fr-CA", "fr")]
    [InlineData("", "de-DE", "en")]
    [InlineData("", "en-US", "en")]
    [InlineData("en", "fr-FR", "en")]
    [InlineData("fr", "en-US", "fr")]
    [InlineData("FR-be", "en-US", "fr")]
    [InlineData("xx", "fr-FR", "fr")]
    [InlineData(null, "ja-JP", "en")]
    public void TheLanguageFollowsThePreferenceThenWindowsThenEnglish(string? preference, string windows, string expected)
    {
        Assert.Equal(expected, AppLanguage.Resolve(preference, CultureInfo.GetCultureInfo(windows)).Name);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData("fr", "fr")]
    [InlineData("fr-FR", "fr")]
    [InlineData("EN", "en")]
    [InlineData("klingon", "")]
    public void APreferenceIsBroughtBackToASupportedValue(string? preference, string expected)
    {
        Assert.Equal(expected, AppLanguage.Normalize(preference));
    }

    [Fact]
    public void TheDefaultLanguageFollowsWindows()
    {
        Assert.Equal(AppLanguage.System, new AppSettings().General.Language);
    }

    [Fact]
    public void TheChosenLanguageIsSavedAndReadBack()
    {
        var settings = new AppSettings() with { General = new GeneralSettings { Language = "fr" } };
        Assert.True(SettingsSerializer.TryDeserialize(SettingsSerializer.Serialize(settings), out var read, out _));
        Assert.Equal("fr", read.General.Language);
    }

    [Fact]
    public void AnUnknownStoredLanguageFallsBackToWindows()
    {
        Assert.True(SettingsSerializer.TryDeserialize("""{ "general": { "language": "zz-ZZ" } }""", out var read, out _));
        Assert.Equal(AppLanguage.System, read.General.Language);
    }

    [Fact]
    public void SettingsWrittenBeforeLanguagesExistedFollowWindows()
    {
        Assert.True(SettingsSerializer.TryDeserialize("""{ "general": { "theme": "Dark" } }""", out var read, out _));
        Assert.Equal(AppLanguage.System, read.General.Language);
        Assert.Equal(ThemePreference.Dark, read.General.Theme);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, true, true)]
    [InlineData(2, false, false)]
    public void PluralsFollowTheRulesOfEachLanguage(long count, bool englishSingular, bool frenchSingular)
    {
        Assert.Equal(englishSingular, Text.IsSingular(count, CultureInfo.GetCultureInfo("en")));
        Assert.Equal(frenchSingular, Text.IsSingular(count, French));
    }

    [Fact]
    public void ListsAreJoinedWithTheLastSeparator()
    {
        Assert.Equal(string.Empty, Text.List([]));
        Assert.Equal("a", Text.List(["a"]));
        Assert.Equal("a and b", Text.List(["a", "b"]));
        Assert.Equal("a, b and c", Text.List(["a", "b", "c"]));
    }

    [Fact]
    public void PagesContainNoHardCodedText()
    {
        var views = Path.Combine(RepositoryRoot(), "src", "Sysora.App");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(views, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                foreach (Match match in LiteralTextAttribute().Matches(line))
                {
                    var value = match.Groups["value"].Value;
                    if (value is not "Sysora" && LetterPattern().IsMatch(value))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{lineNumber}: {match.Value}");
                    }
                }

                if (line.Contains("<x:String>", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{lineNumber}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Text to translate in XAML:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static ResourceManager Manager(string set) => set == nameof(Strings) ? Strings.ResourceManager : UiStrings.ResourceManager;

    /// <summary>The texts of one language only (no fallback to the parent language).</summary>
    private static Dictionary<string, string> Read(ResourceManager manager, CultureInfo culture)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        foreach (DictionaryEntry entry in set)
        {
            texts[(string)entry.Key] = (string)entry.Value!;
        }

        return texts;
    }

    private static int MaxPlaceholder(string text) =>
        PlaceholderPattern().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(-1).Max();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sysora.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"[A-Za-z]{3,}")]
    private static partial Regex LetterPattern();

    [GeneratedRegex(@"(?<![\w.:])(Text|AutomationProperties\.Name|Label|Header|ToolTipService\.ToolTip|Content|Description|Hint|Title|Message|PlaceholderText|OnContent|OffContent|PrimaryButtonText|SecondaryButtonText|CloseButtonText)=""(?<value>[^""{][^""]*)""")]
    private static partial Regex LiteralTextAttribute();
}
