// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Infrastructure.Usage;

namespace AiLimits.Infrastructure.Providers.Cline;

/// <summary>
/// Exact per-request token usage scanned from Cline's local histories. Two
/// layouts share each discovered root:
/// <para><c>tasks/{id}/ui_messages.json</c> (VS Code extension and legacy CLI
/// tasks): <c>api_req_started</c> entries whose <c>text</c> is a JSON string
/// that Cline rewrites in place when the request completes, so counts are
/// diffed per message and only the delta is emitted.</para>
/// <para><c>sessions/{sid}/{sid}.messages.json</c> (Cline CLI 3.x): assistant
/// messages carry a first-class <c>metrics</c> object; the sibling
/// <c>{sid}.json</c> supplies the working directory for project
/// attribution.</para>
/// Cline live-writes these files during requests, so every open uses
/// <see cref="FileShare.ReadWrite"/> and an unreadable or half-written file is
/// skipped rather than failing the scan; the next scan retries it.
/// </summary>
public sealed class ClineTaskHistoryTokenSource(IReadOnlyList<string> roots) : ITokenUsageSource
{
    private readonly ProjectIdentityResolver projectIdentityResolver = new();

    public string Id => "cline.local-history";

    public async IAsyncEnumerable<TokenUsageEvent> ReadAsync(
        ProviderAccount account,
        ScannerCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        foreach (string root in roots)
        {
            IReadOnlyDictionary<string, ClineTaskInfo> tasks = LoadTaskHistory(root);
            string tasksRoot = Path.Combine(root, "tasks");
            if (SafeFileEnumeration.IsSafeDirectory(tasksRoot))
            {
                foreach (
                    string taskDirectory in Directory
                        .EnumerateDirectories(tasksRoot, "*", SafeFileEnumeration.TopLevel)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                )
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string file = Path.Combine(taskDirectory, "ui_messages.json");
                    if (!File.Exists(file))
                        continue;
                    string taskId = Path.GetFileName(taskDirectory);
                    tasks.TryGetValue(taskId, out ClineTaskInfo? info);
                    ProjectIdentity project = projectIdentityResolver.Resolve(info?.Cwd);
                    await foreach (
                        TokenUsageEvent usage in ScanTaskFile(
                                file,
                                taskId,
                                info?.ModelId,
                                project,
                                account,
                                cursor,
                                cancellationToken
                            )
                            .ConfigureAwait(false)
                    )
                    {
                        yield return usage;
                    }
                }
            }

            string sessionsRoot = Path.Combine(root, "sessions");
            if (SafeFileEnumeration.IsSafeDirectory(sessionsRoot))
            {
                foreach (
                    string sessionDirectory in Directory
                        .EnumerateDirectories(sessionsRoot, "*", SafeFileEnumeration.TopLevel)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                )
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string sessionId = Path.GetFileName(sessionDirectory);
                    string file = Path.Combine(sessionDirectory, sessionId + ".messages.json");
                    if (!File.Exists(file))
                        continue;
                    ProjectIdentity project = ResolveSessionProject(sessionDirectory, sessionId);
                    await foreach (
                        TokenUsageEvent usage in ScanSessionFile(
                                file,
                                sessionId,
                                project,
                                account,
                                cursor,
                                cancellationToken
                            )
                            .ConfigureAwait(false)
                    )
                    {
                        yield return usage;
                    }
                }
            }
        }
    }

    private static async IAsyncEnumerable<TokenUsageEvent> ScanTaskFile(
        string file,
        string taskId,
        string? historyModelId,
        ProjectIdentity project,
        ProviderAccount account,
        ScannerCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        JsonDocument? document = await OpenDocumentAsync(file, cancellationToken).ConfigureAwait(false);
        if (document is null)
            yield break;
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                yield break;
            // In-place-update dedupe: Cline rewrites a message text in place when
            // the request completes, so only the growth since the previously seen
            // counts for the same message is usage.
            Dictionary<string, Counts> seen = new(StringComparer.Ordinal);
            foreach (JsonElement message in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (message.ValueKind != JsonValueKind.Object)
                    continue;
                if (
                    !message.TryGetProperty("say", out JsonElement say)
                    || say.ValueKind != JsonValueKind.String
                    || !string.Equals(say.GetString(), "api_req_started", StringComparison.Ordinal)
                )
                    continue;
                if (!message.TryGetProperty("ts", out JsonElement tsNode) || !TryReadInt64(tsNode, out long ts))
                    continue;
                if (
                    !message.TryGetProperty("text", out JsonElement textNode)
                    || textNode.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(textNode.GetString())
                )
                    continue;

                Counts current;
                JsonDocument? payload;
                try
                {
                    payload = JsonDocument.Parse(textNode.GetString()!);
                }
                catch (JsonException)
                {
                    continue;
                }
                using (payload)
                {
                    if (payload.RootElement.ValueKind != JsonValueKind.Object)
                        continue;
                    current = new Counts(
                        ReadLong(payload.RootElement, "tokensIn"),
                        ReadLong(payload.RootElement, "tokensOut"),
                        ReadLong(payload.RootElement, "cacheReads"),
                        ReadLong(payload.RootElement, "cacheWrites")
                    );
                }

                string key = taskId + ":" + ts.ToString(CultureInfo.InvariantCulture);
                Counts delta = current.DeltaFrom(seen.GetValueOrDefault(key));
                seen[key] = current;
                if (delta.Input + delta.Output + delta.CacheRead + delta.CacheWrite == 0)
                    continue;
                DateTimeOffset occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(ts);
                if (ScannerBoundary.AlreadyCovered(cursor, occurredAt))
                    continue;

                string? backend = null;
                string? model = null;
                if (
                    message.TryGetProperty("modelInfo", out JsonElement modelInfo)
                    && modelInfo.ValueKind == JsonValueKind.Object
                )
                {
                    backend = ReadString(modelInfo, "providerId");
                    model = ReadString(modelInfo, "modelId");
                }
                model ??= historyModelId;

                yield return new TokenUsageEvent(
                    account.Key,
                    new ServiceProviderId("cline"),
                    ComposeRawModelId(model, backend),
                    occurredAt,
                    delta.Input,
                    delta.Output,
                    delta.CacheRead,
                    delta.CacheWrite,
                    0,
                    $"cline:task:{taskId}:{ts.ToString(CultureInfo.InvariantCulture)}",
                    project
                );
            }
        }
    }

    private static async IAsyncEnumerable<TokenUsageEvent> ScanSessionFile(
        string file,
        string sessionId,
        ProjectIdentity project,
        ProviderAccount account,
        ScannerCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        JsonDocument? document = await OpenDocumentAsync(file, cancellationToken).ConfigureAwait(false);
        if (document is null)
            yield break;
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                yield break;
            if (
                !document.RootElement.TryGetProperty("messages", out JsonElement messages)
                || messages.ValueKind != JsonValueKind.Array
            )
                yield break;
            Dictionary<string, Counts> seen = new(StringComparer.Ordinal);
            foreach (JsonElement message in messages.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (message.ValueKind != JsonValueKind.Object)
                    continue;
                if (
                    !message.TryGetProperty("role", out JsonElement role)
                    || role.ValueKind != JsonValueKind.String
                    || !string.Equals(role.GetString(), "assistant", StringComparison.Ordinal)
                )
                    continue;
                if (
                    !message.TryGetProperty("metrics", out JsonElement metrics)
                    || metrics.ValueKind != JsonValueKind.Object
                )
                    continue;
                if (!message.TryGetProperty("ts", out JsonElement tsNode) || !TryReadInt64(tsNode, out long ts))
                    continue;

                var current = new Counts(
                    ReadLong(metrics, "inputTokens"),
                    ReadLong(metrics, "outputTokens"),
                    ReadLong(metrics, "cacheReadTokens"),
                    ReadLong(metrics, "cacheWriteTokens")
                );
                string messageKey = ReadString(message, "id") ?? ts.ToString(CultureInfo.InvariantCulture);
                Counts delta = current.DeltaFrom(seen.GetValueOrDefault(messageKey));
                seen[messageKey] = current;
                if (delta.Input + delta.Output + delta.CacheRead + delta.CacheWrite == 0)
                    continue;
                DateTimeOffset occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(ts);
                if (ScannerBoundary.AlreadyCovered(cursor, occurredAt))
                    continue;

                string? backend = null;
                string? model = null;
                if (
                    message.TryGetProperty("modelInfo", out JsonElement modelInfo)
                    && modelInfo.ValueKind == JsonValueKind.Object
                )
                {
                    backend = ReadString(modelInfo, "provider");
                    model = ReadString(modelInfo, "id");
                }

                yield return new TokenUsageEvent(
                    account.Key,
                    new ServiceProviderId("cline"),
                    ComposeRawModelId(model, backend),
                    occurredAt,
                    delta.Input,
                    delta.Output,
                    delta.CacheRead,
                    delta.CacheWrite,
                    0,
                    $"cline:session:{sessionId}:{messageKey}",
                    project
                );
            }
        }
    }

    private static async Task<JsonDocument?> OpenDocumentAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            // Half-written while Cline streams an update; the next scan retries.
            return null;
        }
    }

    private static IReadOnlyDictionary<string, ClineTaskInfo> LoadTaskHistory(string root)
    {
        string file = Path.Combine(root, "state", "taskHistory.json");
        Dictionary<string, ClineTaskInfo> tasks = new(StringComparer.Ordinal);
        if (!File.Exists(file))
            return tasks;
        try
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return tasks;
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                string? id = ReadIdValue(entry, "id");
                if (id is null)
                    continue;
                tasks[id] = new ClineTaskInfo(
                    ReadString(entry, "cwdOnTaskInitialization"),
                    ReadString(entry, "modelId")
                );
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return tasks;
    }

    private ProjectIdentity ResolveSessionProject(string sessionDirectory, string sessionId)
    {
        string file = Path.Combine(sessionDirectory, sessionId + ".json");
        if (File.Exists(file))
        {
            try
            {
                using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    string? cwd =
                        ReadString(document.RootElement, "cwd") ?? ReadString(document.RootElement, "workspace_root");
                    if (cwd is not null)
                    {
                        return projectIdentityResolver.Resolve(cwd);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        return ProjectIdentity.Unknown;
    }

    // The logged model id is used as-is when it already names its routing
    // backend ("cline-pass/kimi-k3"); otherwise the backend is prefixed so the
    // auth provider that served the request survives on the event
    // ("openai-codex/gpt-5.3-codex").
    private static string ComposeRawModelId(string? model, string? backend)
    {
        if (string.IsNullOrWhiteSpace(model))
            return "unknown";
        model = model.Trim();
        if (model.Contains("/"))
            return model;
        return string.IsNullOrWhiteSpace(backend) ? model : backend.Trim() + "/" + model;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string? ReadIdValue(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return null;
        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long id))
        {
            return id.ToString(CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static bool TryReadInt64(JsonElement value, out long result)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out result))
                return true;
            if (value.TryGetDouble(out double floating) && floating >= long.MinValue && floating <= long.MaxValue)
            {
                result = (long)floating;
                return true;
            }
        }
        result = 0;
        return false;
    }

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result)
            ? Math.Max(0, result)
            : 0;

    private sealed record ClineTaskInfo(string? Cwd, string? ModelId);

    private readonly record struct Counts(long Input, long Output, long CacheRead, long CacheWrite)
    {
        public Counts DeltaFrom(Counts previous) =>
            new(
                Delta(Input, previous.Input),
                Delta(Output, previous.Output),
                Delta(CacheRead, previous.CacheRead),
                Delta(CacheWrite, previous.CacheWrite)
            );

        private static long Delta(long current, long previous) => current >= previous ? current - previous : current;
    }
}
