// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using System.Diagnostics;

namespace AiLimits.Infrastructure.Providers.Droid;

internal sealed class DroidSessionLimitStrategy(HttpClient httpClient, IClock clock, FactoryCredentialReader credentialReader) : ILimitFetchStrategy
{
    public string Id => "droid.factory-cli-oauth";

    public int Order => 10;

    public async Task<StrategyAvailabilityResult> CheckAvailabilityAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        return await credentialReader.ReadAsync(cancellationToken).ConfigureAwait(false) is null
            ? new StrategyAvailabilityResult(StrategyAvailability.NotConfigured, "Sign in with the Droid CLI to connect Factory.")
            : StrategyAvailabilityResult.Ready();
    }

    public async Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        FactoryCredential credential = await credentialReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return FetchResult.Failure(FetchFailureKind.Authentication, "Factory CLI session was not found. Run droid and sign in in the browser.", FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
        }
        return await new DroidApiLimitStrategy(httpClient, clock, Id, Order).FetchWithTokenAsync(account, credential.AccessToken, cancellationToken).ConfigureAwait(false);
    }
}
