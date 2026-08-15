// SPDX-License-Identifier: Apache-2.0
using System.Text.RegularExpressions;

namespace AiLimits.Application.Diagnostics;

/// <summary>
/// Strips credentials out of text that is about to be persisted to the local
/// database, written to a log, or shown on the diagnostics page.
///
/// The rule is deliberately inverted from "redact these four prefixes": a
/// diagnostic keeps only what still reads as prose, a URL, a status code or a
/// short identifier. Anything shaped like a secret goes, whether or not the
/// surrounding key name was one somebody thought to enumerate — because the
/// strings flowing through here include provider error bodies nobody controls.
///
/// Three layers, applied in order:
///   1. A value following a credential-ish key, in any of the syntaxes these
///      messages actually arrive in: <c>k=v</c>, <c>k: v</c>, <c>"k":"v"</c>.
///   2. A value following an HTTP auth scheme (Bearer, Basic, token, ...).
///   3. Any remaining free-standing JWT or long opaque blob.
/// </summary>
public static partial class DiagnosticRedactor
{
    public const string Placeholder = "[redacted]";

    /// <summary>Diagnostics are for humans; anything longer is noise.</summary>
    public const int DefaultMaxLength = 500;

    /// <param name="maxLength">
    /// Where to truncate. The default suits a message shown in the UI; a crash
    /// log passes something larger so a stack trace survives.
    /// </param>
    public static string Redact(string? message, int maxLength = DefaultMaxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        if (string.IsNullOrWhiteSpace(message))
        {
            return "No diagnostic was provided.";
        }

        // Scheme first: "Authorization: Bearer <token>" matches both rules, and
        // only this order leaves the scheme name readable in the output.
        string text = SchemeSecret().Replace(message, m => m.Groups["scheme"].Value + " " + Placeholder);
        text = KeyedSecret().Replace(text, m => m.Groups["lead"].Value + Placeholder);
        text = Jwt().Replace(text, Placeholder);
        text = OpaqueBlob().Replace(text, m => LooksLikeSecret(m.Value) ? Placeholder : m.Value);

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    /// <summary>
    /// A long run of token characters is only redacted when it also looks
    /// high-entropy. Without this, ordinary long words, hyphenated model slugs
    /// and file paths would be destroyed along with the secrets.
    /// </summary>
    private static bool LooksLikeSecret(string candidate)
    {
        bool digit = false,
            upper = false,
            lower = false;
        foreach (char c in candidate)
        {
            if (char.IsAsciiDigit(c))
                digit = true;
            else if (char.IsAsciiLetterUpper(c))
                upper = true;
            else if (char.IsAsciiLetterLower(c))
                lower = true;
        }
        // Mixed case plus digits is the signature of a generated credential.
        // A 40+ character run is treated as one even without mixed case, which
        // covers lowercase hex digests and API keys such as sk-... .
        return (digit && upper && lower) || candidate.Length >= 40;
    }

    /// <summary>
    /// key=value / key: value / "key": "value", where the key contains any of
    /// the words a credential is realistically named after. Covers the JSON
    /// colon form, api_key, x-api-key, session and cookie tokens. A value that
    /// is itself an auth scheme is skipped, because <see cref="SchemeSecret"/>
    /// has already redacted what followed it.
    /// </summary>
    [GeneratedRegex(
        """(?<lead>"?[A-Za-z0-9_.\-]*(?:token|secret|password|passwd|pwd|api[_\-]?key|apikey|auth|credential|cookie|session|signature|sig)[A-Za-z0-9_.\-]*"?\s*[:=]\s*"?)(?<value>(?!(?:Bearer|Basic|Digest|Token|ApiKey|SSWS|OAuth)\b)[^\s"',;&}\]]+)""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex KeyedSecret();

    /// <summary>An HTTP authorization scheme and the credential after it.</summary>
    [GeneratedRegex(
        @"(?<scheme>\b(?:Bearer|Basic|Digest|Token|ApiKey|SSWS|OAuth))\s+(?<value>[^\s"",;&}\]]+)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SchemeSecret();

    /// <summary>A three-segment base64url JWT, wherever it appears.</summary>
    [GeneratedRegex(@"\b[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b")]
    private static partial Regex Jwt();

    /// <summary>A free-standing run long enough to be a credential.</summary>
    [GeneratedRegex(@"[A-Za-z0-9+/_-]{32,}={0,2}")]
    private static partial Regex OpaqueBlob();
}
