// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Infrastructure.Providers.Claude;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Tests;

public sealed class ClaudeProviderAdapterTests
{
    [Fact]
    public async Task AuthStatusEmailIsUsedForTheDiscoveredAccount()
    {
        string home = Path.Combine(Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(home, ".credentials.json"),
                """{"claudeAiOauth":{"accessToken":"opaque-test-token"}}""");
            var runner = new FakeRunner("""{"loggedIn":true,"email":"dev@example.com","orgName":"Acme"}""");
            var adapter = new ClaudeProviderAdapter(new HttpClient(), new FixedClock(), home, runner, "claude-test");

            var account = Assert.Single(await adapter.DiscoverAccountsAsync(default));

            Assert.Equal("dev@example.com", account.Login);
            Assert.Equal("dev@example.com", account.DisplayName);
            Assert.Equal(new[] { "auth", "status", "--json" }, runner.Arguments);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeRunner(string output) : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Arguments = arguments;
            return Task.FromResult(new ProcessResult(0, output, string.Empty, TimeSpan.Zero));
        }
    }
}
