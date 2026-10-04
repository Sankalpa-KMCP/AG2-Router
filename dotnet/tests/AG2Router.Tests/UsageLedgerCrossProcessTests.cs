using System.Diagnostics;
using System.IO;
using AG2Router.AG2.Persistence;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Builds (once) a small console helper project that references the real usage-ledger
/// assemblies, so tests can execute genuine independent OS processes against one shared
/// temporary ledger directory. Each child has its own PathLockRegistry, dedup index, and
/// cross-process lease handles — the exact configuration a second AG2 Router process would
/// have. Synchronization uses named events and result files; no sleeps gate correctness.
/// </summary>
public sealed class UsageChildProcessFixture : IDisposable
{
    private readonly string _workspace;

    public string DotNetExePath { get; }
    public string HelperDllPath { get; }

    public UsageChildProcessFixture()
    {
        DotNetExePath = ResolveDotNetExe();
        string repoRoot = LocateRepoRoot();
        _workspace = Path.Combine(Path.GetTempPath(), $"ag2_u1a_helper_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_workspace, "helper"));

        string coreProject = Path.Combine(repoRoot, "dotnet", "src", "AG2Router.Core", "AG2Router.Core.csproj");
        string ag2Project = Path.Combine(repoRoot, "dotnet", "src", "AG2Router.AG2", "AG2Router.AG2.csproj");
        File.WriteAllText(
            Path.Combine(_workspace, "helper", "helper.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{CORE}" />
                <ProjectReference Include="{AG2}" />
              </ItemGroup>
            </Project>
            """.Replace("{CORE}", coreProject).Replace("{AG2}", ag2Project));

        File.WriteAllText(Path.Combine(_workspace, "helper", "Program.cs"), ChildProgramSource);

        var buildInfo = new ProcessStartInfo(DotNetExePath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        string outputDir = Path.Combine(_workspace, "out");
        buildInfo.ArgumentList.Add("build");
        buildInfo.ArgumentList.Add(Path.Combine(_workspace, "helper", "helper.csproj"));
        buildInfo.ArgumentList.Add("-c");
        buildInfo.ArgumentList.Add("Debug");
        buildInfo.ArgumentList.Add("-o");
        buildInfo.ArgumentList.Add(outputDir);
        buildInfo.ArgumentList.Add("--nologo");
        buildInfo.ArgumentList.Add("-v");
        buildInfo.ArgumentList.Add("quiet");
        using var build = Process.Start(buildInfo) ?? throw new InvalidOperationException("dotnet build could not start.");
        if (!build.WaitForExit(TimeSpan.FromSeconds(180)))
        {
            build.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Cross-process helper build timed out.");
        }
        if (build.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Cross-process helper build failed: " + build.StandardError.ReadToEnd());
        }

        HelperDllPath = Path.Combine(outputDir, "helper.dll");
    }

    private static string ChildProgramSource => """
        using AG2Router.AG2.Persistence;
        using AG2Router.Core.Models;
        using System.Globalization;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < args.Length; i += 2)
            map[args[i]] = args[i + 1];

        string ledgerDir = map["--ledger"];
        string resultFile = map["--result"];
        string readyName = map["--ready"];
        string goName = map["--go"];

        var basis = map["--basis"] == "unattributed"
            ? UsageAccountAttributionBasis.Unattributed
            : UsageAccountAttributionBasis.VerifiedObservation;
        var observed = DateTimeOffset.Parse(map["--observed"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var call = new UsageCallRecord(
            UsageCallKeys.Compute(map["--cascade"], map["--response"]),
            UsageCallKeys.CanonicalizeResponseModel("claude-sonnet-4-5"),
            "anthropic",
            long.Parse(map["--input"], CultureInfo.InvariantCulture),
            long.Parse(map["--output"], CultureInfo.InvariantCulture),
            null, null, null,
            observed,
            UsageTimeAttribution.ObservationTime,
            basis,
            basis == UsageAccountAttributionBasis.VerifiedObservation ? map["--account"] : null,
            "CrossProcessTest");

        using (var ready = EventWaitHandle.OpenExisting(readyName)) ready.Set();
        using (var go = EventWaitHandle.OpenExisting(goName)) go.WaitOne(TimeSpan.FromMinutes(3));

        var ledger = new DurableUsageCallLedger(ledgerDir);
        try
        {
            if (map.TryGetValue("--queries", out var queriesText))
            {
                int min = int.MaxValue;
                int max = 0;
                int iterations = int.Parse(queriesText, CultureInfo.InvariantCulture);
                for (int i = 0; i < iterations; i++)
                {
                    int count = ledger.GetAllCallsAsync().GetAwaiter().GetResult().Count;
                    if (count < min) min = count;
                    if (count > max) max = count;
                }
                File.WriteAllText(resultFile, $"QOK {min} {max} {iterations}");
                return;
            }

            int repeat = map.TryGetValue("--repeat", out var repeatText)
                ? int.Parse(repeatText, CultureInfo.InvariantCulture) : 1;
            int inserted = 0;
            int duplicates = 0;
            for (int i = 0; i < repeat; i++)
            {
                string cascadeId = i == 0 ? map["--cascade"] : $"{map["--cascade"]}-{i}";
                var iterationCall = new UsageCallRecord(
                    UsageCallKeys.Compute(cascadeId, map["--response"]),
                    call.ResponseModelKey,
                    call.Provider,
                    call.InputTokens,
                    call.OutputTokens,
                    null, null, null,
                    call.FirstObservedAtUtc,
                    call.TimeAttribution,
                    call.AccountAttributionBasis,
                    call.AccountId,
                    call.Source);
                var result = ledger.IngestBatchAsync([iterationCall]).GetAwaiter().GetResult();
                inserted += result.InsertedCount;
                duplicates += result.DuplicateCount;
            }
            File.WriteAllText(resultFile, $"OK {inserted} {duplicates}");
        }
        catch (UsageLedgerConflictException)
        {
            File.WriteAllText(resultFile, "CONFLICT");
        }
        catch (UsageLedgerCorruptionException)
        {
            File.WriteAllText(resultFile, "CORRUPT");
        }
        """;

    private static string LocateRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "dotnet", "src", "AG2Router.AG2", "AG2Router.AG2.csproj")))
                return directory;
            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException("Repository root could not be located for the cross-process helper.");
    }

    private static string ResolveDotNetExe()
    {
        string? processPath = Environment.ProcessPath;
        if (processPath is not null &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;

        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is not null)
        {
            foreach (string candidate in new[] { "dotnet.exe", "dotnet" })
            {
                string fullPath = Path.Combine(dotnetRoot, candidate);
                if (File.Exists(fullPath)) return fullPath;
            }
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidate in new[] { "dotnet.exe", "dotnet" })
            {
                string fullPath = Path.Combine(directory.Trim(), candidate);
                if (File.Exists(fullPath)) return fullPath;
            }
        }

        throw new InvalidOperationException("dotnet.exe could not be located for cross-process usage-ledger tests.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors on temp dir
        }
    }
}

/// <summary>
/// Cross-process correctness of the durable usage ledger: independent OS processes ingest into
/// one shared ledger directory, and the global call-key uniqueness, idempotence, and conflict
/// invariants must hold even when writers initially choose different observation months.
/// </summary>
[CollectionDefinition("UsageCrossProcess", DisableParallelization = false)]
public class UsageCrossProcessTestCollection { }

[Collection("UsageCrossProcess")]
public class UsageLedgerCrossProcessTests : IClassFixture<UsageChildProcessFixture>
{
    private static readonly DateTimeOffset October = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset November = new(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);

    private readonly UsageChildProcessFixture _fixture;
    private readonly string _tempRoot;

    public UsageLedgerCrossProcessTests(UsageChildProcessFixture fixture)
    {
        _fixture = fixture;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ag2_u1a_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    private sealed record ChildOutcome(string Status, int Inserted, int Duplicate, int QueryIterations = 0);

    private static string LedgerDirOf(string testDir) => Path.Combine(testDir, "usage");

    private static DurableUsageCallLedger CreateLedger(string testDir) =>
        new(LedgerDirOf(testDir));

    private async Task<ChildOutcome[]> RunChildrenAsync(
        string testDir,
        params (string CascadeId, long InputTokens, long OutputTokens, DateTimeOffset Observed, UsageAccountAttributionBasis Basis, int Repeat, int QueryIterations)[] specs)
    {
        string goName = $"ag2u1a-go-{Guid.NewGuid():N}";
        var children = new List<(Process Process, string ResultFile, EventWaitHandle Ready)>();
        var readyNames = new List<string>();
        try
        {
            using var go = new EventWaitHandle(false, EventResetMode.ManualReset, goName);
            for (int index = 0; index < specs.Length; index++)
            {
                var spec = specs[index];
                string readyName = $"ag2u1a-ready-{Guid.NewGuid():N}";
                string resultFile = Path.Combine(_tempRoot, $"result-{Guid.NewGuid():N}.txt");
                var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
                readyNames.Add(readyName);

                var psi = new ProcessStartInfo(_fixture.DotNetExePath)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("exec");
                psi.ArgumentList.Add(_fixture.HelperDllPath);
                psi.ArgumentList.Add("--ledger"); psi.ArgumentList.Add(LedgerDirOf(testDir));
                psi.ArgumentList.Add("--result"); psi.ArgumentList.Add(resultFile);
                psi.ArgumentList.Add("--ready"); psi.ArgumentList.Add(readyName);
                psi.ArgumentList.Add("--go"); psi.ArgumentList.Add(goName);
                psi.ArgumentList.Add("--cascade"); psi.ArgumentList.Add(spec.CascadeId);
                psi.ArgumentList.Add("--response"); psi.ArgumentList.Add("response-1");
                psi.ArgumentList.Add("--input"); psi.ArgumentList.Add(spec.InputTokens.ToString());
                psi.ArgumentList.Add("--output"); psi.ArgumentList.Add(spec.OutputTokens.ToString());
                psi.ArgumentList.Add("--observed"); psi.ArgumentList.Add(spec.Observed.ToString("O"));
                psi.ArgumentList.Add("--basis"); psi.ArgumentList.Add(
                    spec.Basis == UsageAccountAttributionBasis.Unattributed ? "unattributed" : "verified");
                psi.ArgumentList.Add("--account"); psi.ArgumentList.Add("acc_synthetic01");
                if (spec.QueryIterations > 0)
                {
                    psi.ArgumentList.Add("--mode"); psi.ArgumentList.Add("query");
                    psi.ArgumentList.Add("--queries"); psi.ArgumentList.Add(spec.QueryIterations.ToString());
                }
                else if (spec.Repeat > 1)
                {
                    psi.ArgumentList.Add("--repeat"); psi.ArgumentList.Add(spec.Repeat.ToString());
                }

                var process = Process.Start(psi) ?? throw new InvalidOperationException("Child process could not start.");
                children.Add((process, resultFile, ready));
            }

            foreach (var (_, _, ready) in children)
            {
                Assert.True(ready.WaitOne(TimeSpan.FromSeconds(90)), "Child process did not signal readiness in time.");
            }
            go.Set();

            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            foreach (var (process, _, _) in children)
            {
                try
                {
                    await process.WaitForExitAsync(exitCts.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    throw new InvalidOperationException("Child process did not exit in time; stderr: " + process.StandardError.ReadToEnd());
                }
            }

            var outcomes = new List<ChildOutcome>();
            foreach (var (_, resultFile, _) in children)
            {
                Assert.True(File.Exists(resultFile), "Child process produced no result file.");
                string text = await File.ReadAllTextAsync(resultFile);
                string[] parts = text.Split(' ');
                outcomes.Add(parts[0] switch
                {
                    "OK" => new ChildOutcome("OK", int.Parse(parts[1]), int.Parse(parts[2])),
                    "QOK" => new ChildOutcome("QOK", int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3])),
                    "CONFLICT" => new ChildOutcome("CONFLICT", 0, 0),
                    "CORRUPT" => new ChildOutcome("CORRUPT", 0, 0),
                    _ => throw new InvalidOperationException($"Unexpected child result: '{text}'."),
                });
            }
            return outcomes.ToArray();
        }
        finally
        {
            foreach (var (_, _, ready) in children) ready.Dispose();
            foreach (var (process, _, _) in children) process.Dispose();
        }
    }

    private static int CountKeyOccurrences(string testDir, string callKey)
    {
        string ledgerDir = LedgerDirOf(testDir);
        if (!Directory.Exists(ledgerDir)) return 0;
        return Directory.GetFiles(ledgerDir, "usage-calls-*.json")
            .Count(file => File.ReadAllText(file).Contains(callKey, StringComparison.Ordinal));
    }

    private static UsageCallRecord CallFor(string cascadeId, long inputTokens, long outputTokens, DateTimeOffset observed) =>
        new(
            UsageCallKeys.Compute(cascadeId, "response-1"),
            "claude-sonnet-4-5",
            "anthropic",
            inputTokens,
            outputTokens,
            null, null, null,
            observed,
            UsageTimeAttribution.ObservationTime,
            UsageAccountAttributionBasis.VerifiedObservation,
            "acc_synthetic01",
            "CrossProcessTest");

    #region CP1–CP5: concurrent independent processes

    [Fact]
    public async Task CP1_SameKeySamePayloadSameSegment_InsertsExactlyOnce()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        string callKey = UsageCallKeys.Compute("cp1", "response-1");

        var outcomes = await RunChildrenAsync(testDir,
            ("cp1", 100, 40, October, UsageAccountAttributionBasis.VerifiedObservation, 1, 0),
            ("cp1", 100, 40, October, UsageAccountAttributionBasis.VerifiedObservation, 1, 0));

        Assert.All(outcomes, outcome => Assert.Equal("OK", outcome.Status));
        Assert.Equal(1, outcomes.Sum(outcome => outcome.Inserted));
        Assert.Equal(1, outcomes.Sum(outcome => outcome.Duplicate));
        Assert.Equal(1, CountKeyOccurrences(testDir, callKey));
        var record = Assert.Single(await CreateLedger(testDir).GetAllCallsAsync());
        Assert.Equal(CallFor("cp1", 100, 40, October), record);
    }

    [Fact]
    public async Task CP2_SameKeyConflictingPayloadSameSegment_ExactlyOneRecordAndOneConflict()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        string callKey = UsageCallKeys.Compute("cp2", "response-1");
        var candidateA = CallFor("cp2", 100, 40, October);
        var candidateB = CallFor("cp2", 999, 40, October);

        var outcomes = await RunChildrenAsync(testDir,
            ("cp2", 100, 40, October, UsageAccountAttributionBasis.VerifiedObservation, 1, 0),
            ("cp2", 999, 40, October, UsageAccountAttributionBasis.VerifiedObservation, 1, 0));

        Assert.Equal(1, outcomes.Count(outcome => outcome.Status == "OK"));
        Assert.Equal(1, outcomes.Count(outcome => outcome.Status == "CONFLICT"));
        var record = Assert.Single(await CreateLedger(testDir).GetAllCallsAsync());
        Assert.Equal(1, CountKeyOccurrences(testDir, callKey));
        // The survivor is exactly one complete candidate, never a mixture.
        Assert.True(
            record == candidateA || record == candidateB,
            "The persisted record must be one complete candidate payload.");
    }

    [Fact]
    public async Task CP3_SameKeySamePayloadDifferentMonths_NeverDuplicatesAcrossSegments()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        string callKey = UsageCallKeys.Compute("cp3", "response-1");

        var outcomes = await RunChildrenAsync(testDir,
            ("cp3", 100, 40, October, UsageAccountAttributionBasis.VerifiedObservation, 1, 0),
            ("cp3", 100, 40, November, UsageAccountAttributionBasis.VerifiedObservation, 1, 0));

        Assert.All(outcomes, outcome => Assert.Equal("OK", outcome.Status));
        Assert.Equal(1, outcomes.Sum(outcome => outcome.Inserted));
        Assert.Equal(1, outcomes.Sum(outcome => outcome.Duplicate));
        Assert.Equal(1, CountKeyOccurrences(testDir, callKey));
        var record = Assert.Single(await CreateLedger(testDir).GetAllCallsAsync());
        var octoberCandidate = CallFor("cp3", 100, 40, October);
        var novemberCandidate = CallFor("cp3", 100, 40, November);
        Assert.True(
            record == octoberCandidate || record == novemberCandidate,
            "The surviving record must be one complete candidate (either month may win the race).");
    }

    [Fact]
    public async Task CP4_SameKeyConflictingPayloadDifferentMonths_OneRecordOneConflict()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        string callKey = UsageCallKeys.Compute("cp4", "response-1");
        var candidateA = CallFor("cp4", 100, 40, October);

        var outcomes = await RunChildrenAsync(testDir,
            ("cp4", 100, 40, October, UsageAccountAttributionBasis.VerifiedObservation, 1, 0),
            ("cp4", 999, 40, November, UsageAccountAttributionBasis.VerifiedObservation, 1, 0));

        Assert.Equal(1, outcomes.Count(outcome => outcome.Status == "OK"));
        Assert.Equal(1, outcomes.Count(outcome => outcome.Status == "CONFLICT"));
        Assert.Equal(1, CountKeyOccurrences(testDir, callKey));
        var record = Assert.Single(await CreateLedger(testDir).GetAllCallsAsync());
        var candidateB = CallFor("cp4", 999, 40, November);
        Assert.True(
            record == candidateA || record == candidateB,
            "The persisted record must be one complete candidate payload.");
    }

    [Fact]
    public async Task CP5_DistinctKeysDifferentMonths_BothSucceed()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));

        var outcomes = await RunChildrenAsync(testDir,
            ("cp5-a", 100, 40, October, UsageAccountAttributionBasis.VerifiedObservation, 1, 0),
            ("cp5-b", 200, 50, November, UsageAccountAttributionBasis.VerifiedObservation, 1, 0));

        Assert.All(outcomes, outcome =>
        {
            Assert.Equal("OK", outcome.Status);
            Assert.Equal(1, outcome.Inserted);
        });
        var records = await CreateLedger(testDir).GetAllCallsAsync();
        Assert.True(records.Count == 2, $"Expected 2 records, found {records.Count}.");
    }

    #endregion

    #region CP6: restart with empty in-memory index

    [Fact]
    public async Task CP6_FreshProcessDuplicateAfterRestart_IsIdempotentNoOp()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        var original = CallFor("cp6", 100, 40, October);
        await CreateLedger(testDir).IngestBatchAsync([original]);

        // The child process starts with no in-memory index; durable state must be authoritative.
        var outcomes = await RunChildrenAsync(testDir,
            ("cp6", 100, 40, October, UsageAccountAttributionBasis.VerifiedObservation, 1, 0));

        var outcome = Assert.Single(outcomes);
        Assert.Equal("OK", outcome.Status);
        Assert.Equal(0, outcome.Inserted);
        Assert.Equal(1, outcome.Duplicate);
        var record = Assert.Single(await CreateLedger(testDir).GetAllCallsAsync());
        Assert.Equal(original, record);
    }

    [Fact]
    public async Task CP6b_FreshProcessConflictingAfterRestart_FailsClosed()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        var original = CallFor("cp6b", 100, 40, October);
        await CreateLedger(testDir).IngestBatchAsync([original]);

        var outcomes = await RunChildrenAsync(testDir,
            ("cp6b", 999, 40, November, UsageAccountAttributionBasis.VerifiedObservation, 1, 0));

        var outcome = Assert.Single(outcomes);
        Assert.Equal("CONFLICT", outcome.Status);
        var record = Assert.Single(await CreateLedger(testDir).GetAllCallsAsync());
        Assert.Equal(original, record);
    }

    #endregion

    #region CP8: corruption while another writer enters

    [Fact]
    public async Task CP8_CorruptSegment_FailsClosedForWritersOnAnySegment_WithoutReplacement()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        var ledger = CreateLedger(testDir);
        var committed = CallFor("cp8", 100, 40, October);
        await ledger.IngestBatchAsync([committed]);
        string corruptPath = Path.Combine(LedgerDirOf(testDir), "usage-calls-2026-10.json");
        const string corruptBytes = "{ not a ledger";
        await File.WriteAllTextAsync(corruptPath, corruptBytes);

        // A writer targeting the corrupt segment fails closed.
        var onCorrupt = await RunChildrenAsync(testDir,
            ("cp8-fresh", 1, 2, October, UsageAccountAttributionBasis.VerifiedObservation, 1, 0));
        Assert.Equal("CORRUPT", Assert.Single(onCorrupt).Status);

        // A writer targeting a different segment also fails closed: the refresh of global
        // durable state refuses to proceed, so no replacement segment is created anywhere.
        var onHealthy = await RunChildrenAsync(testDir,
            ("cp8-fresh", 1, 2, November, UsageAccountAttributionBasis.Unattributed, 1, 0));
        Assert.Equal("CORRUPT", Assert.Single(onHealthy).Status);
        Assert.False(File.Exists(Path.Combine(LedgerDirOf(testDir), "usage-calls-2026-11.json")));
        Assert.Equal(corruptBytes, await File.ReadAllTextAsync(corruptPath));
    }

    #endregion

    #region Crash-prefix recovery (multi-segment batch)

    [Fact]
    public async Task MultiSegmentBatch_CrashBetweenSegmentWrites_RecoversByIdempotentReingest()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        string ledgerDir = LedgerDirOf(testDir);
        var october = CallFor("crash-oct", 100, 40, October);
        var november = CallFor("crash-nov", 200, 50, November);
        var crashingWriter = new CrashAfterFirstWriteFileWriter();
        var crashingLedger = new DurableUsageCallLedger(ledgerDir, fileWriter: crashingWriter);

        // Process-death simulation: October commits, November never does.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => crashingLedger.IngestBatchAsync([october, november]));

        var reopened = CreateLedger(testDir);
        var committed = await reopened.GetAllCallsAsync();
        var committedRecord = Assert.Single(committed);
        Assert.Equal(october, committedRecord);

        // Re-ingesting the original batch completes the missing records without duplicating
        // the committed prefix and without manufacturing a conflict.
        var recovery = await reopened.IngestBatchAsync([october, november]);
        Assert.Equal(1, recovery.InsertedCount);
        Assert.Equal(1, recovery.DuplicateCount);

        var restored = await reopened.GetAllCallsAsync();
        Assert.Equal(
            new[] { october, november }.OrderBy(static call => call.CallKey, StringComparer.Ordinal),
            restored.OrderBy(static call => call.CallKey, StringComparer.Ordinal));
    }

    private sealed class CrashAfterFirstWriteFileWriter : IDurableFileWriter
    {
        private int _writes;

        public async Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _writes) == 2)
                throw new OperationCanceledException("synthetic crash between segment writes");
            await new DurableFileWriter().WriteAtomicAsync(destinationPath, content, cancellationToken);
        }
    }

    #endregion

    #region CP7 + stale in-memory index (in-process seams)

    [Fact]
    public async Task CP7_GlobalLeaseDisposalFailure_StillReleasesIngestOwnership()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        var ledger = CreateLedger(testDir);
        ledger.AcquireIngestLeaseAsync = (_, _) => Task.FromResult<IAsyncDisposable>(new ThrowingLease());

        await ledger.IngestBatchAsync([CallFor("cp7-a", 100, 40, October)]);

        // The synthetic disposal failure is captured and dropped; ledger ownership must still
        // be released, proven by a follow-up ingest completing within a bounded budget.
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var followUp = await ledger.IngestBatchAsync([CallFor("cp7-b", 1, 2, October)], bounded.Token);
        Assert.Equal(1, followUp.InsertedCount);
        Assert.True((await ledger.GetAllCallsAsync()).Count == 2, "Both ingested calls must be readable.");
    }

    private sealed class ThrowingLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => throw new IOException("synthetic lease disposal failure");
    }

    [Fact]
    public async Task StaleInMemoryIndex_CannotViolateGlobalCorrectness()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        // Both instances exist before the key is durable; instance2's index is stale by the
        // time it ingests. The freshness-validated refresh must discover the committed key.
        var instance1 = CreateLedger(testDir);
        var instance2 = CreateLedger(testDir);

        var committed = CallFor("stale-1", 100, 40, October);
        await instance1.IngestBatchAsync([committed]);

        var duplicate = await instance2.IngestBatchAsync([CallFor("stale-1", 100, 40, October)]);
        Assert.Equal(0, duplicate.InsertedCount);
        Assert.Equal(1, duplicate.DuplicateCount);

        await Assert.ThrowsAsync<UsageLedgerConflictException>(
            () => instance2.IngestBatchAsync([CallFor("stale-1", 999, 40, November)]));

        var record = Assert.Single(await instance2.GetAllCallsAsync());
        Assert.Equal(committed, record);
    }

    #endregion

    #region Cross-process reader/writer overlap

    [Fact]
    public async Task CPQ_CrossProcessReader_DuringConcurrentWriter_AlwaysSeesCompleteSegments()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        var finalLedger = CreateLedger(testDir);

        // One child commits 15 sequential batches while another child continuously queries the
        // same ledger directory through real cross-process file replacement windows.
        var outcomes = await RunChildrenAsync(testDir,
            ("cpq", 100, 40, October, UsageAccountAttributionBasis.VerifiedObservation, 15, 0),
            ("cpq-reader", 0, 0, October, UsageAccountAttributionBasis.VerifiedObservation, 0, 150));

        Assert.Equal(2, outcomes.Length);
        var writer = outcomes[0];
        Assert.Equal("OK", writer.Status);
        Assert.Equal(15, writer.Inserted);

        var reader = outcomes[1];
        Assert.Equal("QOK", reader.Status);
        Assert.Equal(150, reader.QueryIterations);
        Assert.True(reader.Inserted <= writer.Inserted, $"Reader observed {reader.Inserted} but writer committed {writer.Inserted}.");
        Assert.True(reader.Duplicate >= reader.Inserted, "Reader minimum must not exceed its maximum.");

        Assert.Equal(15, (await finalLedger.GetAllCallsAsync()).Count);
    }

    #endregion

    #region Query/write concurrency

    [Fact]
    public async Task Queries_DuringConcurrentCommits_AlwaysSeeCompleteSegments()
    {
        string testDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        var ledger = CreateLedger(testDir);

        var writerTask = Task.Run(async () =>
        {
            for (int index = 0; index < 25; index++)
            {
                await ledger.IngestBatchAsync([CallFor($"qw-{index:00}", 100 + index, 1, October)]);
            }
        });

        var observedCounts = new List<int>();
        while (!writerTask.IsCompleted)
        {
            observedCounts.Add((await ledger.GetAllCallsAsync()).Count);
        }
        await writerTask;
        observedCounts.Add((await ledger.GetAllCallsAsync()).Count);

        // Every read parsed successfully (any partial JSON would throw), and a single reader
        // can only observe a monotonically non-decreasing committed prefix.
        Assert.Equal(25, observedCounts[^1]);
        Assert.Equal(observedCounts, observedCounts.OrderBy(count => count).ToList());
    }

    #endregion
}
