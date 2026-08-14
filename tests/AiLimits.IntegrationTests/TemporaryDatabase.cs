// SPDX-License-Identifier: Apache-2.0
using Microsoft.Data.Sqlite;

namespace AiLimits.IntegrationTests;

internal sealed class TemporaryDatabase : IDisposable
{
    public TemporaryDatabase()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "AiLimits.IntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        PathToFile = Path.Combine(DirectoryPath, "state.db");
    }

    public string DirectoryPath { get; }

    public string PathToFile { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
