// SPDX-License-Identifier: Apache-2.0
using AiLimits.App;
using AiLimits.Infrastructure.Providers;

namespace AiLimits.Tests;

public sealed class LiveDashboardDataSourceTests
{
    [Fact]
    public void Discovery_failure_without_an_account_stays_disconnected()
    {
        var connection = LiveDashboardDataSource.BuildMissingConnection(
            BuiltInProviderDescriptors.Cursor,
            discoveryFailed: true
        );

        Assert.False(connection.IsConnected);
    }
}
