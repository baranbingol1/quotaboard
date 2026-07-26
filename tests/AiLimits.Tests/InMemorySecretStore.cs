// SPDX-License-Identifier: Apache-2.0
using System.Collections.Concurrent;
using AiLimits.Application.Abstractions;

namespace AiLimits.Tests;

/// <summary>
/// Stands in for Credential Manager so secret-backed code can be tested
/// without writing to the developer's real credential vault.
/// </summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<(string Scope, string Key), string> _entries = new();

    /// <summary>Set to throw from every operation, mimicking an unavailable vault.</summary>
    public Exception? Fault { get; set; }

    public IReadOnlyCollection<(string Scope, string Key)> Keys => _entries.Keys.ToArray();

    public Task SetAsync(string scope, string key, string secret, CancellationToken cancellationToken)
    {
        if (Fault is { } fault) throw fault;
        _entries[(scope, key)] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string scope, string key, CancellationToken cancellationToken)
    {
        if (Fault is { } fault) throw fault;
        return Task.FromResult(_entries.TryGetValue((scope, key), out string? value) ? value : null);
    }

    public Task DeleteAsync(string scope, string key, CancellationToken cancellationToken)
    {
        if (Fault is { } fault) throw fault;
        _entries.TryRemove((scope, key), out _);
        return Task.CompletedTask;
    }
}
