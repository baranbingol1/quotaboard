// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AiLimits.Application.Preferences;

/// <summary>
/// Which providers appear as Overview cards. Only hidden provider ids are
/// stored, so newly detected providers default to visible and a temporary
/// discovery failure can never reset anyone's choice. Hiding is presentation
/// only: hidden providers keep refreshing, scanning, and appearing in
/// Connections and Usage.
/// </summary>
public sealed class ProviderVisibilitySet
{
    public static readonly ProviderVisibilitySet Empty = new ProviderVisibilitySet(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    );

    private readonly HashSet<string> _hidden;

    private ProviderVisibilitySet(HashSet<string> hidden)
    {
        _hidden = hidden;
    }

    public IReadOnlyCollection<string> HiddenProviders => _hidden;

    public bool IsVisible(string providerId)
    {
        return string.IsNullOrWhiteSpace(providerId) || !_hidden.Contains(providerId.Trim());
    }

    public ProviderVisibilitySet WithVisibility(string providerId, bool visible)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return this;
        }
        HashSet<string> hidden = new HashSet<string>(_hidden, StringComparer.OrdinalIgnoreCase);
        if (visible)
        {
            hidden.Remove(providerId.Trim());
        }
        else
        {
            hidden.Add(providerId.Trim());
        }
        return new ProviderVisibilitySet(hidden);
    }

    public static ProviderVisibilitySet Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("hidden", out JsonElement hiddenElement)
                || hiddenElement.ValueKind != JsonValueKind.Array
            )
            {
                return Empty;
            }
            HashSet<string> hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement entry in hiddenElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
                {
                    hidden.Add(entry.GetString()!.Trim());
                }
            }
            return hidden.Count == 0 ? Empty : new ProviderVisibilitySet(hidden);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public string Serialize()
    {
        return JsonSerializer.Serialize(
            new Dictionary<string, string[]>
            {
                ["hidden"] = _hidden.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            }
        );
    }
}
