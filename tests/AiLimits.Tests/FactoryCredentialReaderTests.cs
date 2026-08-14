// SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Infrastructure.Providers.Droid;

namespace AiLimits.Tests;

public sealed class FactoryCredentialReaderTests
{
    private const string Key = "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=";
    private const string Envelope =
        "CQkJCQkJCQkJCQkJCQkJCQ==:Zy0oxukNWUlRKdRsZERo1g==:uajsVgwTRwQITCbS1kwMVv4i072iHVxrvH1CtrIos/fnBZx4JAcul5hZYuSeqMoS6byLJdUOJdFClAWkW1QJbED05j6cMw1GDK/E2knhaiuVO/u7S44zvmWxkGhULnGSuiEZRjUd0Q6W4sSDhw==";

    [Fact]
    public async Task ReaderReusesTheFactoryCliOAuthSession()
    {
        var path = Path.Combine(Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(path, "auth.v2.key"), Key);
            await File.WriteAllTextAsync(Path.Combine(path, "auth.v2.file"), Envelope);

            var credential = await new FactoryCredentialReader(path).ReadAsync(default);

            Assert.NotNull(credential);
            Assert.Equal("test-access-token", credential.AccessToken);
            Assert.Equal("test-refresh-token", credential.RefreshToken);
            Assert.Equal("org_test", credential.OrganizationId);
            Assert.Null(credential.Email);
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task JwtEmailIsShownOnTheFactoryAccountWithoutAProfileRequest()
    {
        var path = Path.Combine(Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            const string email = "factory.user@example.com";
            var accessToken = CreateJwt(email);
            await WriteEncryptedCredentialAsync(path, accessToken);

            var credential = await new FactoryCredentialReader(path).ReadAsync(default);
            var adapter = new DroidProviderAdapter(new HttpClient(), new FixedClock(), path);
            var account = Assert.Single(await adapter.DiscoverAccountsAsync(default));

            Assert.NotNull(credential);
            Assert.Equal(email, credential.Email);
            Assert.Equal(email, account.Login);
            Assert.IsType<FactoryLogTokenSource>(Assert.Single(adapter.CreateTokenSources(account)));
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    private static async Task WriteEncryptedCredentialAsync(string path, string accessToken)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cleartext = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                access_token = accessToken,
                refresh_token = "refresh-token",
                active_organization_id = "org_test",
            }
        );
        var ciphertext = new byte[cleartext.Length];
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, cleartext, ciphertext, tag);
        }

        await File.WriteAllTextAsync(Path.Combine(path, "auth.v2.key"), Convert.ToBase64String(key));
        await File.WriteAllTextAsync(
            Path.Combine(path, "auth.v2.file"),
            string.Join(
                ':',
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag),
                Convert.ToBase64String(ciphertext)
            )
        );
    }

    private static string CreateJwt(string email)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { email }));
        return $"{header}.{payload}.display-only";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
