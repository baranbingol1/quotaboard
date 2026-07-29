// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Infrastructure.Usage;

namespace AiLimits.Infrastructure.Providers.Claude;

public sealed class ClaudeJsonlTokenSource(string claudeHome) : ITokenUsageSource
{
    private readonly ProjectIdentityResolver projectIdentityResolver = new();

    public string Id => "claude.projects-jsonl";

    public async IAsyncEnumerable<TokenUsageEvent> ReadAsync(
        ProviderAccount account,
        ScannerCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var projects = Path.Combine(claudeHome, "projects");
        if (!Directory.Exists(projects)) yield break;

        foreach (var file in Directory.EnumerateFiles(projects, "*.jsonl", SafeFileEnumeration.Recursive)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            // FileShare.ReadWrite is essential: files actively written by
            // Claude cannot be opened with the default FileShare.Read used by
            // File.ReadLinesAsync. If the open or any read fails (locked file,
            // permission), skip this file and continue with the next one.
            Stream? stream;
            try
            {
                stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            await using (stream.ConfigureAwait(false))
            {
                StreamReader? reader;
                try
                {
                    reader = new StreamReader(stream);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                using (reader)
                {
                    var seen = new Dictionary<string, Counts>(StringComparer.Ordinal);
                    var project = ProjectIdentity.Unknown;
                    var lineNumber = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string? line;
                        try
                        {
                            line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                            break;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            break;
                        }
                        if (line is null) break;
                        lineNumber++;
                        JsonDocument document;
                        try { document = JsonDocument.Parse(line); }
                        catch (JsonException) { continue; }
                        using (document)
                        {
                            var root = document.RootElement;
                            if (TryWorkingDirectory(root) is { } workingDirectory)
                                project = projectIdentityResolver.Resolve(workingDirectory);
                            if (!TryTimestamp(root, out var occurredAt) ||
                                !root.TryGetProperty("message", out var message) ||
                                !message.TryGetProperty("usage", out var usage) ||
                                usage.ValueKind != JsonValueKind.Object) continue;

                            var messageId = ReadString(message, "id") ?? $"{Path.GetFileName(file)}:{lineNumber}";
                            var model = ReadString(message, "model") ?? "unknown";
                            var current = new Counts(
                                ReadLong(usage, "input_tokens"),
                                ReadLong(usage, "output_tokens"),
                                ReadLong(usage, "cache_read_input_tokens"),
                                ReadLong(usage, "cache_creation_input_tokens"));
                            var previous = seen.GetValueOrDefault(messageId);
                            var delta = current.DeltaFrom(previous);
                            seen[messageId] = current;
                            if (delta.Input + delta.Output + delta.CacheRead + delta.CacheWrite == 0) continue;
                            if (ScannerBoundary.AlreadyCovered(cursor, occurredAt)) continue;

                            yield return new TokenUsageEvent(
                                account.Key, new ServiceProviderId("claude"), model, occurredAt,
                                delta.Input, delta.Output, delta.CacheRead, delta.CacheWrite, 0,
                                $"claude:{messageId}:{lineNumber}", project);
                        }
                    }
                }
            }
        }
    }

    private static string? TryWorkingDirectory(JsonElement root)
    {
        if (ReadString(root, "cwd") is { } cwd) return cwd;
        if (ReadString(root, "projectPath") is { } projectPath) return projectPath;
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            return ReadString(message, "cwd") ?? ReadString(message, "projectPath");
        return null;
    }

    private static bool TryTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var node) &&
               node.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(node.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            ? Math.Max(0, result)
            : 0;

    private readonly record struct Counts(long Input, long Output, long CacheRead, long CacheWrite)
    {
        public Counts DeltaFrom(Counts previous) => new(
            Delta(Input, previous.Input), Delta(Output, previous.Output),
            Delta(CacheRead, previous.CacheRead), Delta(CacheWrite, previous.CacheWrite));
        private static long Delta(long current, long previous) => current >= previous ? current - previous : current;
    }
}
