// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Preferences;

namespace AiLimits.Tests;

public sealed class ProviderVisibilitySetTests
{
    [Fact]
    public void Newly_detected_providers_default_to_visible()
    {
        Assert.True(ProviderVisibilitySet.Empty.IsVisible("cursor"));
        Assert.True(ProviderVisibilitySet.Parse("{\"hidden\":[]}").IsVisible("antigravity"));
    }

    [Fact]
    public void Hiding_and_showing_round_trips_through_serialization()
    {
        var set = ProviderVisibilitySet
            .Empty.WithVisibility("copilot", visible: false)
            .WithVisibility("antigravity", visible: false);

        var reloaded = ProviderVisibilitySet.Parse(set.Serialize());

        Assert.False(reloaded.IsVisible("copilot"));
        Assert.False(reloaded.IsVisible("antigravity"));
        Assert.True(reloaded.IsVisible("codex"));

        var shownAgain = reloaded.WithVisibility("copilot", visible: true);
        Assert.True(shownAgain.IsVisible("copilot"));
        Assert.False(shownAgain.IsVisible("antigravity"));
    }

    [Fact]
    public void Provider_ids_are_case_insensitive_and_trimmed()
    {
        var set = ProviderVisibilitySet.Empty.WithVisibility(" Copilot ", visible: false);

        Assert.False(set.IsVisible("copilot"));
        Assert.False(ProviderVisibilitySet.Parse(set.Serialize()).IsVisible("COPILOT"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"hidden\":\"oops\"}")]
    [InlineData("[1,2,3]")]
    public void Malformed_preference_data_falls_back_to_all_visible(string? json)
    {
        var set = ProviderVisibilitySet.Parse(json);

        Assert.True(set.IsVisible("codex"));
        Assert.Empty(set.HiddenProviders);
    }

    [Fact]
    public void Unknown_or_blank_ids_are_harmless()
    {
        var set = ProviderVisibilitySet.Empty.WithVisibility("", visible: false);

        Assert.Empty(set.HiddenProviders);
        Assert.True(set.IsVisible(""));
        Assert.True(set.IsVisible("never-seen-before"));
    }
}
