// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AiLimits.Application.Presentation;

/// <summary>
/// Single source of truth for provider/vendor accent colors so the same tool
/// is the same color everywhere (cards, connections, charts, tray, usage
/// breakdowns). Brand hexes verified against the providers' own sites
/// (2026-07-22): claude.com #D97757, factory.ai #EE6018, ampcode.com logo
/// #F34E3F. Cursor/OpenCode have monochrome brands; their dashboard
/// colors were chosen with the user (Cursor neutral, OpenCode TUI peach).
/// Unknown keys get a stable, unique generated color so new
/// providers/vendors never collapse into one gray.
/// </summary>
public static class ProviderColors
{
    public const string Codex = "#2E8B68";
    public const string Claude = "#D97757";
    public const string OpenCode = "#FAB283";
    public const string Factory = "#EE6018";
    public const string Copilot = "#5E6AD2";
    // User preference: dark green/emerald (ampcode.com's dark theme is #091C1E
    // deep teal-green; the #F34E3F logo mark was rejected as the accent).
    public const string Amp = "#047857";
    public const string Antigravity = "#4285F4";
    public const string Cursor = "#E7E5E4";
    // ClinePass blue (CodexBar's ClinePass accent).
    public const string Cline = "#61A3FA";
    public const string Neutral = "#7E878B";

    // Keyed by Normalize(): lowercase letters+digits only, so "Z.AI", "z-ai"
    // and "zai" all land on the same entry.
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        // Tool / provider ids (ProviderDescriptor.Id values).
        ["codex"] = Codex,
        ["claude"] = Claude,
        ["claudecode"] = Claude,
        ["opencode"] = OpenCode,
        ["droid"] = Factory,
        ["factory"] = Factory,
        ["copilot"] = Copilot,
        ["amp"] = Amp,
        ["antigravity"] = Antigravity,
        ["cursor"] = Cursor,
        ["cline"] = Cline,
        ["clinepass"] = Cline,
        // Auth-provider / model-vendor names share the family color.
        ["openai"] = Codex,
        ["chatgpt"] = Codex,
        ["anthropic"] = Claude,
        ["google"] = Antigravity,
        ["googleantigravity"] = Antigravity,
        ["githubcopilot"] = Copilot,
        ["github"] = Copilot,
        ["opencodego"] = OpenCode,
        ["opencodezen"] = OpenCode,
        ["xai"] = "#9B7EDE",
        ["zai"] = "#3AAFA9",
        ["zhipuai"] = "#3AAFA9",
        ["minimax"] = "#E76F51",
        ["moonshotai"] = "#4E9CF5",
        ["chutes"] = "#D4A72C",
    };

    /// <summary>
    /// Resolves an accent for any provider/vendor key. Tries the key, then the
    /// (possibly localized or suffixed) display label by longest known prefix
    /// ("OpenAI (ChatGPT OAuth)" → openai), then derives a stable unique color.
    /// </summary>
    public static string Resolve(string? key, string? label = null)
    {
        string normalizedKey = Normalize(key);
        if (normalizedKey.Length > 0 && Known.TryGetValue(normalizedKey, out string? exact))
        {
            return exact;
        }
        string? byPrefix = MatchByPrefix(normalizedKey) ?? MatchByPrefix(Normalize(label));
        if (byPrefix is not null)
        {
            return byPrefix;
        }
        string seed = normalizedKey.Length > 0 ? normalizedKey : Normalize(label);
        return seed.Length == 0 ? Neutral : Derived(seed);
    }

    private static string? MatchByPrefix(string normalized)
    {
        if (normalized.Length == 0)
        {
            return null;
        }
        string? best = null;
        int bestLength = 0;
        foreach (KeyValuePair<string, string> entry in Known)
        {
            if (entry.Key.Length > bestLength && normalized.StartsWith(entry.Key, StringComparison.Ordinal))
            {
                best = entry.Value;
                bestLength = entry.Key.Length;
            }
        }
        return best;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        StringBuilder builder = new StringBuilder(value.Length);
        foreach (char c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    // Deterministic pleasant color for unknown keys: FNV-1a hash spun around
    // the hue wheel by the golden angle, at fixed saturation/lightness tuned
    // to stay readable on both dark and light surfaces.
    private static string Derived(string seed)
    {
        uint hash = 2166136261;
        foreach (char c in seed)
        {
            hash = (hash ^ c) * 16777619;
        }
        double hue = hash % 360u * 137.508 % 360.0;
        (byte r, byte g, byte b) = HslToRgb(hue, 0.55, 0.62);
        return string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
    }

    private static (byte R, byte G, byte B) HslToRgb(double hue, double saturation, double lightness)
    {
        double chroma = (1.0 - Math.Abs(2.0 * lightness - 1.0)) * saturation;
        double x = chroma * (1.0 - Math.Abs(hue / 60.0 % 2.0 - 1.0));
        double m = lightness - chroma / 2.0;
        (double r, double g, double b) = (int)(hue / 60.0) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return ((byte)Math.Round((r + m) * 255.0), (byte)Math.Round((g + m) * 255.0), (byte)Math.Round((b + m) * 255.0));
    }
}
