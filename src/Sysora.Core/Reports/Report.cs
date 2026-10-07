using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sysora.Core.Analysis;
using Sysora.Core.Models;

namespace Sysora.Core.Reports;

/// <summary>What a report is about.</summary>
public enum ReportKind
{
    PcHealth,
    Diagnosis,
    WhyNow,
    Replay,
    GameSession,
    AppImpact,
    Alerts,
    Changes,
    Comparison,
    Timeline,
    Troubleshooting,
    LargeFiles,
}

/// <summary>Formats a report can be exported to.</summary>
public enum ReportFormat
{
    /// <summary>A readable, self-contained page (prints to PDF from any browser).</summary>
    Html,

    /// <summary>Structured data for other tools.</summary>
    Json,
}

/// <summary>The PC the report was made on (no user or computer name: reports are meant to be shared).</summary>
/// <param name="OperatingSystem">"Windows 11 Pro 24H2 (build 26100.4652)".</param>
/// <param name="Processor">Processor name.</param>
/// <param name="Memory">Installed or usable memory.</param>
/// <param name="Graphics">Graphics adapters.</param>
public sealed record ReportSystemInfo(string OperatingSystem, string? Processor, string? Memory, IReadOnlyList<string> Graphics)
{
    public static ReportSystemInfo? From(SystemInformation? information)
    {
        if (information is null)
        {
            return null;
        }

        var os = information.OperatingSystem;
        var version = os.DisplayVersion is { Length: > 0 } display ? $" {display}" : string.Empty;
        var memory = information.InstalledMemoryBytes ?? information.UsableMemoryBytes;
        return new ReportSystemInfo(
            $"{os.ProductName}{version} (build {os.Build}, {information.OsArchitecture})",
            information.Processor.Name,
            memory is { } bytes ? Formatting.MetricFormatter.Bytes(bytes) : null,
            information.Gpus.Select(g => g.Name).ToArray());
    }
}

/// <summary>A labeled value. <see cref="Value"/> and <see cref="Unit"/> carry the raw number when there is one.</summary>
public sealed record ReportFact(string Label, string Text)
{
    public double? Value { get; init; }

    public string? Unit { get; init; }

    /// <summary>Reference, period or source.</summary>
    public string? Detail { get; init; }
}

/// <summary>A table of formatted values.</summary>
public sealed record ReportTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>One part of a report.</summary>
public sealed record ReportSection(string Title)
{
    public string? Text { get; init; }

    public IReadOnlyList<ReportFact> Facts { get; init; } = [];

    /// <summary>Statements labeled observed, inferred or unknown.</summary>
    public IReadOnlyList<Finding> Findings { get; init; } = [];

    public IReadOnlyList<ReportTable> Tables { get; init; } = [];

    public IReadOnlyList<string> Items { get; init; } = [];
}

/// <summary>
/// A report of one Sysora function, ready to export. The sections are for reading; <see cref="Data"/> holds the
/// complete analysis as structured data (typed values, timestamps, enumerations as names) for other tools.
/// </summary>
public sealed record ReportDocument
{
    /// <summary>Identifies the format of the JSON export.</summary>
    public string Schema { get; init; } = "sysora.report/1";

    public required ReportKind Kind { get; init; }

    public required string Title { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public string AppVersion { get; init; } = AppInfo.InformationalVersion;

    public ReportSystemInfo? System { get; init; }

    /// <summary>Period analyzed.</summary>
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    /// <summary>The main conclusion in one or two sentences.</summary>
    public required string Summary { get; init; }

    public IReadOnlyList<ReportSection> Sections { get; init; } = [];

    /// <summary>What could not be measured or analyzed, and why.</summary>
    public IReadOnlyList<string> MissingData { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The underlying analysis, as structured data.</summary>
    public JsonElement? Data { get; init; }
}

/// <summary>Writes reports as JSON or as a self-contained HTML page.</summary>
public static class ReportWriter
{
    /// <summary>A file name for the report, e.g. "Sysora-Diagnosis-2026-10-07-1730.html".</summary>
    public static string FileName(ReportDocument report, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(report);
        var stamp = report.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
        return $"Sysora-{report.Kind}-{stamp}.{(format == ReportFormat.Html ? "html" : "json")}";
    }

    public static string Write(ReportDocument report, ReportFormat format) => format == ReportFormat.Html ? ToHtml(report) : ToJson(report);

    public static string ToJson(ReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, ReportJsonContext.Default.ReportDocument);
    }

    public static string ToHtml(ReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var html = new StringBuilder(16 * 1024);
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append(CultureInfo.InvariantCulture, $"<title>{E(report.Title)}</title>\n<style>{Style}</style>\n</head>\n<body>\n<main>\n");
        html.Append(CultureInfo.InvariantCulture, $"<header><p class=\"brand\">Sysora · Your PC, Explained.</p><h1>{E(report.Title)}</h1>");
        html.Append(CultureInfo.InvariantCulture, $"<p class=\"meta\">Generated {E(Time(report.GeneratedAt))} · Sysora {E(report.AppVersion)}");
        if (report.From is { } from && report.To is { } to)
        {
            html.Append(CultureInfo.InvariantCulture, $" · Period analyzed: {E(Time(from))} – {E(Time(to))}");
        }

        html.Append("</p></header>\n");
        html.Append(CultureInfo.InvariantCulture, $"<section class=\"summary\"><p>{E(report.Summary)}</p></section>\n");
        if (report.System is { } system)
        {
            html.Append("<section><h2>This PC</h2><dl>");
            Fact(html, "Windows", system.OperatingSystem);
            if (system.Processor is { } processor)
            {
                Fact(html, "Processor", processor);
            }

            if (system.Memory is { } memory)
            {
                Fact(html, "Memory", memory);
            }

            if (system.Graphics.Count > 0)
            {
                Fact(html, "Graphics", string.Join(", ", system.Graphics));
            }

            html.Append("</dl></section>\n");
        }

        foreach (var section in report.Sections)
        {
            html.Append(CultureInfo.InvariantCulture, $"<section><h2>{E(section.Title)}</h2>");
            if (section.Text is { Length: > 0 } text)
            {
                html.Append(CultureInfo.InvariantCulture, $"<p>{E(text)}</p>");
            }

            if (section.Facts.Count > 0)
            {
                html.Append("<dl>");
                foreach (var fact in section.Facts)
                {
                    Fact(html, fact.Label, fact.Text, fact.Detail);
                }

                html.Append("</dl>");
            }

            if (section.Findings.Count > 0)
            {
                html.Append("<ul class=\"findings\">");
                foreach (var finding in section.Findings)
                {
                    var css = finding.Basis.ToString().ToLowerInvariant();
                    html.Append(CultureInfo.InvariantCulture, $"<li><span class=\"basis {css}\">{E(finding.BasisText)}</span> <strong>{E(finding.Label)}</strong>: {E(finding.Text)}</li>");
                }

                html.Append("</ul>");
            }

            if (section.Items.Count > 0)
            {
                html.Append("<ul>");
                foreach (var item in section.Items)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<li>{E(item)}</li>");
                }

                html.Append("</ul>");
            }

            foreach (var table in section.Tables)
            {
                html.Append("<div class=\"table\"><table>");
                if (table.Title.Length > 0)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<caption>{E(table.Title)}</caption>");
                }

                html.Append("<thead><tr>");
                foreach (var column in table.Columns)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<th>{E(column)}</th>");
                }

                html.Append("</tr></thead><tbody>");
                foreach (var row in table.Rows)
                {
                    html.Append("<tr>");
                    foreach (var cell in row)
                    {
                        html.Append(CultureInfo.InvariantCulture, $"<td>{E(cell)}</td>");
                    }

                    html.Append("</tr>");
                }

                html.Append("</tbody></table></div>");
            }

            html.Append("</section>\n");
        }

        if (report.MissingData.Count > 0)
        {
            html.Append("<section><h2>Not available</h2><p>Sysora never replaces a missing measurement with an estimate.</p><ul>");
            foreach (var missing in report.MissingData)
            {
                html.Append(CultureInfo.InvariantCulture, $"<li>{E(missing)}</li>");
            }

            html.Append("</ul></section>\n");
        }

        if (report.Notes.Count > 0)
        {
            html.Append("<section class=\"notes\"><h2>Notes</h2><ul>");
            foreach (var note in report.Notes)
            {
                html.Append(CultureInfo.InvariantCulture, $"<li>{E(note)}</li>");
            }

            html.Append("</ul></section>\n");
        }

        html.Append("<footer>Made locally by Sysora: every value comes from this PC. Observed = measured; Inferred = deduced from measurements (not proven); Unknown = not observable.</footer>\n");
        html.Append("</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>The analysis as structured JSON for the report's <see cref="ReportDocument.Data"/>.</summary>
    public static JsonElement ToElement<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.SerializeToElement(value, type);

    private static void Fact(StringBuilder html, string label, string text, string? detail = null)
    {
        html.Append(CultureInfo.InvariantCulture, $"<dt>{E(label)}</dt><dd>{E(text)}");
        if (detail is { Length: > 0 })
        {
            html.Append(CultureInfo.InvariantCulture, $"<span class=\"detail\">{E(detail)}</span>");
        }

        html.Append("</dd>");
    }

    private static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    /// <summary>Escapes the characters that have a meaning in HTML; the page is UTF-8, so everything else stays readable.</summary>
    private static string E(string text)
    {
        if (text.AsSpan().IndexOfAny("&<>\"'") < 0)
        {
            return text;
        }

        var escaped = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            escaped.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }

        return escaped.ToString();
    }

    private const string Style = """
        :root{color-scheme:light dark;--fg:#1b1b1f;--muted:#5d5d66;--bg:#ffffff;--card:#f5f5f8;--line:#dcdce3;--accent:#3b5bdb;--obs:#2f9e44;--inf:#e67700;--unk:#868e96}
        @media (prefers-color-scheme:dark){:root{--fg:#ececf1;--muted:#a3a3ad;--bg:#16161a;--card:#202027;--line:#33333c;--accent:#7c95ff}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 "Segoe UI",system-ui,sans-serif}
        main{max-width:980px;margin:0 auto;padding:32px 20px 48px}h1{font-size:28px;margin:4px 0 6px}h2{font-size:18px;margin:0 0 10px}
        .brand{color:var(--accent);font-weight:600;margin:0}.meta{color:var(--muted);margin:0 0 20px;font-size:13px}
        section{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 18px;margin:0 0 14px}
        .summary{border-left:4px solid var(--accent)}.summary p{margin:0;font-size:16px}
        dl{display:grid;grid-template-columns:minmax(140px,30%) 1fr;gap:6px 16px;margin:0}dt{color:var(--muted)}dd{margin:0}
        .detail{display:block;color:var(--muted);font-size:13px}ul{margin:6px 0 0;padding-left:20px}li{margin:3px 0}
        .findings{list-style:none;padding-left:0}.basis{display:inline-block;font-size:12px;padding:1px 8px;border-radius:9px;color:#fff;margin-right:4px}
        .basis.observed{background:var(--obs)}.basis.inferred{background:var(--inf)}.basis.unknown{background:var(--unk)}
        .table{overflow-x:auto}table{border-collapse:collapse;width:100%;margin-top:8px;font-size:14px}caption{text-align:left;font-weight:600;padding:4px 0}
        th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted);font-weight:600}
        footer{color:var(--muted);font-size:12px;margin-top:20px}
        @media print{body{background:#fff;color:#000}section{break-inside:avoid;background:#fff}}
        """;
}
