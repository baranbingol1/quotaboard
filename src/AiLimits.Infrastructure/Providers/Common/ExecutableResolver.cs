// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Infrastructure.Providers.Common;

/// <summary>
/// Turns the name of a provider CLI into a fully qualified executable path,
/// or refuses to run it.
///
/// Handing a bare name straight to <c>CreateProcess</c> lets the OS search the
/// application directory and the current working directory before PATH, so a
/// binary dropped next to QuotaBoard.exe — or into whatever folder happened to
/// be current — would win over the real CLI. Resolving here means only PATH is
/// consulted, and only for entries that are themselves absolute.
///
/// Only <c>.exe</c> is accepted. <c>.cmd</c> and <c>.bat</c> shims cannot be
/// launched at all with <c>UseShellExecute = false</c> (the interpreter would
/// have to be cmd.exe), so accepting them only ever produced a Win32Exception
/// at start time.
/// </summary>
public static class ExecutableResolver
{
    private const string Extension = ".exe";

    /// <summary>
    /// Resolves <paramref name="executable"/> to an absolute path.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is empty, is a relative path, or does not name an .exe.
    /// </exception>
    /// <exception cref="FileNotFoundException">Nothing on PATH matched.</exception>
    public static string Resolve(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        string candidate = executable.Trim().Trim('"');

        if (Path.IsPathFullyQualified(candidate))
        {
            RequireExecutableExtension(candidate);
            if (!File.Exists(candidate))
            {
                throw new FileNotFoundException("The provider executable does not exist.", candidate);
            }
            return Path.GetFullPath(candidate);
        }

        // "..\tool.exe", "sub/tool.exe" and the drive-relative "C:tool.exe"
        // all resolve against a working directory we do not control.
        if (
            candidate.Contains(Path.DirectorySeparatorChar)
            || candidate.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(candidate)
            || candidate.Contains(':')
        )
        {
            throw new ArgumentException(
                "A provider executable must be a bare name or a fully qualified path.",
                nameof(executable)
            );
        }

        string fileName = candidate.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : candidate + Extension;

        foreach (string directory in SearchPath())
        {
            string path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        throw new FileNotFoundException("The provider executable was not found on PATH.", fileName);
    }

    /// <summary>Absolute PATH entries only, in order.</summary>
    private static IEnumerable<string> SearchPath()
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (
            string entry in path.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            string directory = entry.Trim('"');
            // A relative PATH entry (including the empty-segment "." that some
            // machines still carry) resolves against the working directory.
            if (directory.Length > 0 && Path.IsPathFullyQualified(directory))
            {
                yield return directory;
            }
        }
    }

    private static void RequireExecutableExtension(string candidate)
    {
        if (!Path.GetExtension(candidate).Equals(Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A provider executable must be an .exe; .cmd and .bat shims cannot be launched directly.",
                nameof(candidate)
            );
        }
    }
}
