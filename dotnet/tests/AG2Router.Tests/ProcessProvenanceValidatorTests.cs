using AG2Router.AG2.Discovery;
using Xunit;

namespace AG2Router.Tests;

public class ProcessProvenanceValidatorTests
{
    [Fact]
    public void Validate_EmptyList_ReturnsNoCandidates()
    {
        var result = ProcessProvenanceValidator.Validate(Array.Empty<DiscoveredProcessRaw>());
        Assert.Equal(ProvenanceStatus.NoCandidates, result.Status);
        Assert.Null(result.SelectedProcess);
    }

    [Fact]
    public void Validate_UnrelatedSameNameProcess_ReturnsUnrelatedCandidate()
    {
        var unrelated = new DiscoveredProcessRaw(
            ProcessId: 1001,
            Name: "language_server.exe",
            CommandLine: @"C:\CustomTools\language_server.exe --port 1234",
            ExecutablePath: @"C:\CustomTools\language_server.exe"
        );

        var result = ProcessProvenanceValidator.Validate(new[] { unrelated });
        Assert.Equal(ProvenanceStatus.UnrelatedCandidate, result.Status);
        Assert.Null(result.SelectedProcess);
    }

    [Fact]
    public void Validate_GenuineCandidate_PassesProvenanceAndExtractsToken()
    {
        var genuine = new DiscoveredProcessRaw(
            ProcessId: 12345,
            Name: "language_server.exe",
            CommandLine: @"C:\Users\test\AppData\Local\Programs\antigravity\resources\bin\language_server.exe --standalone --override_ide_name antigravity --subclient_type hub --csrf_token 11111111-2222-3333-4444-555555555555",
            ExecutablePath: @"C:\Users\test\AppData\Local\Programs\antigravity\resources\bin\language_server.exe"
        );

        var result = ProcessProvenanceValidator.Validate(new[] { genuine });
        Assert.Equal(ProvenanceStatus.Discovered, result.Status);
        Assert.NotNull(result.SelectedProcess);
        Assert.Equal(12345, result.SelectedProcess.ProcessId);
        Assert.Equal("11111111-2222-3333-4444-555555555555", result.CsrfToken);
    }

    [Fact]
    public void Validate_GenuineCandidateMissingCsrfToken_ReturnsMissingCsrfToken()
    {
        var candidate = new DiscoveredProcessRaw(
            ProcessId: 22440,
            Name: "language_server.exe",
            CommandLine: @"C:\Users\test\AppData\Local\Programs\antigravity\resources\bin\language_server.exe --standalone --override_ide_name antigravity",
            ExecutablePath: @"C:\Users\test\AppData\Local\Programs\antigravity\resources\bin\language_server.exe"
        );

        var result = ProcessProvenanceValidator.Validate(new[] { candidate });
        Assert.Equal(ProvenanceStatus.MissingCsrfToken, result.Status);
    }

    [Fact]
    public void Validate_MultipleGenuineCandidates_ReturnsAmbiguousCandidates()
    {
        var cand1 = new DiscoveredProcessRaw(
            ProcessId: 1111,
            Name: "language_server.exe",
            CommandLine: @"C:\antigravity\resources\bin\language_server.exe --standalone --csrf_token token_1",
            ExecutablePath: @"C:\antigravity\resources\bin\language_server.exe"
        );
        var cand2 = new DiscoveredProcessRaw(
            ProcessId: 2222,
            Name: "language_server.exe",
            CommandLine: @"D:\antigravity\resources\bin\language_server.exe --standalone --csrf_token token_2",
            ExecutablePath: @"D:\antigravity\resources\bin\language_server.exe"
        );

        var result = ProcessProvenanceValidator.Validate(new[] { cand1, cand2 });
        Assert.Equal(ProvenanceStatus.AmbiguousCandidates, result.Status);
        Assert.Null(result.SelectedProcess);
        Assert.Contains("Multiple Antigravity language server processes detected", result.Message);
    }
}
