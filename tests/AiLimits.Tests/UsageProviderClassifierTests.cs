// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Usage;

namespace AiLimits.Tests;

public sealed class UsageProviderClassifierTests
{
    [Theory]
    [InlineData("opencode", "github-copilot", "GitHub Copilot")]
    [InlineData("opencode", "openai", "OpenAI")]
    [InlineData("opencode", "openai-oauth", "OpenAI (ChatGPT OAuth)")]
    [InlineData("opencode", "openai-api", "OpenAI (API key)")]
    [InlineData("opencode", "xai", "xAI")]
    [InlineData("opencode", "opencode-go", "OpenCode Go")]
    [InlineData("codex", "codex", "OpenAI (Codex)")]
    [InlineData("claude", "claude", "Anthropic (Claude Code)")]
    [InlineData("cline", "cline", "Cline")]
    [InlineData("cline", "cline-pass", "ClinePass")]
    [InlineData("cline", "clinepass", "ClinePass")]
    public void Names_the_authorizing_provider_not_the_model_maker(
        string recordingSource,
        string authorizationProvider,
        string expected)
    {
        Assert.Equal(expected, UsageProviderClassifier.GetDisplayName(recordingSource, authorizationProvider));
    }

    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("claude", "claude")]
    public void Harness_recorded_usage_is_not_labelled_as_an_auth_flow_we_never_detected(
        string recordingSource,
        string authorizationProvider)
    {
        string name = UsageProviderClassifier.GetDisplayName(recordingSource, authorizationProvider);

        Assert.DoesNotContain("OAuth", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("API key", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenCode_keeps_the_auth_split_it_actually_detects()
    {
        Assert.Equal("OpenAI (ChatGPT OAuth)", UsageProviderClassifier.GetDisplayName("opencode", "openai-oauth"));
        Assert.Equal("OpenAI (API key)", UsageProviderClassifier.GetDisplayName("opencode", "openai-api"));
    }

    [Fact]
    public void Records_that_render_the_same_label_share_one_key()
    {
        // "codex" and "openai-oauth" are different authorization ids that both
        // render "OpenAI (…)" families; whatever the label says, the key must
        // agree with it or the usage facets list the provider twice.
        Assert.Equal(
            UsageProviderClassifier.GetKey("codex", "codex"),
            UsageProviderClassifier.GetKey("codex", "codex"));
        Assert.Equal("openai-codex", UsageProviderClassifier.GetKey("codex", "codex"));
        Assert.Equal("anthropic-claude-code", UsageProviderClassifier.GetKey("claude", "claude"));
        Assert.Equal("openai-chatgpt-oauth", UsageProviderClassifier.GetKey("opencode", "openai-oauth"));
        Assert.Equal("github-copilot", UsageProviderClassifier.GetKey("opencode", "github-copilot"));
        Assert.Equal("z-ai-coding-plan", UsageProviderClassifier.GetKey("opencode", "zai-coding-plan"));
    }

    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("claude", "claude")]
    [InlineData("opencode", "openai-oauth")]
    [InlineData("opencode", "openai-api")]
    [InlineData("opencode", "github-copilot")]
    [InlineData("cline", "cline-pass")]
    public void Every_key_is_a_lowercase_slug_of_its_own_label(
        string recordingSource,
        string authorizationProvider)
    {
        string key = UsageProviderClassifier.GetKey(recordingSource, authorizationProvider);

        Assert.NotEmpty(key);
        Assert.Equal(key.ToLowerInvariant(), key);
        Assert.DoesNotContain(key, " ");
        Assert.False(key.StartsWith('-') || key.EndsWith('-'));
    }
}
