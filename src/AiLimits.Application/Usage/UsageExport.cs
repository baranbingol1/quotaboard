// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AiLimits.Application.Usage;

/// <summary>
/// Deterministic serialization of the matching usage rows for export. Dates
/// are ISO, numbers are invariant-culture, and CSV fields are RFC 4180
/// quoted — the output opens cleanly in Excel and stays byte-stable for tests.
/// </summary>
public static class UsageExport
{
    private static readonly string[] CsvHeader =
    [
        "day",
        "provider",
        "harness",
        "project",
        "model",
        "tokens",
        "inputTokens",
        "outputTokens",
        "cacheReadTokens",
        "cacheWriteTokens",
        "reasoningTokens",
        "apiEquivalentCostUsd",
    ];

    public static string Csv(IReadOnlyList<UsageAnalyticsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var builder = new StringBuilder();
        AppendCsvLine(builder, CsvHeader);
        foreach (UsageAnalyticsRecord record in OrderForExport(records))
        {
            AppendCsvLine(
                builder,
                [
                    record.Day.ToString("O", CultureInfo.InvariantCulture),
                    record.ProviderLabel,
                    record.HarnessLabel,
                    record.ProjectLabel,
                    record.ModelLabel,
                    record.Tokens.ToString(CultureInfo.InvariantCulture),
                    record.InputTokens.ToString(CultureInfo.InvariantCulture),
                    record.OutputTokens.ToString(CultureInfo.InvariantCulture),
                    record.CacheReadTokens.ToString(CultureInfo.InvariantCulture),
                    record.CacheWriteTokens.ToString(CultureInfo.InvariantCulture),
                    record.ReasoningTokens.ToString(CultureInfo.InvariantCulture),
                    record.ApiEquivalentCostUsd?.ToString("0.####", CultureInfo.InvariantCulture) ?? "",
                ]
            );
        }
        return builder.ToString();
    }

    public static string Json(IReadOnlyList<UsageAnalyticsRecord> records, UsageAnalyticsQuery query)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(query);
        using var stream = new MemoryStream();
        using (
            var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, SkipValidation = false })
        )
        {
            writer.WriteStartObject();
            writer.WriteStartObject("window");
            writer.WriteString("from", query.From.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("through", query.Through.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WriteNumber("recordCount", records.Count);
            writer.WriteStartArray("records");
            foreach (UsageAnalyticsRecord record in OrderForExport(records))
            {
                writer.WriteStartObject();
                writer.WriteString("day", record.Day.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("provider", record.ProviderLabel);
                writer.WriteString("harness", record.HarnessLabel);
                writer.WriteString("project", record.ProjectLabel);
                writer.WriteString("model", record.ModelLabel);
                writer.WriteNumber("tokens", record.Tokens);
                writer.WriteNumber("inputTokens", record.InputTokens);
                writer.WriteNumber("outputTokens", record.OutputTokens);
                writer.WriteNumber("cacheReadTokens", record.CacheReadTokens);
                writer.WriteNumber("cacheWriteTokens", record.CacheWriteTokens);
                writer.WriteNumber("reasoningTokens", record.ReasoningTokens);
                if (record.ApiEquivalentCostUsd is { } cost)
                {
                    writer.WriteNumber("apiEquivalentCostUsd", cost);
                }
                else
                {
                    writer.WriteNull("apiEquivalentCostUsd");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // Stable ordering makes exports reproducible regardless of scanner order.
    private static IEnumerable<UsageAnalyticsRecord> OrderForExport(IReadOnlyList<UsageAnalyticsRecord> records) =>
        records
            .OrderBy(record => record.Day)
            .ThenBy(record => record.ProviderKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.ModelKey, StringComparer.OrdinalIgnoreCase);

    private static void AppendCsvLine(StringBuilder builder, string[] fields)
    {
        for (int index = 0; index < fields.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }
            AppendCsvField(builder, fields[index]);
        }
        builder.Append("\r\n");
    }

    /// <summary>
    /// Leading characters Excel and Sheets treat as the start of a formula. A
    /// project directory or model label beginning with one of these would be
    /// executed when the export is opened, so such a field gets an apostrophe
    /// in front of it before the RFC 4180 quoting below.
    /// </summary>
    private static readonly char[] FormulaLeadIns = ['=', '+', '-', '@', '\t', '\r'];

    private static void AppendCsvField(StringBuilder builder, string field)
    {
        string value = field.Length > 0 && Array.IndexOf(FormulaLeadIns, field[0]) >= 0 ? "'" + field : field;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            builder.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
        }
        else
        {
            builder.Append(value);
        }
    }
}
