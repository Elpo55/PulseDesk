using System.Globalization;

namespace Sysora.Localization;

/// <summary>Builds sentences from translated templates.</summary>
public static class Text
{
    /// <summary>
    /// Fills a translated template ("{0} started using a lot of CPU") with values formatted for the regional format
    /// chosen in Windows.
    /// </summary>
    public static string Format(string template, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, template, args);

    /// <summary>
    /// Picks the singular or plural template for <paramref name="count"/>, following the rules of the interface language
    /// (English: singular for 1 only; French: singular for 0 and 1), and fills <c>{0}</c> with the count.
    /// </summary>
    /// <param name="count">The number of items.</param>
    /// <param name="one">Singular template, e.g. "{0} alert".</param>
    /// <param name="other">Plural template, e.g. "{0} alerts".</param>
    /// <param name="args">Values for <c>{1}</c> and later placeholders.</param>
    public static string Plural(long count, string one, string other, params object?[] args)
    {
        var template = IsSingular(count) ? one : other;
        if (args.Length == 0)
        {
            return string.Format(CultureInfo.CurrentCulture, template, count);
        }

        var all = new object?[args.Length + 1];
        all[0] = count;
        args.CopyTo(all, 1);
        return string.Format(CultureInfo.CurrentCulture, template, all);
    }

    /// <summary>Whether <paramref name="count"/> takes the singular in the interface language.</summary>
    public static bool IsSingular(long count) => IsSingular(count, AppLanguage.Current);

    /// <summary>Whether <paramref name="count"/> takes the singular in <paramref name="language"/>.</summary>
    public static bool IsSingular(long count, CultureInfo language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return language.TwoLetterISOLanguageName == "fr" ? count is 0 or 1 or -1 : count is 1 or -1;
    }

    /// <summary>Joins items as a readable list: "a, b and c" (", " and " and " are translated).</summary>
    public static string List(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        return list.Count switch
        {
            0 => string.Empty,
            1 => list[0],
            _ => string.Join(Strings.List_Separator, list.Take(list.Count - 1)) + Strings.List_LastSeparator + list[^1],
        };
    }
}
