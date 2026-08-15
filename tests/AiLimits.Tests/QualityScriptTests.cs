// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;

namespace AiLimits.Tests;

public sealed class QualityScriptTests
{
    [Fact]
    public async Task CoverageMergesReportsAndTreatsEitherHitAsCovered()
    {
        using var temp = new TemporaryDirectory();
        WriteCoverage(Path.Combine(temp.Path, "unit", "a", "coverage.cobertura.xml"), "src/A.cs", null, (1, 0), (2, 1));
        WriteCoverage(Path.Combine(temp.Path, "unit", "b", "coverage.cobertura.xml"), "src/B.cs", (1, 0));
        WriteCoverage(Path.Combine(temp.Path, "integration", "coverage.cobertura.xml"), "SRC\\A.cs", (1, 1), (2, 0));

        ProcessResult result = await RunScriptAsync(
            "Assert-Coverage.ps1",
            $"-ResultsDirectory '{Path.Combine(temp.Path, "unit")}','{Path.Combine(temp.Path, "integration")}' -Minimum 66.67"
        );

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("66.67% (2/3 lines)", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoverageResolvesFilenamesAgainstReportSourceRoots()
    {
        using var temp = new TemporaryDirectory();
        WriteCoverage(Path.Combine(temp.Path, "unit", "coverage.cobertura.xml"), "src/A.cs", temp.Path, (1, 0));
        WriteCoverage(
            Path.Combine(temp.Path, "integration", "coverage.cobertura.xml"),
            "A.cs",
            Path.Combine(temp.Path, "src"),
            (1, 1)
        );

        ProcessResult result = await RunScriptAsync(
            "Assert-Coverage.ps1",
            $"-ResultsDirectory '{Path.Combine(temp.Path, "unit")}','{Path.Combine(temp.Path, "integration")}' -Minimum 100"
        );

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("100% (1/1 lines)", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoverageDoesNotPrefixSourceRootToAbsoluteFilenames()
    {
        using var temp = new TemporaryDirectory();
        string sourceFile = Path.Combine(temp.Path, "src", "A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllText(sourceFile, string.Empty);
        WriteCoverage(
            Path.Combine(temp.Path, "unit", "coverage.cobertura.xml"),
            sourceFile,
            Path.DirectorySeparatorChar.ToString(),
            (1, 0)
        );
        WriteCoverage(
            Path.Combine(temp.Path, "integration", "coverage.cobertura.xml"),
            "A.cs",
            Path.GetDirectoryName(sourceFile),
            (1, 1)
        );

        ProcessResult result = await RunScriptAsync(
            "Assert-Coverage.ps1",
            $"-ResultsDirectory '{Path.Combine(temp.Path, "unit")}','{Path.Combine(temp.Path, "integration")}' -Minimum 100"
        );

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("100% (1/1 lines)", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task CoverageEnforcesFloor(bool atFloor, int expectedExitCode)
    {
        using var temp = new TemporaryDirectory();
        WriteCoverage(Path.Combine(temp.Path, "coverage.cobertura.xml"), "A.cs", (1, 1), (2, atFloor ? 1 : 0));
        ProcessResult result = await RunScriptAsync(
            "Assert-Coverage.ps1",
            $"-ResultsDirectory '{temp.Path}' -Minimum 100"
        );
        Assert.True(result.ExitCode == expectedExitCode, result.Output);
    }

    [Fact]
    public async Task CoverageRejectsMissingAndMalformedReports()
    {
        using var missing = new TemporaryDirectory();
        Assert.NotEqual(
            0,
            (await RunScriptAsync("Assert-Coverage.ps1", $"-ResultsDirectory '{missing.Path}'")).ExitCode
        );
        File.WriteAllText(Path.Combine(missing.Path, "coverage.cobertura.xml"), "not xml");
        Assert.NotEqual(
            0,
            (await RunScriptAsync("Assert-Coverage.ps1", $"-ResultsDirectory '{missing.Path}'")).ExitCode
        );
    }

    [Theory]
    [InlineData("duration=\"00:00:30\"", 0)]
    [InlineData("duration=\"00:00:30.001\"", 1)]
    [InlineData("startTime=\"2026-01-01T00:00:00Z\" endTime=\"2026-01-01T00:00:30Z\"", 0)]
    public async Task DurationAssertionParsesDurationAndTimestampFallback(string timing, int expectedExitCode)
    {
        using var temp = new TemporaryDirectory();
        WriteTrx(Path.Combine(temp.Path, "unit.trx"), timing, includeResult: true);
        ProcessResult result = await RunScriptAsync(
            "Assert-TestDurations.ps1",
            $"-ResultsDirectory '{temp.Path}' -MaxSeconds 30"
        );
        Assert.True(result.ExitCode == expectedExitCode, result.Output);
    }

    [Fact]
    public async Task DurationAssertionRejectsMissingAndEmptyTrx()
    {
        using var temp = new TemporaryDirectory();
        Assert.NotEqual(
            0,
            (await RunScriptAsync("Assert-TestDurations.ps1", $"-ResultsDirectory '{temp.Path}'")).ExitCode
        );
        WriteTrx(Path.Combine(temp.Path, "unit.trx"), "", includeResult: false);
        Assert.NotEqual(
            0,
            (await RunScriptAsync("Assert-TestDurations.ps1", $"-ResultsDirectory '{temp.Path}'")).ExitCode
        );
    }

    private static void WriteCoverage(string path, string file, params (int Line, int Hits)[] lines) =>
        WriteCoverage(path, file, null, lines);

    private static void WriteCoverage(string path, string file, string? source, params (int Line, int Hits)[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string entries = string.Concat(lines.Select(line => $"<line number=\"{line.Line}\" hits=\"{line.Hits}\"/>"));
        string sources = source is null ? "" : $"<sources><source>{source}</source></sources>";
        File.WriteAllText(
            path,
            $"<coverage>{sources}<packages><package><classes><class filename=\"{file}\"><lines>{entries}</lines></class></classes></package></packages></coverage>"
        );
    }

    private static void WriteTrx(string path, string timing, bool includeResult)
    {
        string result = includeResult ? $"<UnitTestResult testName=\"test\" outcome=\"Passed\" {timing}/>" : "";
        File.WriteAllText(
            path,
            $"<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>{result}</Results></TestRun>"
        );
    }

    private static async Task<ProcessResult> RunScriptAsync(string name, string arguments)
    {
        string script = Path.Combine(FindRepositoryRoot(), "scripts", "quality", name);
        string shell = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
        string command = $"& '{script.Replace("'", "''", StringComparison.Ordinal)}' {arguments}";
        using var process = Process.Start(
            new ProcessStartInfo(
                shell,
                $"-NoProfile -Command \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            )
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        )!;
        string output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiLimits.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"quotaboard-quality-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
