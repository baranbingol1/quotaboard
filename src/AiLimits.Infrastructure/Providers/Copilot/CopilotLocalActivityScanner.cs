// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Application.Usage;

namespace AiLimits.Infrastructure.Providers.Copilot;

public sealed class CopilotLocalActivityScanner(string? vscodeUserRoot = null, string? copilotHome = null)
{
    private readonly string _vscodeUserRoot =
        vscodeUserRoot
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User");
    private readonly string _copilotHome =
        copilotHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    public async Task<IReadOnlyList<CopilotActivityEvent>> ScanAsync(CancellationToken cancellationToken)
    {
        var events = new List<CopilotActivityEvent>();
        foreach (string path in FindVsCodeSessionFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReadVsCodeSessionAsync(path, events, cancellationToken).ConfigureAwait(false);
        }
        foreach (
            string path in SafeEnumerateFiles(
                Path.Combine(_copilotHome, "session-state"),
                "events.jsonl",
                SearchOption.AllDirectories
            )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReadCliSessionAsync(path, events, cancellationToken).ConfigureAwait(false);
        }
        return events;
    }

    private IEnumerable<string> FindVsCodeSessionFiles()
    {
        string workspaces = Path.Combine(_vscodeUserRoot, "workspaceStorage");
        foreach (string directory in SafeEnumerateDirectories(workspaces))
        {
            foreach (
                string path in SafeEnumerateFiles(
                    Path.Combine(directory, "chatSessions"),
                    "*.json*",
                    SearchOption.TopDirectoryOnly
                )
            )
            {
                yield return path;
            }
        }
        foreach (
            string path in SafeEnumerateFiles(
                Path.Combine(_vscodeUserRoot, "globalStorage", "emptyWindowChatSessions"),
                "*.json*",
                SearchOption.TopDirectoryOnly
            )
        )
        {
            yield return path;
        }
    }

    private static async Task ReadVsCodeSessionAsync(
        string path,
        List<CopilotActivityEvent> events,
        CancellationToken cancellationToken
    )
    {
        var requests = new Dictionary<string, CopilotActivityEvent>(StringComparer.Ordinal);
        string fallbackSessionId = Path.GetFileNameWithoutExtension(path);
        string sessionId = fallbackSessionId;
        DateTimeOffset? createdAt = null;
        foreach (JsonElement root in await ReadJsonValuesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            JsonElement value = root;
            if (
                root.TryGetProperty("kind", out JsonElement kind)
                && kind.ValueKind == JsonValueKind.Number
                && kind.TryGetInt32(out int operation)
                && operation == 0
                && root.TryGetProperty("v", out JsonElement initial)
            )
            {
                value = initial;
            }
            if (value.ValueKind == JsonValueKind.Object)
            {
                sessionId = String(value, "sessionId") ?? sessionId;
                createdAt ??= UnixMilliseconds(value, "creationDate");
                AddRequests(value, sessionId, requests);
            }
            if (
                root.TryGetProperty("kind", out kind)
                && kind.ValueKind == JsonValueKind.Number
                && kind.TryGetInt32(out operation)
                && operation == 2
                && IsRequestsPath(root)
                && root.TryGetProperty("v", out JsonElement inserted)
            )
            {
                AddRequestArray(inserted, sessionId, requests);
            }
        }
        if (createdAt.HasValue && requests.Count > 0)
        {
            events.Add(
                new CopilotActivityEvent(
                    "vscode-copilot",
                    "VS Code Copilot Chat",
                    sessionId,
                    createdAt.Value,
                    CopilotActivityKind.Session,
                    null
                )
            );
        }
        events.AddRange(requests.Values);
    }

    private static void AddRequests(
        JsonElement root,
        string sessionId,
        Dictionary<string, CopilotActivityEvent> requests
    )
    {
        if (root.TryGetProperty("requests", out JsonElement values))
        {
            AddRequestArray(values, sessionId, requests);
        }
    }

    private static void AddRequestArray(
        JsonElement values,
        string sessionId,
        Dictionary<string, CopilotActivityEvent> requests
    )
    {
        if (values.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement request in values.EnumerateArray())
        {
            if (request.ValueKind != JsonValueKind.Object || !IsCopilotRequest(request))
            {
                continue;
            }
            DateTimeOffset? timestamp = UnixMilliseconds(request, "timestamp");
            if (!timestamp.HasValue)
            {
                continue;
            }
            string id = String(request, "requestId") ?? $"{sessionId}:{timestamp.Value.ToUnixTimeMilliseconds()}";
            requests[id] = new CopilotActivityEvent(
                "vscode-copilot",
                "VS Code Copilot Chat",
                sessionId,
                timestamp.Value,
                CopilotActivityKind.Request,
                NormalizeModel(String(request, "modelId"))
            );
        }
    }

    private static bool IsCopilotRequest(JsonElement request)
    {
        string? model = String(request, "modelId");
        if (model?.StartsWith("copilot/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }
        if (!request.TryGetProperty("agent", out JsonElement agent) || agent.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        string? id = String(agent, "id");
        return id?.StartsWith("github.copilot", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static async Task ReadCliSessionAsync(
        string path,
        List<CopilotActivityEvent> events,
        CancellationToken cancellationToken
    )
    {
        string sessionId = Directory.GetParent(path)?.Name ?? Path.GetFileNameWithoutExtension(path);
        string? model = null;
        foreach (JsonElement root in await ReadJsonValuesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string? type = String(root, "type");
            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            DateTimeOffset? timestamp = Timestamp(root, "timestamp");
            if (type == "session.start")
            {
                sessionId = String(data, "sessionId") ?? sessionId;
                model = String(data, "selectedModel");
                timestamp ??= Timestamp(data, "startTime");
                if (timestamp.HasValue)
                {
                    events.Add(
                        new CopilotActivityEvent(
                            "copilot-cli",
                            "Copilot CLI",
                            sessionId,
                            timestamp.Value,
                            CopilotActivityKind.Session,
                            null
                        )
                    );
                }
            }
            else if (type == "session.model_change")
            {
                model = String(data, "newModel") ?? model;
            }
            else if (type == "assistant.turn_end" && timestamp.HasValue)
            {
                events.Add(
                    new CopilotActivityEvent(
                        "copilot-cli",
                        "Copilot CLI",
                        sessionId,
                        timestamp.Value,
                        CopilotActivityKind.Request,
                        model
                    )
                );
            }
        }
    }

    private static bool IsRequestsPath(JsonElement root)
    {
        if (!root.TryGetProperty("k", out JsonElement path) || path.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        JsonElement.ArrayEnumerator parts = path.EnumerateArray();
        return parts.MoveNext()
            && parts.Current.ValueKind == JsonValueKind.String
            && parts.Current.GetString() == "requests";
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadJsonValuesAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var values = new List<JsonElement>();
        FileStream? stream = null;
        StreamReader? reader = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            reader = new StreamReader(stream);
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(text);
                values.Add(document.RootElement.Clone());
                return values;
            }
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                JsonDocument? document = null;
                try
                {
                    document = JsonDocument.Parse(line);
                    values.Add(document.RootElement.Clone());
                }
                catch (JsonException)
                {
                    // VS Code and Copilot CLI can leave a partial final line while writing.
                }
                finally
                {
                    document?.Dispose();
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        finally
        {
            reader?.Dispose();
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        return values;
    }

    private static string? String(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    // TryGetInt64 throws when the element is not a Number, so the kind check is
    // load-bearing: one session file with a null or string timestamp would
    // otherwise abort the scan for every session, not just that one.
    private static DateTimeOffset? UnixMilliseconds(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement item)
        && item.ValueKind == JsonValueKind.Number
        && item.TryGetInt64(out long milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;

    private static DateTimeOffset? Timestamp(JsonElement value, string property) =>
        String(value, property) is { } text && DateTimeOffset.TryParse(text, out DateTimeOffset timestamp)
            ? timestamp
            : null;

    private static string? NormalizeModel(string? model) =>
        string.Equals(model, "copilot/auto", StringComparison.OrdinalIgnoreCase) ? null : model;

    private static string[] SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateDirectories(path).ToArray() : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] SafeEnumerateFiles(string path, string pattern, SearchOption option)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern, option).ToArray() : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
