// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Domain;

public enum ResetCreditStatus
{
    Unknown,
    Available,
    Redeeming,
    Redeemed,
    Expired,
}

/// <summary>
/// A provider-granted, redeemable rate-limit reset (for example OpenAI's
/// Codex "limit reset credits"). Provider-agnostic: any adapter that exposes
/// such an inventory can surface it.
/// </summary>
public sealed record ResetCredit(
    string Id,
    string Kind,
    ResetCreditStatus Status,
    DateTimeOffset? GrantedAt,
    DateTimeOffset? ExpiresAt,
    string? Title,
    string? Description
);

public sealed record ResetCreditInventory(IReadOnlyList<ResetCredit> Credits, DateTimeOffset ObservedAt)
{
    // Availability is recomputed at render time rather than trusting a
    // server-side count, so credits that expired since the fetch drop out.
    public IReadOnlyList<ResetCredit> Available(DateTimeOffset now)
    {
        return Credits
            .Where(
                (ResetCredit credit) =>
                    credit.Status == ResetCreditStatus.Available
                    && (!credit.ExpiresAt.HasValue || credit.ExpiresAt.Value > now)
            )
            .OrderBy((ResetCredit credit) => credit.ExpiresAt ?? DateTimeOffset.MaxValue)
            .ThenBy((ResetCredit credit) => credit.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public int AvailableCount(DateTimeOffset now)
    {
        return Available(now).Count;
    }

    public DateTimeOffset? NextExpiry(DateTimeOffset now)
    {
        return Available(now).FirstOrDefault((ResetCredit credit) => credit.ExpiresAt.HasValue)?.ExpiresAt;
    }
}
