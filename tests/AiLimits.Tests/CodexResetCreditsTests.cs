// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Codex;

namespace AiLimits.Tests;

public sealed class CodexResetCreditsTests
{
    [Fact]
    public async Task OversizedResponseReturnsNullWithoutThrowing()
    {
        using var temp = new TempDir();
        var codexHome = temp.Path;
        // auth.json with account_id "acc-1" so FetchResetCreditsAsync gets past
        // the credential check.
        await File.WriteAllTextAsync(
            Path.Combine(codexHome, "auth.json"),
            "{\"tokens\":{\"access_token\":\"test-token\",\"account_id\":\"acc-1\"}}"
        );

        var oversized = "{\"credits\":[" + new string('x', 3_000_000) + "]}";
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json"),
        });

        var adapter = new CodexProviderAdapter(new HttpClient(handler), new FixedClock(), codexHome);

        var result = await adapter.FetchResetCreditsAsync(Account("acc-1"), default);
        Assert.Null(result);
    }

    private static ProviderAccount Account(string value) =>
        new(new AccountKey(new ProviderId("codex"), value), value, null, "fixture", 1, true);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
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
