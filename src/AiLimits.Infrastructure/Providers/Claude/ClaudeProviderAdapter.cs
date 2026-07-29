// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using System.ComponentModel;
using System.Text.Json;

namespace AiLimits.Infrastructure.Providers.Claude;

public sealed class ClaudeProviderAdapter : IProviderAdapter
{
    private readonly HttpClient _httpClient;

    private readonly IClock _clock;

    private readonly string _claudeHome;

    private readonly IProcessRunner? _processRunner;

    private readonly string? _claudeExecutable;

    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Claude;

    public ClaudeProviderAdapter(HttpClient httpClient, IClock clock, string? claudeHome = null, IProcessRunner? processRunner = null, string? claudeExecutable = null)
    {
        _httpClient = httpClient;
        _clock = clock;
        _claudeHome = claudeHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _processRunner = processRunner;
        _claudeExecutable = claudeExecutable ?? FindClaudeExecutable();
    }

    public async Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        CliCredential credential = await CliCredentialReader.ReadClaudeAsync(Path.Combine(_claudeHome, ".credentials.json"), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProviderAccount> result;
        if (credential is not null)
        {
            string? statusEmail = await ReadAuthStatusEmailAsync(cancellationToken).ConfigureAwait(false);
            string? login = statusEmail ?? credential.Login;
            IReadOnlyList<ProviderAccount> readOnlyList = new ProviderAccount[] { new ProviderAccount(new AccountKey(Descriptor.Id, credential.AccountId), login ?? "Claude Code account", login, "Claude Code OAuth", 1L, IsConnected: true) };
            result = readOnlyList;
        }
        else
        {
            IReadOnlyList<ProviderAccount> readOnlyList = Array.Empty<ProviderAccount>();
            result = readOnlyList;
        }
        return result;
    }

    private async Task<string?> ReadAuthStatusEmailAsync(CancellationToken cancellationToken)
    {
        if (_processRunner is null || string.IsNullOrWhiteSpace(_claudeExecutable))
        {
            return null;
        }
        try
        {
            ProcessResult result = await _processRunner.RunAsync(_claudeExecutable, new[] { "auth", "status", "--json" }, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || result.OutputTruncated)
            {
                return null;
            }
            string output = result.StandardOutput.Trim();
            int start = output.IndexOf('{');
            int end = output.LastIndexOf('}');
            if (start < 0 || end < start)
            {
                return null;
            }
            using JsonDocument document = JsonDocument.Parse(output.Substring(start, end - start + 1));
            return document.RootElement.TryGetProperty("email", out JsonElement email)
                && email.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(email.GetString())
                ? email.GetString()!.Trim()
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or JsonException)
        {
            return null;
        }
    }

    private static string? FindClaudeExecutable()
    {
        string? configured = Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH")?.Trim();
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
        {
            return configured;
        }
        // .cmd/.bat shims are deliberately absent: ProcessRunner starts the
        // process with UseShellExecute = false, which cannot launch them.
        string[] names = OperatingSystem.IsWindows() ? new[] { "claude.exe" } : new[] { "claude" };
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string name in names)
            {
                string path = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        return null;
    }

    public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account)
    {
        return new ILimitFetchStrategy[] { new ClaudeOAuthLimitStrategy(_httpClient, _clock, account, Path.Combine(_claudeHome, ".credentials.json")) };
    }

    public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account)
    {
        return new ITokenUsageSource[] { new ClaudeJsonlTokenSource(_claudeHome) };
    }
}
