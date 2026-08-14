// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Application.Pricing;

public static class ModelVendorClassifier
{
    private static readonly (string Vendor, string PricingProvider, string[] Prefixes)[] ModelFamilies = new (
        string,
        string,
        string[]
    )[14]
    {
        ("Anthropic", "anthropic", new string[] { "claude-", "anthropic.claude-" }),
        ("Cursor", "cursor", new string[] { "composer-", "cursor-" }),
        (
            "OpenAI",
            "openai",
            new string[]
            {
                "gpt-",
                "chatgpt-",
                "codex-",
                "openai.gpt-",
                "o1",
                "o3",
                "o4",
                "text-embedding-",
                "whisper-",
                "dall-e-",
                "sora-",
            }
        ),
        ("Google", "google", new string[] { "gemini-", "gemma-" }),
        ("xAI", "xai", new string[] { "grok-" }),
        ("Zhipu AI", "zai", new string[] { "glm-", "codegeex-" }),
        ("DeepSeek", "deepseek", new string[] { "deepseek-" }),
        ("Alibaba", "alibaba", new string[] { "qwen-" }),
        ("Mistral AI", "mistral", new string[] { "mistral-", "ministral-", "codestral-", "pixtral-" }),
        ("Meta", "meta", new string[] { "llama-", "meta-llama-" }),
        ("Cohere", "cohere", new string[] { "command-" }),
        // models.dev catalogs Moonshot under "moonshotai" (there is no "moonshot").
        ("Moonshot AI", "moonshotai", new string[] { "kimi-" }),
        ("MiniMax", "minimax", new string[] { "minimax-" }),
        ("01.AI", "01-ai", new string[] { "yi-" }),
    };

    public static string GetDisplayName(string service, string rawModelId)
    {
        ArgumentNullException.ThrowIfNull(service, "service");
        ArgumentNullException.ThrowIfNull(rawModelId, "rawModelId");
        string modelId = rawModelId.Trim().ToLowerInvariant();
        int separator = modelId.LastIndexOf('/');
        if (separator >= 0 && separator < modelId.Length - 1)
        {
            modelId = modelId.Substring(separator + 1);
        }
        (string, string, string[])[] modelFamilies = ModelFamilies;
        for (int i = 0; i < modelFamilies.Length; i++)
        {
            var (result, _, source) = modelFamilies[i];
            if (source.Any((string prefix) => modelId.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return result;
            }
        }
        string text = service.Trim().ToLowerInvariant();
        string result2;
        switch (text)
        {
            case "codex":
                result2 = "OpenAI";
                break;
            case "claude":
            case "claude code":
                result2 = "Anthropic";
                break;
            default:
                result2 = "Unresolved";
                break;
        }
        return result2;
    }

    public static string? GetPricingProviderId(string service, string rawModelId)
    {
        ArgumentNullException.ThrowIfNull(service, "service");
        ArgumentNullException.ThrowIfNull(rawModelId, "rawModelId");
        string modelId = rawModelId.Trim().ToLowerInvariant();
        int num = modelId.LastIndexOf('/');
        if (num >= 0 && num < modelId.Length - 1)
        {
            modelId = modelId.Substring(num + 1);
        }
        (string, string, string[])[] modelFamilies = ModelFamilies;
        for (int i = 0; i < modelFamilies.Length; i++)
        {
            (string, string, string[]) tuple = modelFamilies[i];
            string item = tuple.Item2;
            string[] item2 = tuple.Item3;
            if (item2.Any((string prefix) => modelId.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return item;
            }
        }
        string text = service.Trim().ToLowerInvariant();
        string result;
        switch (text)
        {
            case "codex":
                result = "openai";
                break;
            case "claude":
            case "claude code":
                result = "anthropic";
                break;
            default:
                result = null;
                break;
        }
        return result;
    }
}
