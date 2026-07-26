// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Application.Pricing;

/// <summary>
/// Everything the Settings card needs to describe the model catalog: whether
/// it loaded, how much it covers, when it was last confirmed current, when it
/// is next due, and why the last attempt failed if it did.
/// </summary>
/// <param name="FetchedAt">When the catalog was last downloaded or revalidated against the server.</param>
/// <param name="LastError">Reason the most recent attempt failed, or null when it succeeded.</param>
public sealed record ModelCatalogStatus(
    bool IsAvailable,
    int ModelCount,
    string Hash,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? NextDue,
    DateTimeOffset? LastAttemptAt,
    string? LastError)
{
    public static ModelCatalogStatus Unavailable(string? lastError, DateTimeOffset? lastAttemptAt) =>
        new(false, 0, string.Empty, null, null, lastAttemptAt, lastError);

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    public string ShortHash => Hash.Length <= 8 ? Hash : Hash[..8];
}
