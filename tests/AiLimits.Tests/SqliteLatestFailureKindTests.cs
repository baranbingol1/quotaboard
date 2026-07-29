// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;
using AiLimits.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AiLimits.Tests;

public sealed class SqliteLatestFailureKindTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Latest_attempt_is_reported_per_account_even_outside_a_global_window()
    {
        // Regression: the old query took the globally newest 100 rows, so any
        // account whose attempts fell out of that window defaulted to healthy.
        using var directory = new TemporaryDb();
        var repository = await CreateRepositoryAsync(directory.Path);
        var busy = Key("busy");
        var quiet = Key("quiet");

        await RecordAsync(repository, quiet, Base, FetchFailureKind.Network);
        for (var i = 1; i <= 120; i++)
        {
            await RecordAsync(repository, busy, Base.AddSeconds(i), FetchFailureKind.None);
        }

        IReadOnlyDictionary<AccountKey, FetchFailureKind> kinds = await repository.ReadLatestFailureKindsAsync(default);

        Assert.Equal(FetchFailureKind.Network, kinds[quiet]);
        Assert.Equal(FetchFailureKind.None, kinds[busy]);
        Assert.Equal(2, kinds.Count);
    }

    [Fact]
    public async Task The_newest_timestamp_wins_per_account()
    {
        using var directory = new TemporaryDb();
        var repository = await CreateRepositoryAsync(directory.Path);
        var account = Key("one");

        await RecordAsync(repository, account, Base, FetchFailureKind.Network);
        await RecordAsync(repository, account, Base.AddMinutes(1), FetchFailureKind.Authentication);

        IReadOnlyDictionary<AccountKey, FetchFailureKind> kinds = await repository.ReadLatestFailureKindsAsync(default);

        Assert.Equal(FetchFailureKind.Authentication, kinds[account]);
    }

    [Fact]
    public async Task On_identical_timestamps_the_attempt_recorded_last_wins()
    {
        // A refresh can record several attempts within one clock tick (fixed
        // clocks in tests, coarse timers in the field); "latest" must mean the
        // one recorded last, so a success after a failure reads as success.
        using var directory = new TemporaryDb();
        var repository = await CreateRepositoryAsync(directory.Path);
        var account = Key("one");

        await RecordAsync(repository, account, Base, FetchFailureKind.Network);
        await RecordAsync(repository, account, Base, FetchFailureKind.None);

        IReadOnlyDictionary<AccountKey, FetchFailureKind> kinds = await repository.ReadLatestFailureKindsAsync(default);

        Assert.Equal(FetchFailureKind.None, kinds[account]);
    }

    private static AccountKey Key(string account) => new(new ProviderId("fake"), account);

    private static async Task<SqliteSnapshotRepository> CreateRepositoryAsync(string directory)
    {
        var database = new SqliteDatabase(Path.Combine(directory, "state.db"));
        await database.InitializeAsync();
        return new SqliteSnapshotRepository(database);
    }

    private static Task RecordAsync(SqliteSnapshotRepository repository, AccountKey account,
        DateTimeOffset startedAt, FetchFailureKind kind) =>
        repository.RecordAttemptAsync(
            new FetchAttempt(Guid.NewGuid().ToString("N"), account, "strategy", startedAt,
                TimeSpan.FromMilliseconds(1), kind, "message"), default);

    private sealed class TemporaryDb : IDisposable
    {
        public TemporaryDb()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
