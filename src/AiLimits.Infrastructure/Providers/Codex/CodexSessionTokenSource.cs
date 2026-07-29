// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Infrastructure.Usage;

namespace AiLimits.Infrastructure.Providers.Codex;

public sealed class CodexSessionTokenSource(string codexHome) : ITokenUsageSource
{
    private readonly ProjectIdentityResolver projectIdentityResolver = new();

    public string Id => "codex.sessions-jsonl";

    public async IAsyncEnumerable<TokenUsageEvent> ReadAsync(
        ProviderAccount account,
        ScannerCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The Codex app moves finished rollouts into archived_sessions; both
        // trees carry the same JSONL format.
        var roots = new[] { Path.Combine(codexHome, "sessions"), Path.Combine(codexHome, "archived_sessions") };
        var files = roots
            .Where(SafeFileEnumeration.IsSafeDirectory)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.jsonl", SafeFileEnumeration.Recursive))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var previous = Counts.Zero;
            var model = "unknown";
            var project = ProjectIdentity.Unknown;
            var lineNumber = 0;
            // Live session files are appended by the Codex app while we read;
            // open shared and skip anything unreadable rather than aborting
            // the whole scan.
            StreamReader reader;
            try
            {
                reader = new StreamReader(new FileStream(
                    file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 16384,
                    FileOptions.Asynchronous | FileOptions.SequentialScan));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            using var readerScope = reader;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (!root.TryGetProperty("payload", out var payload) ||
                        // Newer session formats emit "payload": null / "info": null
                        // lines; TryGetProperty on a Null element throws.
                        payload.ValueKind != JsonValueKind.Object) continue;
                    if (TryWorkingDirectory(root, payload) is { } workingDirectory)
                        project = projectIdentityResolver.Resolve(workingDirectory);
                    if (!TryTimestamp(root, out var occurredAt)) continue;
                    if (payload.TryGetProperty("model", out var modelNode) &&
                        modelNode.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(modelNode.GetString()))
                        model = modelNode.GetString()!;

                    var info = payload.TryGetProperty("info", out var infoNode) && infoNode.ValueKind == JsonValueKind.Object
                        ? infoNode
                        : payload;
                    if (!info.TryGetProperty("total_token_usage", out var usage) ||
                        usage.ValueKind != JsonValueKind.Object) continue;

                    var current = new Counts(
                        ReadLong(usage, "input_tokens"),
                        ReadLong(usage, "output_tokens"),
                        ReadLong(usage, "cached_input_tokens"),
                        ReadLong(usage, "reasoning_output_tokens"));
                    var delta = current.DeltaFrom(previous);
                    previous = current;
                    var input = Math.Max(0, delta.Input - delta.Cache);
                    var output = Math.Max(0, delta.Output - delta.Reasoning);
                    if (input + output + delta.Cache + delta.Reasoning == 0) continue;
                    if (ScannerBoundary.AlreadyCovered(cursor, occurredAt)) continue;

                    yield return new TokenUsageEvent(
                        account.Key, new ServiceProviderId("codex"), model, occurredAt,
                        input, output, delta.Cache, 0, delta.Reasoning,
                        $"codex:{Path.GetFileName(file)}:{lineNumber}", project);
                }
            }
        }
    }

    private static string? TryWorkingDirectory(JsonElement root, JsonElement payload)
    {
        if (ReadString(payload, "cwd") is { } payloadCwd) return payloadCwd;
        if (payload.TryGetProperty("turn_context", out var turnContext) &&
            turnContext.ValueKind == JsonValueKind.Object &&
            ReadString(turnContext, "cwd") is { } turnCwd) return turnCwd;
        return ReadString(root, "cwd");
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static bool TryTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var node) &&
               node.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(node.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    }

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            ? Math.Max(0, result)
            : 0;

    private readonly record struct Counts(long Input, long Output, long Cache, long Reasoning)
    {
        public static Counts Zero => new(0, 0, 0, 0);
        public Counts DeltaFrom(Counts previous) => new(
            Delta(Input, previous.Input), Delta(Output, previous.Output),
            Delta(Cache, previous.Cache), Delta(Reasoning, previous.Reasoning));
        private static long Delta(long current, long previous) => current >= previous ? current - previous : current;
    }
}
