// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Snapshots;

public sealed class SnapshotMerger
{
    private static readonly TimeSpan NewMeterBadgeDuration = TimeSpan.FromDays(7);

    public ProviderSnapshot Merge(ProviderSnapshot? previous, ProviderSnapshot incoming)
    {
        if (previous is null)
        {
            return incoming with
            {
                Meters = incoming.Meters.Select((UsageMeter meter) => MarkFirstObservation(meter, incoming.ObservedAt)).ToArray()
            };
        }
        if (previous.Account != incoming.Account)
        {
            throw new InvalidOperationException("Snapshots from different accounts cannot be merged.");
        }
        Dictionary<MeterKey, UsageMeter> dictionary = previous.Meters.ToDictionary((UsageMeter meter) => meter.Key);
        List<UsageMeter> list = new List<UsageMeter>(incoming.Meters.Count + previous.Meters.Count);
        foreach (UsageMeter meter in incoming.Meters)
        {
            if (dictionary.Remove(meter.Key, out var value))
            {
                DateTimeOffset dateTimeOffset = value.FirstObservedAt ?? previous.ObservedAt;
                list.Add(meter with
                {
                    FirstObservedAt = dateTimeOffset,
                    IsNew = (incoming.ObservedAt - dateTimeOffset <= NewMeterBadgeDuration)
                });
            }
            else
            {
                list.Add(MarkFirstObservation(meter, incoming.ObservedAt));
            }
        }
        if (incoming.Completeness == SnapshotCompleteness.Partial)
        {
            list.AddRange(dictionary.Values.Select((UsageMeter prior) => prior with
            {
                Status = MeterStatus.Stale,
                IsNew = false
            }));
        }
        return incoming with
        {
            Meters = list
        };
    }

    private static UsageMeter MarkFirstObservation(UsageMeter meter, DateTimeOffset observedAt)
    {
        return meter with
        {
            FirstObservedAt = observedAt,
            IsNew = true
        };
    }
}
