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

    /// <summary>
    /// Optional per-key fault selector; when it returns an exception for an
    /// entry, only operations on that entry throw. Mimics a vault that fails
    /// halfway through a multi-entry write.
    /// </summary>
    public Func<(string Scope, string Key), Exception?>? FaultFor { get; set; }

    /// <summary>Optional per-key fault selector applied only to writes.</summary>
    public Func<(string Scope, string Key), Exception?>? SetFaultFor { get; set; }

    /// <summary>Optional hook invoked immediately before a value is read.</summary>
    public Action<(string Scope, string Key)>? BeforeGet { get; set; }

    public IReadOnlyCollection<(string Scope, string Key)> Keys => _entries.Keys.ToArray();

    public Task SetAsync(string scope, string key, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFaulted(scope, key);
        if (SetFaultFor?.Invoke((scope, key)) is { } setFault)
        {
            throw setFault;
        }
        _entries[(scope, key)] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string scope, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFaulted(scope, key);
        BeforeGet?.Invoke((scope, key));
        return Task.FromResult(_entries.TryGetValue((scope, key), out string? value) ? value : null);
    }

    public Task DeleteAsync(string scope, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFaulted(scope, key);
        _entries.TryRemove((scope, key), out _);
        return Task.CompletedTask;
    }

    private void ThrowIfFaulted(string scope, string key)
    {
        if (FaultFor?.Invoke((scope, key)) is { } keyedFault)
        {
            throw keyedFault;
        }
        if (Fault is { } fault)
        {
            throw fault;
        }
    }
}
