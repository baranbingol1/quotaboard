// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Domain;

public sealed record ProviderAccount(
    AccountKey Key,
    string DisplayName,
    string? Login,
    string AuthSource,
    long ConfigurationRevision,
    bool IsConnected,
    DateTimeOffset? LastSuccessfulRefreshAt = null
);

public sealed record ProviderDescriptor(
    ProviderId Id,
    string DisplayName,
    string AccentColor,
    bool SupportsMultipleAccounts,
    bool SupportsExactTokens,
    string TokenCoverageDescription,
    IReadOnlyList<string> AuthenticationCapabilities
);
