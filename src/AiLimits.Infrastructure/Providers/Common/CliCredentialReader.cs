// SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiLimits.Infrastructure.Providers.Common;

internal static class CliCredentialReader
{
    public static Task<CliCredential?> ReadCodexAsync(string path, CancellationToken cancellationToken)
    {
        return ReadAsync(
            path,
            "tokens",
            new string[] { "access_token", "accessToken" },
            new string[] { "id_token", "idToken" },
            new string[] { "account_id", "accountId" },
            new string[] { "chatgpt_plan_type", "plan_type" },
            cancellationToken
        );
    }

    public static Task<CliCredential?> ReadClaudeAsync(string path, CancellationToken cancellationToken)
    {
        return ReadAsync(
            path,
            "claudeAiOauth",
            new string[] { "accessToken", "access_token" },
            Array.Empty<string>(),
            Array.Empty<string>(),
            new string[] { "subscriptionType", "subscription_type" },
            cancellationToken
        );
    }

    private static async Task<CliCredential?> ReadAsync(
        string path,
        string containerName,
        IReadOnlyList<string> accessNames,
        IReadOnlyList<string> idTokenNames,
        IReadOnlyList<string> accountNames,
        IReadOnlyList<string> planNames,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            CliCredential result;
            await using (
                FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    16384,
                    FileOptions.Asynchronous | FileOptions.SequentialScan
                )
            )
            {
                using JsonDocument document = await JsonDocument
                    .ParseAsync(stream, default(JsonDocumentOptions), cancellationToken)
                    .ConfigureAwait(false);
                JsonElement root = document.RootElement;
                JsonElement nested;
                JsonElement container = (
                    (
                        root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty(containerName, out nested)
                        && nested.ValueKind == JsonValueKind.Object
                    )
                        ? nested
                        : root
                );
                string accessToken = ReadString(container, accessNames);
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    result = null;
                }
                else
                {
                    string idToken = ReadString(container, idTokenNames) ?? accessToken;
                    JsonElement claims = ParseJwtClaims(idToken);
                    string accountId =
                        ReadString(container, accountNames)
                        ?? ReadString(root, accountNames)
                        ?? ReadString(
                            claims,
                            new string[] { "chatgpt_account_id", "account_id", "organization_id", "org_id", "sub" }
                        );
                    string planHint =
                        ReadString(container, planNames)
                        ?? ReadString(root, planNames)
                        ?? ReadPlanFromClaims(claims, planNames);
                    result = new CliCredential(
                        Login: ReadString(claims, new string[] { "email", "preferred_username" }),
                        AccessToken: accessToken,
                        AccountId: accountId ?? AnonymousKey(path),
                        SourcePath: path,
                        PlanHint: planHint
                    );
                }
            }
            return result;
        }
        catch (Exception ex)
            when (((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException) ? 1 : 0) != 0)
        {
            return null;
        }
    }

    private static JsonElement ParseJwtClaims(string token)
    {
        string[] array = token.Split('.');
        if (array.Length < 2)
        {
            return default(JsonElement);
        }
        try
        {
            string text = array[1].Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            using JsonDocument jsonDocument = JsonDocument.Parse(Convert.FromBase64String(text));
            return jsonDocument.RootElement.Clone();
        }
        catch (Exception ex) when (((ex is FormatException || ex is JsonException) ? 1 : 0) != 0)
        {
            return default(JsonElement);
        }
    }

    // The ChatGPT plan lives in the id_token under the namespaced claim
    // "https://api.openai.com/auth" → chatgpt_plan_type (CodexBar parity).
    private static string? ReadPlanFromClaims(JsonElement claims, IReadOnlyList<string> planNames)
    {
        if (claims.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string direct = ReadString(claims, planNames);
        if (direct != null)
        {
            return direct;
        }
        return
            claims.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object
            ? ReadString(auth, planNames)
            : null;
    }

    private static string? ReadString(JsonElement element, IReadOnlyList<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (string name in names)
        {
            if (
                element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
            )
            {
                return value.GetString().Trim();
            }
        }
        return null;
    }

    private static string AnonymousKey(string path)
    {
        return "local-"
            + Convert
                .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)))[..8])
                .ToLowerInvariant();
    }
}

internal sealed record CliCredential(
    string AccessToken,
    string AccountId,
    string? Login,
    string SourcePath,
    string? PlanHint = null
);
