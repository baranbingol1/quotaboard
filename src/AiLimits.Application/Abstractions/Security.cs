// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Application.Abstractions;

public interface ISecretStore
{
    Task SetAsync(string scope, string key, string secret, CancellationToken cancellationToken);

    Task<string?> GetAsync(string scope, string key, CancellationToken cancellationToken);

    Task DeleteAsync(string scope, string key, CancellationToken cancellationToken);
}
