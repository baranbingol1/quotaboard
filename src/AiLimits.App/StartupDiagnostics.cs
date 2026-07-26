// SPDX-License-Identifier: Apache-2.0
using System.Runtime.ExceptionServices;
using AiLimits.Application.Diagnostics;

namespace AiLimits.App;

internal static class StartupDiagnostics
{
    private const string OptInVariable = "AILIMITS_STARTUP_DIAGNOSTICS";

    /// <summary>
    /// Generous enough to keep a full stack trace, which is the whole point of
    /// this log, while still bounding what one exception can write.
    /// </summary>
    private const int LogEntryLimit = 8000;

    [ThreadStatic]
    private static bool _inFirstChanceHandler;

    public static bool Register()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal))
            return false;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        Write("Startup diagnostics enabled.");
        return true;
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (_inFirstChanceHandler)
            return;
        _inFirstChanceHandler = true;
        try
        {
            Write($"FIRST_CHANCE {args.Exception.GetType().FullName}: {args.Exception.Message}");
        }
        finally
        {
            _inFirstChanceHandler = false;
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args) =>
        Write($"UNHANDLED terminating={args.IsTerminating}: {args.ExceptionObject}");

    /// <summary>
    /// Exception messages and <c>ToString()</c> dumps routinely quote the
    /// request that failed, so everything on its way to this file goes through
    /// the same redactor that guards persisted diagnostics.
    /// </summary>
    private static void Write(string message)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "AiLimits");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup-diagnostics.log"),
                $"{DateTimeOffset.UtcNow:O} {DiagnosticRedactor.Redact(message, LogEntryLimit)}{Environment.NewLine}");
        }
        catch { }
    }
}
