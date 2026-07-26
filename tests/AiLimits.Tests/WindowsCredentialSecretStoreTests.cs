// SPDX-License-Identifier: Apache-2.0
using AiLimits.Platform.Windows.Security;

namespace AiLimits.Tests;

public sealed class WindowsCredentialSecretStoreTests
{
    [Fact]
    public async Task OversizedSecretThrowsBeforeAllocatingBytes()
    {
        var store = new WindowsCredentialSecretStore();

        // 2561 characters in UTF-16 = 5122 bytes, exceeding the 5120-byte limit.
        var oversized = new string('x', 2561);

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.SetAsync("test-scope", "oversized-key", oversized, default));

        Assert.Contains("2560 UTF-16 characters", ex.Message, StringComparison.Ordinal);
    }
}
