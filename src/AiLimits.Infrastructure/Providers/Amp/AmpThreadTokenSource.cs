// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Infrastructure.Providers.Amp;

public sealed class AmpThreadTokenSource : ITokenUsageSource
{
    private const int PageSize = 20;
    // Safety valve for pathological accounts; a scan visiting this many
    // threads has almost certainly paged far past any real activity window.
    private const int MaxThreadsPerScan = 200;
    private static readonly TimeSpan RescanOverlap = TimeSpan.FromDays(1);
    private readonly IProcessRunner runner;
    private readonly Func<string?> executableResolver;

    public AmpThreadTokenSource(IProcessRunner runner)
        : this(runner, AmpCliStrategy.FindExecutable)
    {
    }

    internal AmpThreadTokenSource(IProcessRunner runner, Func<string?> executableResolver)
    {
        this.runner = runner;
        this.executableResolver = executableResolver;
    }

    public string Id => "amp.thread-exports";

    public async IAsyncEnumerable<TokenUsageEvent> ReadAsync(
        ProviderAccount account,
        ScannerCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? executable = executableResolver();
        if (executable is null) yield break;

        DateTimeOffset? cutoff = cursor?.LastObservedAt?.Subtract(RescanOverlap);

        // Page through the full listing (newest-updated first). Earlier builds
        // fetched only the first 20 threads, permanently skipping history for
        // accounts with more.
        var summaries = new List<AmpThreadSummary>();
        var seenThreadIds = new HashSet<string>(StringComparer.Ordinal);
        for (int offset = 0; summaries.Count < MaxThreadsPerScan; offset += PageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessResult listResult = await runner.RunAsync(
                executable,
                ["threads", "list", "--include-archived", "--limit", PageSize.ToString(CultureInfo.InvariantCulture), "--offset", offset.ToString(CultureInfo.InvariantCulture), "--json"],
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
            if (listResult.ExitCode != 0 || !AmpThreadParser.TryParseThreadList(listResult.StandardOutput, out IReadOnlyList<AmpThreadSummary> page))
                throw new InvalidOperationException("Amp thread listing failed.");
            if (page.Count == 0) break;

            // New threads created mid-scan shift offset pages; ids dedupe the
            // re-served entries.
            summaries.AddRange(page.Where(thread => seenThreadIds.Add(thread.Id)));
            if (page.Count < PageSize) break;
            // Once an entire page is older than the rescan cutoff, deeper pages
            // cannot contain activity the cursor has not already covered.
            if (cutoff.HasValue && page.All(thread => thread.UpdatedAt.HasValue && thread.UpdatedAt < cutoff)) break;
        }

        var allUsage = new List<AmpThreadUsage>();
        foreach (AmpThreadSummary thread in summaries
                     .Where(thread => !cutoff.HasValue || !thread.UpdatedAt.HasValue || thread.UpdatedAt >= cutoff)
                     .Take(MaxThreadsPerScan))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessResult exportResult = await runner.RunAsync(
                executable,
                ["threads", "export", thread.Id],
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            if (exportResult.ExitCode != 0
                || !AmpThreadParser.TryParseUsage(thread.Id, exportResult.StandardOutput, cutoff, out IReadOnlyList<AmpThreadUsage> usage))
                throw new InvalidOperationException("Amp thread export failed.");
            allUsage.AddRange(usage);
        }

        foreach (AmpThreadUsage usage in allUsage.OrderBy(item => item.OccurredAt))
        {
            yield return new TokenUsageEvent(
                account.Key,
                new ServiceProviderId("amp"),
                usage.Model,
                usage.OccurredAt,
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheReadTokens,
                usage.CacheWriteTokens,
                0,
                usage.SourceEventId,
                ProjectIdentity.Unknown);
        }
    }
}

internal sealed record AmpThreadSummary(string Id, DateTimeOffset? UpdatedAt);

internal sealed record AmpThreadUsage(
    string Model,
    DateTimeOffset OccurredAt,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    string SourceEventId);

internal static class AmpThreadParser
{
    internal static bool TryParseThreadList(string? raw, out IReadOnlyList<AmpThreadSummary> threads)
    {
        threads = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw ?? "");
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;

            var parsed = new List<AmpThreadSummary>();
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || ReadString(item, "id") is not { } id) continue;
                DateTimeOffset? updatedAt = ReadString(item, "updated") is { } updated
                    && DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset timestamp)
                        ? timestamp
                        : null;
                parsed.Add(new AmpThreadSummary(id, updatedAt));
            }
            threads = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseUsage(
        string threadId,
        string? raw,
        DateTimeOffset? cutoff,
        out IReadOnlyList<AmpThreadUsage> parsedUsage)
    {
        parsedUsage = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw ?? "");
            if (!document.RootElement.TryGetProperty("messages", out JsonElement messages)
                || messages.ValueKind != JsonValueKind.Array)
                return false;

            var parsed = new List<AmpThreadUsage>();
            foreach (JsonElement message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("usage", out JsonElement usage)
                    || usage.ValueKind != JsonValueKind.Object)
                    continue;

                long input = ReadLong(usage, "inputTokens");
                long output = ReadLong(usage, "outputTokens");
                long cacheRead = ReadLong(usage, "cacheReadInputTokens");
                long cacheWrite = ReadLong(usage, "cacheCreationInputTokens");
                if (input + output + cacheRead + cacheWrite == 0) continue;

                if (ReadString(usage, "model") is not { } model
                    || ReadString(usage, "timestamp") is not { } timestampText
                    || !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset occurredAt))
                    return false;
                if (cutoff.HasValue && occurredAt < cutoff.Value) continue;

                string? messageId = ReadString(message, "protocolMessageID")
                    ?? ReadIdentifier(message, "messageId");
                if (string.IsNullOrWhiteSpace(messageId)) return false;
                parsed.Add(new AmpThreadUsage(
                    model,
                    occurredAt,
                    input,
                    output,
                    cacheRead,
                    cacheWrite,
                    $"amp:{threadId}:{messageId}"));
            }
            parsedUsage = parsed.OrderBy(item => item.OccurredAt).ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string? ReadIdentifier(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static long ReadLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt64(out long result)
            ? Math.Max(0, result)
            : 0;
}
