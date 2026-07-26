// SPDX-License-Identifier: Apache-2.0
using AiLimits.Infrastructure.Providers.Common;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiLimits.Infrastructure.Providers.OpenCode;

public sealed class OpenCodePathDiscovery(IProcessRunner processRunner)
{
    public async Task<string> FindDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await processRunner.RunAsync("opencode", new string[] { "db", "path" }, TimeSpan.FromSeconds(4L), cancellationToken).ConfigureAwait(false);
            string reportedPath = result.StandardOutput.Trim().Trim('"');
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(reportedPath) && File.Exists(reportedPath))
            {
                return Path.GetFullPath(reportedPath);
            }
        }
        catch (Exception ex) when (((ex is IOException || ex is InvalidOperationException || ex is Win32Exception || ex is OperationCanceledException) ? 1 : 0) != 0)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        InlineArray5<string> buffer = default(InlineArray5<string>);
        buffer[0] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        buffer[1] = ".local";
        buffer[2] = "share";
        buffer[3] = "opencode";
        buffer[4] = "opencode.db";
        return Path.Combine(buffer);
    }
}
