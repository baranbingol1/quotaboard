// SPDX-License-Identifier: Apache-2.0
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Infrastructure.Providers.Antigravity;

/// <summary>
/// Finds the <c>agy</c> executable that ships with the Antigravity CLI.
///
/// The installer drops the binary under <c>%LOCALAPPDATA%</c> and only adds it
/// to PATH when the user runs <c>agy install</c>, so PATH alone misses a
/// perfectly usable install. The well-known locations are consulted after PATH
/// and never instead of it: a user who deliberately put a different build on
/// PATH should keep getting that build.
/// </summary>
internal sealed class AgyCliLocator
{
    /// <summary>Escape hatch for installs in none of the known locations.</summary>
    public const string PathOverrideVariable = "ANTIGRAVITY_CLI_PATH";

    private readonly Func<string, string?> _readEnvironment;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _resolveOnPath;

    public AgyCliLocator()
        : this(Environment.GetEnvironmentVariable, File.Exists, ResolveOnPath) { }

    internal AgyCliLocator(
        Func<string, string?> readEnvironment,
        Func<string, bool> fileExists,
        Func<string, string?> resolveOnPath
    )
    {
        _readEnvironment = readEnvironment;
        _fileExists = fileExists;
        _resolveOnPath = resolveOnPath;
    }

    /// <summary>The absolute path to <c>agy.exe</c>, or null when none is installed.</summary>
    public string? Resolve()
    {
        string? overridePath = _readEnvironment(PathOverrideVariable)?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(overridePath))
        {
            // An explicit override that is wrong is a configuration mistake worth
            // surfacing as "not installed" rather than silently searching on.
            return _fileExists(overridePath) ? overridePath : null;
        }

        string? onPath = _resolveOnPath("agy");
        if (onPath is not null)
        {
            return onPath;
        }

        foreach (string candidate in WellKnownPaths())
        {
            if (_fileExists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private IEnumerable<string> WellKnownPaths()
    {
        string? localAppData = _readEnvironment("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "agy", "bin", "agy.exe");
            yield return Path.Combine(localAppData, "Programs", "agy", "bin", "agy.exe");
        }

        string? userProfile = _readEnvironment("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".local", "bin", "agy.exe");
        }
    }

    private static string? ResolveOnPath(string executable)
    {
        try
        {
            return ExecutableResolver.Resolve(executable);
        }
        catch (Exception error) when (error is FileNotFoundException or ArgumentException)
        {
            return null;
        }
    }
}
