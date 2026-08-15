// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Application.Pricing;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Infrastructure.Usage;

namespace AiLimits.Infrastructure.Providers.Droid;

/// <summary>
/// Reads Factory's structured per-response token telemetry without retaining
/// any prompt, response, or raw log text.
/// </summary>
public sealed class FactoryLogTokenSource(string factoryHome) : ITokenUsageSource
{
    private const string LogMarker = "[Agent] Streaming result | Context:";
    private readonly ProjectIdentityResolver projectIdentityResolver = new();

    public string Id => "droid.factory-logs";

    public async IAsyncEnumerable<TokenUsageEvent> ReadAsync(
        ProviderAccount account,
        ScannerCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var logDirectory = Path.Combine(factoryHome, "logs");
        if (!SafeFileEnumeration.IsSafeDirectory(logDirectory))
            yield break;

        var sessions = await ReadSessionDirectoriesAsync(cancellationToken).ConfigureAwait(false);
        string[] files;
        try
        {
            files = Directory
                .EnumerateFiles(logDirectory, "droid-log-single.log*", SafeFileEnumeration.TopLevel)
                .OrderBy(SafeLastWriteTimeUtc)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StreamReader? reader;
            try
            {
                reader = new StreamReader(
                    new FileStream(
                        file,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        16384,
                        FileOptions.Asynchronous | FileOptions.SequentialScan
                    )
                );
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            using (reader)
            {
                while (true)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        break;
                    }

                    if (line is null)
                        break;
                    if (!TryParseLine(line, out var usage))
                        continue;
                    if (ScannerBoundary.AlreadyCovered(cursor, usage.OccurredAt))
                        continue;

                    var project = sessions.TryGetValue(usage.SessionId, out var workingDirectory)
                        ? projectIdentityResolver.Resolve(workingDirectory)
                        : ProjectIdentity.Unknown;
                    yield return new TokenUsageEvent(
                        account.Key,
                        new ServiceProviderId("factory"),
                        usage.ModelId,
                        usage.OccurredAt,
                        usage.InputTokens,
                        usage.OutputTokens,
                        usage.CacheReadTokens,
                        0,
                        usage.ReasoningTokens,
                        usage.SourceEventId,
                        project
                    );
                }
            }
        }
    }

    internal static bool TryParseLine(string? line, out FactoryLogUsage usage)
    {
        usage = default;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var marker = line.IndexOf(LogMarker, StringComparison.Ordinal);
        if (marker < 0)
            return false;
        var jsonStart = FindJsonStart(line, marker + LogMarker.Length);
        if (jsonStart < 0 || !TryReadTimestamp(line, out var occurredAt))
            return false;

        try
        {
            using var document = JsonDocument.Parse(line[jsonStart..]);
            return TryParseUsage(document.RootElement, occurredAt, out usage);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int FindJsonStart(string line, int startIndex) => line.IndexOf((char)123, startIndex);

    private static bool TryParseUsage(JsonElement root, DateTimeOffset occurredAt, out FactoryLogUsage usage)
    {
        usage = default;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Object
        )
            return false;

        var sessionId = ReadString(tags, "sessionId");
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        var input = ReadLong(root, "inputTokens");
        var cacheRead = ReadLong(root, "cacheReadInputTokens");
        var output = ReadLong(root, "outputTokens");
        var reasoning = ReadLong(root, "reasoningTokens");
        if (input == 0 && cacheRead == 0 && output == 0 && reasoning == 0)
            return false;

        var model = ReadString(tags, "modelId") ?? ReadString(root, "modelId") ?? "unknown";
        // Anthropic and OpenAI include reasoning inside outputTokens. Keep the
        // pricing engine's separate reasoning lane from billing those twice.
        if (reasoning > 0 && ModelVendorClassifier.GetPricingProviderId("droid", model) is "anthropic" or "openai")
            output = Math.Max(0, output - reasoning);

        var traceId = ReadString(tags, "traceId");
        var spanId = ReadString(tags, "spanId");
        usage = new FactoryLogUsage(
            occurredAt,
            sessionId,
            model,
            input,
            output,
            cacheRead,
            reasoning,
            CreateSourceEventId(
                traceId,
                spanId,
                occurredAt,
                sessionId,
                model,
                input,
                output,
                cacheRead,
                reasoning,
                ReadLong(root, "count"),
                ReadLong(root, "contextCount")
            )
        );
        return true;
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadSessionDirectoriesAsync(
        CancellationToken cancellationToken
    )
    {
        var sessions = new Dictionary<string, string>(StringComparer.Ordinal);
        var indexPath = Path.Combine(factoryHome, "sessions-index.json");
        if (!File.Exists(indexPath))
            return sessions;

        try
        {
            await using var stream = new FileStream(
                indexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16384,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            using var document = await JsonDocument
                .ParseAsync(stream, default, cancellationToken)
                .ConfigureAwait(false);
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("entries", out var entries)
            )
            {
                return sessions;
            }

            if (entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                    AddSessionDirectory(entry, sessions);
            }
            else if (entries.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in entries.EnumerateObject())
                    AddSessionDirectory(entry.Value, sessions);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A concurrently rewritten index must not stop other provider scans.
        }

        return sessions;
    }

    private static void AddSessionDirectory(JsonElement entry, IDictionary<string, string> sessions)
    {
        if (entry.ValueKind != JsonValueKind.Object)
            return;
        var sessionId = ReadString(entry, "sessionId") ?? ReadString(entry, "id");
        var workingDirectory = ReadString(entry, "cwd");
        if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(workingDirectory))
            sessions[sessionId] = workingDirectory;
    }

    private static bool TryReadTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var open = line.IndexOf('[');
        if (open < 0)
            return false;
        var close = line.IndexOf(']', open + 1);
        return close > open + 1
            && DateTimeOffset.TryParse(
                line.AsSpan(open + 1, close - open - 1),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp
            );
    }

    private static string CreateSourceEventId(
        string? traceId,
        string? spanId,
        DateTimeOffset occurredAt,
        string sessionId,
        string model,
        long input,
        long output,
        long cacheRead,
        long reasoning,
        long count,
        long contextCount
    )
    {
        var canonical = string.Join(
            '|',
            traceId ?? string.Empty,
            spanId ?? string.Empty,
            occurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            sessionId,
            model,
            input.ToString(CultureInfo.InvariantCulture),
            output.ToString(CultureInfo.InvariantCulture),
            cacheRead.ToString(CultureInfo.InvariantCulture),
            reasoning.ToString(CultureInfo.InvariantCulture),
            count.ToString(CultureInfo.InvariantCulture),
            contextCount.ToString(CultureInfo.InvariantCulture)
        );
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return $"factory:{EventIdPart(traceId, "no-trace")}:{EventIdPart(spanId, "no-span")}:{hash[..24]}";
    }

    private static string EventIdPart(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var safe = new string(
            value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').Take(64).ToArray()
        );
        return safe.Length == 0 ? fallback : safe;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static long ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return Math.Max(0, number);
        if (
            value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
        )
            return Math.Max(0, number);
        return 0;
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}

internal readonly record struct FactoryLogUsage(
    DateTimeOffset OccurredAt,
    string SessionId,
    string ModelId,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long ReasoningTokens,
    string SourceEventId
);
