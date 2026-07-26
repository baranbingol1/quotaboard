// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Presentation;
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Providers;

public static class BuiltInProviderDescriptors
{
    public static readonly ProviderDescriptor Codex = new ProviderDescriptor(new ProviderId("codex"), "Codex", ProviderColors.Codex, SupportsMultipleAccounts: true, SupportsExactTokens: true, "Exact local session tokens when Codex telemetry exists.", new string[] { "CLI OAuth", "Codex app-server", "Isolated WebView2" });

    public static readonly ProviderDescriptor Claude = new ProviderDescriptor(new ProviderId("claude"), "Claude Code", ProviderColors.Claude, SupportsMultipleAccounts: true, SupportsExactTokens: true, "Exact local JSONL tokens with streaming-update deduplication.", new string[] { "CLI OAuth", "ConPTY /usage", "Isolated WebView2" });

    public static readonly ProviderDescriptor OpenCode = new ProviderDescriptor(new ProviderId("opencode"), "OpenCode", ProviderColors.OpenCode, SupportsMultipleAccounts: true, SupportsExactTokens: true, "Local CLI history only. Provider and model usage is read without assuming an OpenCode Go or Zen subscription.", new string[] { "Local usage database", "Existing provider auth metadata" });

    public static readonly ProviderDescriptor Droid = new ProviderDescriptor(new ProviderId("droid"), "Factory", ProviderColors.Factory, SupportsMultipleAccounts: true, SupportsExactTokens: true, "Factory quota limits plus exact local Droid response-token telemetry. An API key is optional.", new string[] { "Existing Factory CLI session", "Local Droid logs", "Optional API key" });

    public static readonly ProviderDescriptor Copilot = new ProviderDescriptor(new ProviderId("copilot"), "GitHub Copilot", ProviderColors.Copilot, SupportsMultipleAccounts: true, SupportsExactTokens: false, "Quota and reported AI-credit cost only; no personal token inference.", new string[] { "GitHub device authorization" });

    public static readonly ProviderDescriptor Amp = new(new ProviderId("amp"), "Amp", ProviderColors.Amp, true, true, "Exact recent thread tokens plus subscription and credit balances from the installed Amp CLI.", new[] { "Amp CLI", "Access token" });

    public static readonly ProviderDescriptor Antigravity = new(new ProviderId("antigravity"), "Google Antigravity", ProviderColors.Antigravity, false, false, "Google AI subscription quota from an existing authenticated agy session; exact token usage is not exposed.", new[] { "Existing agy session" });

    public static readonly ProviderDescriptor Cursor = new(new ProviderId("cursor"), "Cursor", ProviderColors.Cursor, false, true, "Usage limits plus exact per-request usage-event tokens from the existing local Cursor app session.", new[] { "Cursor app session" });

    public static readonly ProviderDescriptor Cline = new(new ProviderId("cline"), "Cline", ProviderColors.Cline,
        SupportsMultipleAccounts: false, SupportsExactTokens: true,
        "ClinePass plan windows from the account API plus exact per-request tokens from local Cline logs (VS Code extension and CLI).",
        new[] { "Cline CLI account", "API key (env)", "Local logs" });

    public static IReadOnlyList<ProviderDescriptor> All { get; } = new ProviderDescriptor[] { Codex, Claude, OpenCode, Droid, Copilot, Amp, Antigravity, Cursor, Cline };
}
