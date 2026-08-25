// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Application.Usage;

public enum CopilotActivityKind
{
    Session,
    Request,
}

public sealed record CopilotActivityEvent(
    string HarnessKey,
    string HarnessLabel,
    string SessionId,
    DateTimeOffset OccurredAt,
    CopilotActivityKind Kind,
    string? Model
);

public sealed record CopilotActivitySummary(
    string HarnessKey,
    string HarnessLabel,
    int Sessions,
    int Requests,
    bool UsesCompletedTurns,
    IReadOnlyList<string> Models,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen
);

public static class CopilotActivityQuery
{
    public static IReadOnlyList<CopilotActivitySummary> Run(
        IReadOnlyList<CopilotActivityEvent> events,
        DateOnly from,
        DateOnly through
    ) =>
        events
            .Where(item =>
            {
                DateOnly day = DateOnly.FromDateTime(item.OccurredAt.ToLocalTime().DateTime);
                return day >= from && day <= through;
            })
            .GroupBy(item => new { item.HarnessKey, item.HarnessLabel })
            .Select(group =>
            {
                CopilotActivityEvent[] values = group.ToArray();
                return new CopilotActivitySummary(
                    group.Key.HarnessKey,
                    group.Key.HarnessLabel,
                    values.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count(),
                    values.Count(item => item.Kind == CopilotActivityKind.Request),
                    group.Key.HarnessKey == "copilot-cli",
                    values
                        .Where(item => !string.IsNullOrWhiteSpace(item.Model))
                        .Select(item => item.Model!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    values.Min(item => item.OccurredAt),
                    values.Max(item => item.OccurredAt)
                );
            })
            .OrderBy(item => item.HarnessLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
}
