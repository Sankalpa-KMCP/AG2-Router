using System.IO;
using AG2Router.AG2.Security;

namespace AG2Router.AG2.Discovery;

public enum ProvenanceStatus
{
    Discovered,
    NoCandidates,
    UnrelatedCandidate,
    AmbiguousCandidates,
    MissingCsrfToken
}

public record ProvenanceValidationResult(
    ProvenanceStatus Status,
    DiscoveredProcessRaw? SelectedProcess,
    string? CsrfToken,
    string Message
);

/// <summary>
/// Enforces evidence-based provenance validation on discovered language server processes.
/// Mitigates basename-only spoofing risks by verifying binary paths, Antigravity install hierarchies,
/// required command-line markers, and explicit ambiguity detection.
/// </summary>
public static class ProcessProvenanceValidator
{
    /// <summary>
    /// Evaluates candidate processes against Antigravity provenance rules.
    /// Distinguishes genuine Antigravity daemons from unrelated language_server binaries,
    /// and safely flags ambiguous multi-candidate environments as degraded.
    /// </summary>
    public static ProvenanceValidationResult Validate(IReadOnlyList<DiscoveredProcessRaw> processes)
    {
        if (processes == null || processes.Count == 0)
        {
            return new ProvenanceValidationResult(
                ProvenanceStatus.NoCandidates,
                null,
                null,
                "Antigravity 2 language server is not running");
        }

        // 1. Identify all processes that match the general language server name
        var languageServerCandidates = processes.Where(IsLanguageServerCandidate).ToList();
        if (languageServerCandidates.Count == 0)
        {
            return new ProvenanceValidationResult(
                ProvenanceStatus.NoCandidates,
                null,
                null,
                "Antigravity 2 language server is not running");
        }

        // 2. Filter candidates by strict provenance: executable location + command line markers
        var genuineCandidates = languageServerCandidates.Where(HasAntigravityProvenance).ToList();

        if (genuineCandidates.Count == 0)
        {
            return new ProvenanceValidationResult(
                ProvenanceStatus.UnrelatedCandidate,
                null,
                null,
                "Process named language_server detected, but failed Antigravity provenance verification");
        }

        // 3. Ambiguity check: do not arbitrarily attach if multiple valid daemons are active
        if (genuineCandidates.Count > 1)
        {
            // Check if exactly one is marked --standalone
            var standaloneOnly = genuineCandidates.Where(p => (p.CommandLine ?? "").Contains("--standalone", StringComparison.OrdinalIgnoreCase)).ToList();
            if (standaloneOnly.Count == 1)
            {
                genuineCandidates = standaloneOnly;
            }
            else
            {
                return new ProvenanceValidationResult(
                    ProvenanceStatus.AmbiguousCandidates,
                    null,
                    null,
                    $"Multiple Antigravity language server processes detected ({genuineCandidates.Count} candidates); target is ambiguous");
            }
        }

        var selected = genuineCandidates[0];

        // 4. Verify CSRF token presence
        var csrf = AG2Security.ExtractCsrfToken(selected.CommandLine);
        if (string.IsNullOrWhiteSpace(csrf))
        {
            return new ProvenanceValidationResult(
                ProvenanceStatus.MissingCsrfToken,
                selected,
                null,
                $"Antigravity 2 running (PID {selected.ProcessId}) but CSRF token could not be parsed from command line");
        }

        return new ProvenanceValidationResult(
            ProvenanceStatus.Discovered,
            selected,
            csrf,
            $"Verified Antigravity 2 process (PID {selected.ProcessId})");
    }

    /// <summary>
    /// Returns every genuine Antigravity language-server candidate for multi-instance usage
    /// collection, deduplicated by process id and ordered deterministically. Applies the same
    /// provenance rules as <see cref="Validate"/> without the single-process ambiguity policy:
    /// usage collection must enumerate all validated instances, not select one.
    /// </summary>
    public static IReadOnlyList<DiscoveredProcessRaw> ValidateAll(IReadOnlyList<DiscoveredProcessRaw> processes)
    {
        if (processes is null || processes.Count == 0)
            return Array.Empty<DiscoveredProcessRaw>();

        return processes
            .Where(IsLanguageServerCandidate)
            .Where(HasAntigravityProvenance)
            .GroupBy(static process => process.ProcessId)
            .Select(static group => group.First())
            .OrderBy(static process => process.ProcessId)
            .ToList();
    }

    public static bool IsLanguageServerCandidate(DiscoveredProcessRaw proc)
    {
        var name = proc.Name ?? string.Empty;
        var path = proc.ExecutablePath ?? string.Empty;
        return name.Contains("language_server", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("language_server", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasAntigravityProvenance(DiscoveredProcessRaw proc)
    {
        var path = (proc.ExecutablePath ?? string.Empty).Replace('/', '\\');
        var cmd = proc.CommandLine ?? string.Empty;

        // Path provenance check:
        // Must reside in resources\bin\language_server.exe or have an antigravity directory in its hierarchy
        bool hasPathProvenance =
            path.Contains(@"\antigravity\resources\bin\language_server", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\resources\bin\language_server", StringComparison.OrdinalIgnoreCase) ||
            (path.Contains("antigravity", StringComparison.OrdinalIgnoreCase) && path.Contains("language_server", StringComparison.OrdinalIgnoreCase));

        // Command-line markers check:
        bool hasCmdMarkers =
            cmd.Contains("--standalone", StringComparison.OrdinalIgnoreCase) ||
            (cmd.Contains("--override_ide_name antigravity", StringComparison.OrdinalIgnoreCase) &&
             cmd.Contains("--subclient_type hub", StringComparison.OrdinalIgnoreCase)) ||
            cmd.Contains("--app_data_dir antigravity", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains(@"resources\bin\language_server.exe", StringComparison.OrdinalIgnoreCase);

        // Genuine process requires both path provenance (or command line path) and valid command line markers
        return (hasPathProvenance || cmd.Contains(@"resources\bin\language_server", StringComparison.OrdinalIgnoreCase)) && hasCmdMarkers;
    }
}
