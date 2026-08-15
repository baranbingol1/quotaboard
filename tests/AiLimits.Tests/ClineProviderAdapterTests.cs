// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Cline;

namespace AiLimits.Tests;

[Collection("ClineEnv")]
public sealed class ClineProviderAdapterTests
{
    [Fact]
    public async Task Local_logs_alone_connect_the_account()
    {
        using var temp = new TempDir();
        var adapter = new ClineProviderAdapter(
            new HttpClient(),
            new FixedClock(),
            roots: [temp.Path],
            credentialProbe: () => null
        );

        ProviderAccount account = Assert.Single(await adapter.DiscoverAccountsAsync(default));

        Assert.True(account.IsConnected);
        Assert.Equal("cline", account.Key.Provider.Value);
        Assert.Equal("default", account.Key.Value);
        Assert.Equal("Cline", account.DisplayName);
        Assert.Equal("Local logs", account.AuthSource);
        ITokenUsageSource source = Assert.Single(adapter.CreateTokenSources(account));
        Assert.Equal("cline.local-history", source.Id);
    }

    [Fact]
    public async Task A_credential_alone_connects_the_account_and_labels_the_source()
    {
        var adapter = new ClineProviderAdapter(
            new HttpClient(),
            new FixedClock(),
            roots: [],
            credentialProbe: () => new ClineCredential("token", "API key (CLINE_API_KEY)")
        );

        ProviderAccount account = Assert.Single(await adapter.DiscoverAccountsAsync(default));

        Assert.True(account.IsConnected);
        Assert.Equal("API key (CLINE_API_KEY)", account.AuthSource);
        Assert.Empty(adapter.CreateTokenSources(account));

        ILimitFetchStrategy strategy = Assert.Single(adapter.CreateLimitStrategies(account));
        Assert.Equal("cline.pass-usage-limits-api", strategy.Id);
        Assert.Equal(10, strategy.Order);
        Assert.Equal(
            StrategyAvailability.Available,
            (await strategy.CheckAvailabilityAsync(account, default)).Availability
        );
    }

    [Fact]
    public async Task Nothing_discovered_marks_the_account_disconnected()
    {
        var adapter = new ClineProviderAdapter(
            new HttpClient(),
            new FixedClock(),
            roots: [],
            credentialProbe: () => null
        );

        ProviderAccount account = Assert.Single(await adapter.DiscoverAccountsAsync(default));

        Assert.False(account.IsConnected);
        Assert.Equal("Local logs", account.AuthSource);
        ILimitFetchStrategy strategy = Assert.Single(adapter.CreateLimitStrategies(account));
        Assert.Equal(
            StrategyAvailability.NotConfigured,
            (await strategy.CheckAvailabilityAsync(account, default)).Availability
        );
    }

    [Fact]
    public async Task Environment_api_key_wins_over_the_cli_secrets_file()
    {
        string? original = Environment.GetEnvironmentVariable("CLINE_API_KEY");
        Environment.SetEnvironmentVariable("CLINE_API_KEY", "env-token");
        try
        {
            // No probe: the real reader runs and must prefer the environment even
            // on machines where the Cline CLI secrets file exists.
            var adapter = new ClineProviderAdapter(new HttpClient(), new FixedClock(), roots: []);
            ProviderAccount account = Assert.Single(await adapter.DiscoverAccountsAsync(default));
            Assert.True(account.IsConnected);
            Assert.Equal("API key (CLINE_API_KEY)", account.AuthSource);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLINE_API_KEY", original);
        }
    }

    [Fact]
    public void Secrets_file_yields_the_cli_account_label()
    {
        using var temp = new TempDir();
        string secrets = Path.Combine(temp.Path, "secrets.json");
        File.WriteAllText(
            secrets,
            "{\"cline:clineAccountId\":\"opaque-token\",\"openai-codex-oauth-credentials\":\"xyz\"}"
        );

        ClineCredential? credential = ClineCredentialReader.ResolveSecrets(secrets);

        Assert.NotNull(credential);
        Assert.Equal("opaque-token", credential.Token);
        Assert.Equal("Cline CLI account", credential.SourceLabel);
        Assert.False(credential.IsWorkOsSession);
    }

    [Fact]
    public void Session_blob_resolves_the_id_token_not_the_blob()
    {
        using var temp = new TempDir();
        string secrets = Path.Combine(temp.Path, "secrets.json");
        // The blob's userInfo can hold non-ASCII (display names); only the
        // ASCII idToken may ride in the Authorization header.
        string blob =
            "{\"idToken\":\"header.payload.signature\",\"refreshToken\":\"r1\","
            + "\"userInfo\":{\"displayName\":\"Gökçe Yılmaz\",\"email\":\"someone@example.com\"},"
            + "\"expiresAt\":1784772484}";
        File.WriteAllText(secrets, "{\"cline:clineAccountId\":" + JsonSerializer.Serialize(blob) + "}");

        ClineCredential? credential = ClineCredentialReader.ResolveSecrets(secrets);

        Assert.NotNull(credential);
        Assert.Equal("header.payload.signature", credential.Token);
        Assert.Equal("Cline CLI account", credential.SourceLabel);
        Assert.True(credential.IsWorkOsSession);
        Assert.Equal("someone@example.com", credential.Email);
    }

    [Fact]
    public async Task The_signed_in_email_labels_the_account()
    {
        var adapter = new ClineProviderAdapter(
            new HttpClient(),
            new FixedClock(),
            roots: [],
            credentialProbe: () =>
                new ClineCredential(
                    "header.payload.signature",
                    "Cline CLI account",
                    IsWorkOsSession: true,
                    Email: "someone@example.com"
                )
        );

        ProviderAccount account = Assert.Single(await adapter.DiscoverAccountsAsync(default));

        Assert.Equal("someone@example.com", account.Login);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"cline:clineAccountId\":\"   \"}")]
    [InlineData("{\"cline:clineAccountId\":42}")]
    [InlineData("{\"cline:clineAccountId\":\"{\\\"idToken\\\":\\\"tökén\\\"}\"}")]
    [InlineData("{\"cline:clineAccountId\":\"{\\\"idToken\\\":\\\"has space\\\"}\"}")]
    public void Unusable_secrets_files_do_not_resolve(string content)
    {
        using var temp = new TempDir();
        string secrets = Path.Combine(temp.Path, "secrets.json");
        File.WriteAllText(secrets, content);

        Assert.Null(ClineCredentialReader.ResolveSecrets(secrets));
        Assert.Null(ClineCredentialReader.ResolveSecrets(Path.Combine(temp.Path, "missing.json")));
    }

    [Fact]
    public void Environment_variables_trim_and_prefer_cline_api_key()
    {
        string? cline = Environment.GetEnvironmentVariable("CLINE_API_KEY");
        string? pass = Environment.GetEnvironmentVariable("CLINEPASS_API_KEY");
        Environment.SetEnvironmentVariable("CLINE_API_KEY", "  \"quoted-token\"  ");
        Environment.SetEnvironmentVariable("CLINEPASS_API_KEY", "pass-token");
        try
        {
            ClineCredential? credential = ClineCredentialReader.ResolveEnvironment();
            Assert.NotNull(credential);
            Assert.Equal("quoted-token", credential.Token);
            Assert.Equal("API key (CLINE_API_KEY)", credential.SourceLabel);
            Assert.False(credential.IsWorkOsSession);

            Environment.SetEnvironmentVariable("CLINE_API_KEY", null);
            credential = ClineCredentialReader.ResolveEnvironment();
            Assert.NotNull(credential);
            Assert.Equal("pass-token", credential.Token);
            Assert.Equal("API key (CLINEPASS_API_KEY)", credential.SourceLabel);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLINE_API_KEY", cline);
            Environment.SetEnvironmentVariable("CLINEPASS_API_KEY", pass);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
