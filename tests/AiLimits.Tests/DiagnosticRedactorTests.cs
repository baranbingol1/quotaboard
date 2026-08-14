// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Diagnostics;

namespace AiLimits.Tests;

/// <summary>
/// Diagnostics are persisted to the local database and rendered on the
/// diagnostics page, and they carry provider error bodies nobody controls.
/// Anything credential-shaped has to be gone by the time it lands there.
/// </summary>
public sealed class DiagnosticRedactorTests
{
    private const string Jwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    [Theory]
    // Query-string and form syntax.
    [InlineData(
        "GET /v1/usage?access_token=sk_live_9Xa2Kd8Lm3Qp7Rt1Zv4Bn6Hj failed",
        "sk_live_9Xa2Kd8Lm3Qp7Rt1Zv4Bn6Hj"
    )]
    [InlineData("refresh_token=aB3dE5gH7jK9lM1nO2pQ was rejected", "aB3dE5gH7jK9lM1nO2pQ")]
    // JSON colon form - the denylist this replaced missed all of these.
    [InlineData("""{"api_key":"aB3dE5gH7jK9lM1nO2pQ4rS6"}""", "aB3dE5gH7jK9lM1nO2pQ4rS6")]
    [InlineData("""{"x-api-key": "Kd8Lm3Qp7Rt1Zv4Bn6Hj0Xa2"}""", "Kd8Lm3Qp7Rt1Zv4Bn6Hj0Xa2")]
    [InlineData("""{"sessionToken":"Zv4Bn6Hj0Xa2Kd8Lm3Qp7Rt1"}""", "Zv4Bn6Hj0Xa2Kd8Lm3Qp7Rt1")]
    [InlineData("""{"clientSecret":"Qp7Rt1Zv4Bn6Hj0Xa2Kd8Lm3"}""", "Qp7Rt1Zv4Bn6Hj0Xa2Kd8Lm3")]
    // Header syntax.
    [InlineData("Authorization: Basic dXNlcjpodW50ZXIyaHVudGVyMg==", "dXNlcjpodW50ZXIyaHVudGVyMg==")]
    [InlineData("Authorization: Bearer aB3dE5gH7jK9lM1nO2pQ", "aB3dE5gH7jK9lM1nO2pQ")]
    [InlineData("Cookie: session=Hj0Xa2Kd8Lm3Qp7Rt1Zv4Bn6; Path=/", "Hj0Xa2Kd8Lm3Qp7Rt1Zv4Bn6")]
    [InlineData("Set-Cookie: __Secure-authjs.session-token=Lm3Qp7Rt1Zv4Bn6Hj0Xa2Kd8", "Lm3Qp7Rt1Zv4Bn6Hj0Xa2Kd8")]
    // Free-standing, with no key name to key off at all.
    [InlineData("upstream said: " + Jwt, Jwt)]
    [InlineData("token rejected (3fA9dK2mPq7Zx1Lv8Rt4Bn6Hj0Xa2Kd8Lm3Qp7R)", "3fA9dK2mPq7Zx1Lv8Rt4Bn6Hj0Xa2Kd8Lm3Qp7R")]
    public void Credential_shaped_text_never_survives(string message, string secret)
    {
        string redacted = DiagnosticRedactor.Redact(message);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Cline returned HTTP 429 (rate limited); retry after 30s.")]
    [InlineData("No Cline credential. Set CLINE_API_KEY or sign in with the Cline CLI.")]
    [InlineData("Could not read C:\\Users\\example\\AppData\\Roaming\\Code\\User\\globalStorage")]
    [InlineData("claude-opus-4-5 is not in the pricing catalog.")]
    [InlineData("GET https://api.cline.bot/api/v1/users/me/plan/usage-limits timed out after 15s.")]
    public void Ordinary_diagnostics_are_left_readable(string message)
    {
        Assert.Equal(message, DiagnosticRedactor.Redact(message));
    }

    [Fact]
    public void Every_occurrence_is_replaced_not_just_the_first()
    {
        string redacted = DiagnosticRedactor.Redact(
            "access_token=aB3dE5gH7jK9lM1nO2pQ and refresh_token=Zv4Bn6Hj0Xa2Kd8Lm3Qp7Rt1"
        );

        Assert.DoesNotContain("aB3dE5gH7jK9lM1nO2pQ", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Zv4Bn6Hj0Xa2Kd8Lm3Qp7Rt1", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_name_is_kept_so_the_diagnostic_still_says_what_failed()
    {
        string redacted = DiagnosticRedactor.Redact("""{"api_key":"aB3dE5gH7jK9lM1nO2pQ4rS6"}""");

        Assert.Contains("api_key", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_text_becomes_a_stated_absence(string? message)
    {
        Assert.Equal("No diagnostic was provided.", DiagnosticRedactor.Redact(message));
    }

    [Fact]
    public void Output_is_capped_so_a_dumped_response_body_cannot_flood_the_page()
    {
        string wordy = string.Join(' ', Enumerable.Repeat("the upstream service returned an error", 40));
        Assert.True(wordy.Length > 500);

        Assert.Equal(500, DiagnosticRedactor.Redact(wordy).Length);
    }

    [Fact]
    public void An_authorization_header_keeps_its_scheme_and_loses_its_credential()
    {
        Assert.Equal(
            "Authorization: Bearer " + DiagnosticRedactor.Placeholder,
            DiagnosticRedactor.Redact("Authorization: Bearer aB3dE5gH7jK9lM1nO2pQ")
        );
    }
}
