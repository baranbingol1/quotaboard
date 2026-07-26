// SPDX-License-Identifier: Apache-2.0
using System.Text.RegularExpressions;
using AiLimits.Application.Presentation;

namespace AiLimits.Tests;

public sealed class ProviderColorsTests
{
    [Theory]
    [InlineData("claude", ProviderColors.Claude)]
    [InlineData("codex", ProviderColors.Codex)]
    [InlineData("droid", ProviderColors.Factory)]
    [InlineData("amp", ProviderColors.Amp)]
    [InlineData("cursor", ProviderColors.Cursor)]
    [InlineData("opencode", ProviderColors.OpenCode)]
    [InlineData("copilot", ProviderColors.Copilot)]
    [InlineData("antigravity", ProviderColors.Antigravity)]
    [InlineData("cline", ProviderColors.Cline)]
    [InlineData("clinepass", ProviderColors.Cline)]
    // Slug keys minted by UsageProviderClassifier.GetKey.
    [InlineData("openai-codex", ProviderColors.Codex)]
    [InlineData("anthropic-claude-code", ProviderColors.Claude)]
    [InlineData("openai-chatgpt-oauth", ProviderColors.Codex)]
    public void Provider_ids_map_to_their_brand_accent(string id, string expected)
    {
        Assert.Equal(expected, ProviderColors.Resolve(id));
    }

    [Theory]
    [InlineData("OpenAI (ChatGPT OAuth)", ProviderColors.Codex)]
    [InlineData("OpenAI (API key)", ProviderColors.Codex)]
    [InlineData("OpenAI (Codex)", ProviderColors.Codex)]
    [InlineData("Anthropic (Claude Code)", ProviderColors.Claude)]
    [InlineData("OpenCode Zen", ProviderColors.OpenCode)]
    [InlineData("GitHub Copilot", ProviderColors.Copilot)]
    public void Auth_provider_labels_share_the_family_color(string label, string expected)
    {
        Assert.Equal(expected, ProviderColors.Resolve(label));
    }

    [Fact]
    public void Punctuation_and_case_do_not_change_the_match()
    {
        Assert.Equal(ProviderColors.Resolve("zai"), ProviderColors.Resolve("Z.AI"));
        Assert.Equal(ProviderColors.Resolve("zai"), ProviderColors.Resolve("Z.AI Coding Plan"));
        Assert.Equal(ProviderColors.Resolve("claude"), ProviderColors.Resolve("Claude Code"));
    }

    [Fact]
    public void Unknown_keys_get_stable_unique_wellformed_colors()
    {
        string first = ProviderColors.Resolve("some-new-provider");
        Assert.Equal(first, ProviderColors.Resolve("some-new-provider"));
        Assert.Matches(new Regex("^#[0-9A-F]{6}$"), first);
        Assert.NotEqual(first, ProviderColors.Resolve("another-provider"));
        Assert.NotEqual(ProviderColors.Neutral, first);
    }

    [Fact]
    public void Empty_input_falls_back_to_neutral()
    {
        Assert.Equal(ProviderColors.Neutral, ProviderColors.Resolve(null));
        Assert.Equal(ProviderColors.Neutral, ProviderColors.Resolve("  "));
    }
}
