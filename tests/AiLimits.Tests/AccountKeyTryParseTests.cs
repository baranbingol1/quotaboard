// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Tests;

public sealed class AccountKeyTryParseTests
{
    [Fact]
    public void WhitespaceProviderSegmentReturnsFalse()
    {
        Assert.False(AccountKey.TryParse(" :foo", out var account));
        Assert.Null(account);
    }

    [Fact]
    public void WhitespaceValueSegmentReturnsFalse()
    {
        Assert.False(AccountKey.TryParse("provider: ", out var account));
        Assert.Null(account);
    }

    [Fact]
    public void ValidInputStillParses()
    {
        Assert.True(AccountKey.TryParse("copilot:alice", out var account));
        Assert.Equal("copilot", account.Value.Provider.Value);
        Assert.Equal("alice", account.Value.Value);
    }

    [Fact]
    public void EmptyOrNullReturnsFalse()
    {
        Assert.False(AccountKey.TryParse(null, out _));
        Assert.False(AccountKey.TryParse("", out _));
        Assert.False(AccountKey.TryParse("   ", out _));
    }
}
