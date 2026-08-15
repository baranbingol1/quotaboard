// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Pricing;
using Xunit;

namespace AiLimits.Tests;

public sealed class ModelVendorClassifierTests
{
    [Theory]
    [InlineData("Claude Code", "claude-fable-5", "Anthropic")]
    [InlineData("Claude Code", "claude-opus-4-8", "Anthropic")]
    [InlineData("Codex", "gpt-5.6-sol", "OpenAI")]
    [InlineData("Codex", "gpt-5.5", "OpenAI")]
    [InlineData("OpenCode", "claude-opus-4.7", "Anthropic")]
    [InlineData("OpenCode", "gpt-5.5-fast", "OpenAI")]
    [InlineData("OpenCode", "grok-build-0.1", "xAI")]
    [InlineData("OpenCode", "glm-5.2", "Zhipu AI")]
    [InlineData("Codex", "unknown", "OpenAI")]
    [InlineData("OpenCode", "openai/gpt-5.5-fast", "OpenAI")]
    [InlineData("OpenCode", "xai/grok-build-0.1", "xAI")]
    [InlineData("Claude Code", "unknown", "Anthropic")]
    [InlineData("OpenCode", "unknown-preview-7", "Unresolved")]
    [InlineData("Droid", "minimax-m3", "MiniMax")]
    [InlineData("Droid", "kimi-k2.7-code", "Moonshot AI")]
    [InlineData("Cline", "cline-pass/kimi-k3", "Moonshot AI")]
    [InlineData("Cline", "openai-codex/gpt-5.3-codex", "OpenAI")]
    public void GetDisplayName_recognizes_vendor_independently_of_pricing_resolution(
        string service,
        string rawModelId,
        string expected
    )
    {
        Assert.Equal(expected, ModelVendorClassifier.GetDisplayName(service, rawModelId));
    }
}
