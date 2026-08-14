// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text;

namespace AiLimits.Application.Usage;

/// <summary>
/// Names the account or routing provider that authorized a recorded request.
/// This is intentionally independent from the company that created the model.
/// </summary>
public static class UsageProviderClassifier
{
    public static string GetDisplayName(string recordingSourceId, string authorizationProviderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingSourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationProviderId);

        var source = recordingSourceId.Trim().ToLowerInvariant();
        var provider = authorizationProviderId.Trim().ToLowerInvariant();

        // The Codex and Claude Code transcripts carry no auth signal at all:
        // the same files are written whether the CLI is signed in to a
        // subscription or driven by an API key (or Bedrock/Vertex). Name the
        // harness that authorized the request — the only thing actually
        // observed — instead of asserting an OAuth flow we never detected.
        // OpenCode is the one source that does detect it, below.
        if (source == "codex" && provider == "codex")
            return "OpenAI (Codex)";
        if (source == "claude" && provider == "claude")
            return "Anthropic (Claude Code)";

        return provider switch
        {
            "openai" => "OpenAI",
            "openai-oauth" => "OpenAI (ChatGPT OAuth)",
            "openai-api" => "OpenAI (API key)",
            "anthropic" => "Anthropic",
            "github-copilot" or "copilot" => "GitHub Copilot",
            "xai" => "xAI",
            "opencode-go" => "OpenCode Go",
            "opencode-zen" or "opencode" => "OpenCode Zen",
            "zai" => "Z.AI",
            "zai-coding-plan" => "Z.AI Coding Plan",
            "minimax" => "MiniMax",
            "minimax-coding-plan" => "MiniMax Coding Plan",
            "chutes" => "Chutes",
            "google" => "Google",
            "google-vertex" or "vertex" => "Google Vertex AI",
            "amazon-bedrock" or "bedrock" => "Amazon Bedrock",
            "azure" or "azure-openai" => "Azure OpenAI",
            "cline-pass" or "clinepass" => "ClinePass",
            "cline" => "Cline",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(provider.Replace('-', ' ')),
        };
    }

    /// <summary>
    /// Stable grouping key for the provider a record was authorized by,
    /// derived from the display name so the two can never disagree.
    /// <para>
    /// The raw <c>authorizationProviderId</c> is not usable as a key: the
    /// display name also depends on the recording harness, so
    /// <c>(codex, codex)</c> and <c>(opencode, openai-oauth)</c> are distinct
    /// ids that render the same label. Keying on the id split one provider
    /// into two identically-labelled facet rows, and ticking either filtered
    /// only part of that provider's usage.
    /// </para>
    /// </summary>
    public static string GetKey(string recordingSourceId, string authorizationProviderId) =>
        Slug(GetDisplayName(recordingSourceId, authorizationProviderId));

    private static string Slug(string displayName)
    {
        StringBuilder builder = new(displayName.Length);
        foreach (char character in displayName.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }
        return builder.ToString().TrimEnd('-');
    }
}
