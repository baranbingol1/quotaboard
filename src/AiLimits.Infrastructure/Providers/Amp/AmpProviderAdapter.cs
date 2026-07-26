// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Infrastructure.Providers.Amp;

public sealed class AmpProviderAdapter(HttpClient http, IClock clock, IProcessRunner runner) : IProviderAdapter
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Amp;
    public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        bool cli = AmpCliStrategy.FindExecutable() is not null;
        bool api = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AMP_API_KEY"));
        IReadOnlyList<ProviderAccount> result = [new(new AccountKey(Descriptor.Id, "default"), "Amp", null, cli ? "Amp CLI" : "Access token", 1, cli || api)];
        return Task.FromResult(result);
    }
    public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account) =>
        [new AmpCliStrategy(runner, clock), new AmpApiStrategy(http, clock)];
    public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account) =>
        AmpCliStrategy.FindExecutable() is null ? [] : [new AmpThreadTokenSource(runner)];
}

internal sealed record AmpUsage(
    decimal? Remaining,
    decimal? Limit,
    decimal? Rate,
    double? RemainingPercent,
    decimal? Individual,
    decimal? Workspace,
    string? Email,
    string? SubscriptionPlan,
    double? AgentRemainingPercent,
    double? OrbRemainingPercent);

internal static class AmpParser
{
    private static readonly Regex Ansi = new(@"[[0-?]*[ -/]*[@-~]");
    private static readonly Regex Money = new(@"Amp Free:[ ]*[$](?<r>[0-9.]+)[ ]*/[ ]*[$](?<l>[0-9.]+)[ ]+remaining(?:[ ]+[(]replenishes[ ]+[+][$](?<h>[0-9.]+)[ ]*/hour[)])?", RegexOptions.IgnoreCase);
    private static readonly Regex Percent = new(@"Amp Free:[ ]*(?<p>[0-9.]+)%[ ]+remaining", RegexOptions.IgnoreCase);
    private static readonly Regex Individual = new(@"Individual credits?:[ ]*[$](?<v>[0-9.]+)", RegexOptions.IgnoreCase);
    private static readonly Regex Workspace = new(@"Workspace credits?:[ ]*[$](?<v>[0-9.]+)", RegexOptions.IgnoreCase);
    private static readonly Regex Identity = new(@"Signed in as\s+(?<v>\S+)", RegexOptions.IgnoreCase);
    private static readonly Regex Subscription = new(@"Subscription\s+(?<plan>[^:\r\n]+):(?<usage>[^\r\n]+)", RegexOptions.IgnoreCase);
    private static readonly Regex AgentUsage = new(@"(?<p>[0-9.]+)%\s+(?:other|agent)\s+usage", RegexOptions.IgnoreCase);
    private static readonly Regex OrbUsage = new(@"(?<p>[0-9.]+)%\s+orb\s+usage", RegexOptions.IgnoreCase);

    internal static bool TryParse(string? raw, out AmpUsage? usage)
    {
        string text = Ansi.Replace(raw ?? "", "");
        Match m = Money.Match(text), p = Percent.Match(text), i = Individual.Match(text), w = Workspace.Match(text), e = Identity.Match(text), s = Subscription.Match(text);
        Match a = s.Success ? AgentUsage.Match(s.Groups["usage"].Value) : Match.Empty;
        Match o = s.Success ? OrbUsage.Match(s.Groups["usage"].Value) : Match.Empty;
        usage = null;
        if (!m.Success && !p.Success && !i.Success && !w.Success && !a.Success && !o.Success) return false;
        usage = new(
            Dec(m, "r"),
            Dec(m, "l"),
            Dec(m, "h"),
            Num(p, "p"),
            Dec(i, "v"),
            Dec(w, "v"),
            e.Success ? e.Groups["v"].Value : null,
            s.Success ? s.Groups["plan"].Value.Trim() : null,
            Num(a, "p"),
            Num(o, "p"));
        return true;
    }

    internal static ProviderSnapshot Snapshot(AccountKey account, AmpUsage value, DateTimeOffset now, string strategy, string source)
    {
        List<UsageMeter> meters = [];
        if (value.Limit is > 0 && value.Remaining.HasValue)
        {
            decimal used = Math.Max(0, value.Limit.Value - value.Remaining.Value);
            double percent = Math.Clamp((double)(used / value.Limit.Value * 100), 0, 100);
            DateTimeOffset? reset = value.Rate is > 0 ? now.AddHours((double)(used / value.Rate.Value)) : null;
            meters.Add(Meter("amp:free", "Amp Free", used, value.Limit, percent, TimeSpan.FromDays(1), reset, MeterUnit.Usd, now, strategy));
        }
        else if (value.RemainingPercent.HasValue)
        {
            double percent = Math.Clamp(100 - value.RemainingPercent.Value, 0, 100);
            meters.Add(Meter("amp:free", "Amp Free", (decimal)percent, 100, percent, TimeSpan.FromDays(1), null, MeterUnit.Percent, now, strategy));
        }
        AddSubscriptionMeter(meters, "amp:agent", "Agent usage", value.AgentRemainingPercent, now, strategy);
        AddSubscriptionMeter(meters, "amp:orb", "Orb usage", value.OrbRemainingPercent, now, strategy);
        List<BalanceMetric> balances = [];
        if (value.Individual.HasValue) balances.Add(new("amp:individual", "Individual credits", value.Individual, MeterUnit.Usd));
        if (value.Workspace.HasValue) balances.Add(new("amp:workspace", "Workspace credits", value.Workspace, MeterUnit.Usd));
        List<(string Key, string Value)> extensionValues = [("source", source)];
        if (!string.IsNullOrWhiteSpace(value.Email)) extensionValues.Add(("email", value.Email));
        if (!string.IsNullOrWhiteSpace(value.SubscriptionPlan)) extensionValues.Add(("plan_type", value.SubscriptionPlan));
        var ext = ProviderHttpSupport.SafeExtensions(extensionValues.ToArray());
        return new(account, meters, balances, SnapshotCompleteness.Authoritative, now, DataConfidence.High, ext);
    }

    private static void AddSubscriptionMeter(List<UsageMeter> meters, string key, string name, double? remainingPercent, DateTimeOffset now, string strategy)
    {
        if (!remainingPercent.HasValue) return;
        double usedPercent = Math.Clamp(100 - remainingPercent.Value, 0, 100);
        meters.Add(Meter(key, name, (decimal)usedPercent, 100, usedPercent, TimeSpan.FromDays(30), null, MeterUnit.Percent, now, strategy));
    }

    private static UsageMeter Meter(string key, string name, decimal used, decimal? limit, double percent, TimeSpan window, DateTimeOffset? reset, MeterUnit unit, DateTimeOffset now, string strategy) =>
        new(new MeterKey(key), name, MeterScope.Account, unit, used, limit, percent, window, reset, null,
            percent >= 100 ? MeterStatus.Exhausted : percent >= 95 ? MeterStatus.Critical : percent >= 80 ? MeterStatus.Approaching : MeterStatus.Healthy,
            new MeterProvenance(strategy, "amp usage", now, true));
    private static decimal? Dec(Match m, string g) => m.Success && decimal.TryParse(m.Groups[g].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v) ? v : null;
    private static double? Num(Match m, string g) => m.Success && double.TryParse(m.Groups[g].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double v) ? v : null;
}

internal sealed class AmpCliStrategy(IProcessRunner runner, IClock clock) : ILimitFetchStrategy
{
    public string Id => "amp.cli-usage";
    public int Order => 10;
    public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(ProviderAccount account, CancellationToken cancellationToken) =>
        Task.FromResult(FindExecutable() is null ? new(StrategyAvailability.NotConfigured, "Amp CLI was not found.") : StrategyAvailabilityResult.Ready());
    public async Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string? executable = FindExecutable();
        if (executable is null) return FetchResult.Failure(FetchFailureKind.Unsupported, "Amp CLI was not found.", FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
        try
        {
            ProcessResult result = await runner.RunAsync(executable, ["usage"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            string output = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
            return result.ExitCode == 0 && AmpParser.TryParse(output, out AmpUsage? usage)
                ? FetchResult.Success(AmpParser.Snapshot(account.Key, usage, clock.UtcNow, Id, "cli"), Id, Stopwatch.GetElapsedTime(started))
                : FetchResult.Failure(FetchFailureKind.MalformedResponse, "Amp returned no recognizable usage data.", FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return FetchResult.Failure(FetchFailureKind.Unsupported, "Amp CLI could not be started.", FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
        }
    }
    internal static string? FindExecutable()
    {
        string? configured = Environment.GetEnvironmentVariable("AMP_CLI_PATH")?.Trim();
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        // .cmd/.bat shims are deliberately absent: ProcessRunner starts the
        // process with UseShellExecute = false, which cannot launch them.
        string[] names = OperatingSystem.IsWindows() ? ["amp.exe"] : ["amp"];
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (string name in names) { string path = Path.Combine(dir.Trim('"'), name); if (File.Exists(path)) return path; }
        return null;
    }
}

internal sealed class AmpApiStrategy(HttpClient http, IClock clock) : ILimitFetchStrategy
{
    private static readonly Uri Uri = new("https://ampcode.com/api/internal?userDisplayBalanceInfo");
    public string Id => "amp.display-balance-api";
    public int Order => 20;
    public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(ProviderAccount account, CancellationToken cancellationToken) =>
        Task.FromResult(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AMP_API_KEY")) ? new(StrategyAvailability.NotConfigured, "AMP_API_KEY is not configured.") : StrategyAvailabilityResult.Ready());
    public async Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string? token = Environment.GetEnvironmentVariable("AMP_API_KEY")?.Trim();
        if (string.IsNullOrEmpty(token)) return FetchResult.Failure(FetchFailureKind.Authentication, "AMP_API_KEY is not configured.", FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
        using HttpRequestMessage request = new(HttpMethod.Post, Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent("""{"method":"userDisplayBalanceInfo","params":{}}""", Encoding.UTF8, "application/json");
        using ProviderJsonResult exchange = await ProviderHttp.GetJsonAsync(http, request, Id, "Amp", started, cancellationToken).ConfigureAwait(false);
        if (!exchange.IsSuccess) return exchange.Failure!;
        JsonDocument json = exchange.Document!;
        JsonElement display = default;
        bool found = json.RootElement.TryGetProperty("result", out JsonElement result) && result.TryGetProperty("displayText", out display);
        return found && AmpParser.TryParse(display.GetString(), out AmpUsage? usage)
            ? FetchResult.Success(AmpParser.Snapshot(account.Key, usage, clock.UtcNow, Id, "api"), Id, Stopwatch.GetElapsedTime(started))
            : FetchResult.Failure(FetchFailureKind.MalformedResponse, "Amp returned no recognizable balance data.", FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
    }
}
