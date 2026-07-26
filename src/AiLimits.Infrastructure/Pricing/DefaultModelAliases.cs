// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Pricing;
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Pricing;

public static class DefaultModelAliases
{
    public static IReadOnlyList<ModelAlias> All { get; } = Build();

    private static IReadOnlyList<ModelAlias> Build()
    {
        List<ModelAlias> list = new List<ModelAlias>();
        Add(list, "codex", "openai", new string[] { "gpt-5", "gpt-5-mini", "gpt-5-codex", "gpt-5.1-codex", "gpt-5.2-codex", "gpt-5.3-codex" });
        Add(list, "claude", "anthropic", new string[] { "claude-sonnet-4", "claude-sonnet-4-5", "claude-opus-4", "claude-opus-4-1", "claude-opus-4-5" });
        Add(list, "copilot", "openai", new string[] { "gpt-4.1", "gpt-5", "gpt-5-mini", "o3", "o4-mini" });
        Add(list, "copilot", "anthropic", new string[] { "claude-sonnet-4", "claude-sonnet-4-5", "claude-opus-4", "claude-opus-4-1", "claude-opus-4-5" });
        Add(list, "copilot", "google", new string[] { "gemini-2.5-pro", "gemini-2.5-flash" });
        Add(list, "opencode", "openai", new string[] { "gpt-5", "gpt-5-mini", "gpt-5-codex", "gpt-5.1-codex", "gpt-5.2-codex", "gpt-5.3-codex" });
        Add(list, "opencode", "anthropic", new string[] { "claude-sonnet-4", "claude-sonnet-4-5", "claude-opus-4", "claude-opus-4-1", "claude-opus-4-5" });
        Add(list, "opencode", "google", new string[] { "gemini-2.5-pro", "gemini-2.5-flash" });
        return list;
    }

    private static void Add(ICollection<ModelAlias> aliases, string service, string pricingProvider, IEnumerable<string> modelIds)
    {
        foreach (string modelId in modelIds)
        {
            aliases.Add(new ModelAlias(new ServiceProviderId(service), modelId, pricingProvider, modelId));
        }
    }
}
