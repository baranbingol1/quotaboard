// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Snapshots;

/// <summary>
/// Provides a stable, human-readable meter order that is independent of live
/// values such as usage percentage and reset time.
/// </summary>
public sealed class MeterDisplayOrderComparer : IComparer<UsageMeter>
{
    private static readonly string[] WindowPhrases =
    [
        "5-hour",
        "5 hour",
        "five-hour",
        "hourly",
        "daily",
        "weekly",
        "monthly",
        "session"
    ];

    private static readonly string[] GenericPhrases =
    [
        "limit",
        "budget",
        "quota",
        "allowance",
        "usage"
    ];

    public static MeterDisplayOrderComparer Instance { get; } = new();

    private MeterDisplayOrderComparer()
    {
    }

    public int Compare(UsageMeter? x, UsageMeter? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return 1;
        if (y is null) return -1;

        string xGroup = GroupName(x.DisplayName);
        string yGroup = GroupName(y.DisplayName);
        int comparison = EmptyFirst(xGroup, yGroup);
        if (comparison != 0) return comparison;

        comparison = WindowOrder(x).CompareTo(WindowOrder(y));
        if (comparison != 0) return comparison;

        comparison = StringComparer.OrdinalIgnoreCase.Compare(x.DisplayName, y.DisplayName);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(x.Key.Value, y.Key.Value);
    }

    private static int EmptyFirst(string x, string y)
    {
        if (x.Length == 0) return y.Length == 0 ? 0 : -1;
        if (y.Length == 0) return 1;
        return StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }

    private static string GroupName(string displayName)
    {
        string group = displayName;
        foreach (string phrase in WindowPhrases)
        {
            group = group.Replace(phrase, string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        foreach (string phrase in GenericPhrases)
        {
            group = group.Replace(phrase, string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        return string.Join(' ', group.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static int WindowOrder(UsageMeter meter)
    {
        if (meter.WindowDuration is { } duration)
        {
            if (duration <= TimeSpan.FromHours(8)) return 0;
            if (duration <= TimeSpan.FromHours(36)) return 1;
            if (duration <= TimeSpan.FromDays(8)) return 2;
            if (duration <= TimeSpan.FromDays(32)) return 3;
            return 4;
        }

        string name = meter.DisplayName;
        if (Contains(name, "5-hour") || Contains(name, "5 hour") || Contains(name, "five-hour") || Contains(name, "hourly") || Contains(name, "session")) return 0;
        if (Contains(name, "daily")) return 1;
        if (Contains(name, "weekly")) return 2;
        if (Contains(name, "monthly")) return 3;
        return 4;
    }

    private static bool Contains(string value, string phrase) =>
        value.Contains(phrase, StringComparison.OrdinalIgnoreCase);
}
