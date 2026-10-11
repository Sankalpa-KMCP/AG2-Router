using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AG2Router.Core.Diagnostics;

public enum OwnedSegmentStatus
{
    Active = 0,
    Closed = 1,
    Purged = 2
}

public sealed record OwnedSegmentRecord
{
    public required string SegmentId { get; init; }
    public required string FileName { get; init; }
    public required string RunId { get; init; }
    public required string CreatedAtUtc { get; init; }
    public required uint VolumeSerialNumber { get; init; }
    public required uint FileIndexHigh { get; init; }
    public required uint FileIndexLow { get; init; }
    public required long SizeBytes { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<OwnedSegmentStatus>))]
    public required OwnedSegmentStatus Status { get; init; }
}

public sealed record OwnershipRegistryManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentNamespace = "owned-segments-v1";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Namespace { get; init; } = CurrentNamespace;
    public string UpdatedAtUtc { get; init; } = DateTime.UtcNow.ToString("o");
    public Dictionary<string, OwnedSegmentRecord> Segments { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Crash-consistent durable ownership registry for logger-created segments (Option B).
/// Tracks authoritative OS file identities (VolumeSerialNumber, FileIndexHigh, FileIndexLow),
/// creating RunId, and segment lifecycle state. Supports atomic persistence and fail-closed
/// recovery upon corruption or incomplete updates.
/// </summary>
public sealed class DiagnosticOwnershipRegistry
{
    public const string RegistryFileName = "ownership-registry.json";
    private readonly string _registryFilePath;
    private readonly object _syncLock = new();
    private OwnershipRegistryManifest _manifest;
    private bool _isCorrupted;

    public bool IsCorrupted => _isCorrupted;
    public int Count
    {
        get
        {
            lock (_syncLock) return _manifest.Segments.Count;
        }
    }

    public DiagnosticOwnershipRegistry(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _registryFilePath = Path.Combine(directoryPath, RegistryFileName);
        _manifest = LoadManifestOrCreate();
    }

    private OwnershipRegistryManifest LoadManifestOrCreate()
    {
        if (!File.Exists(_registryFilePath))
        {
            return new OwnershipRegistryManifest();
        }

        try
        {
            string json = File.ReadAllText(_registryFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                _isCorrupted = true;
                return new OwnershipRegistryManifest();
            }

            var manifest = JsonSerializer.Deserialize<OwnershipRegistryManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null ||
                manifest.SchemaVersion != OwnershipRegistryManifest.CurrentSchemaVersion ||
                !string.Equals(manifest.Namespace, OwnershipRegistryManifest.CurrentNamespace, StringComparison.OrdinalIgnoreCase))
            {
                // Unrecognized schema or foreign namespace: fail closed (Section 4)
                _isCorrupted = true;
                return new OwnershipRegistryManifest();
            }

            return manifest;
        }
        catch
        {
            // Truncated, malformed, or unreadable registry: fail closed safely (Section 4)
            _isCorrupted = true;
            return new OwnershipRegistryManifest();
        }
    }

    public void RegisterSegment(OwnedSegmentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        OwnershipRegistryManifest snapshot;
        lock (_syncLock)
        {
            if (_isCorrupted) return;
            _manifest.Segments[record.SegmentId] = record;
            _manifest = _manifest with { UpdatedAtUtc = DateTime.UtcNow.ToString("o") };
            snapshot = _manifest;
        }
        PersistAtomic(snapshot);
    }

    public void UpdateSegmentStatus(string segmentId, OwnedSegmentStatus status, long sizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        OwnershipRegistryManifest snapshot;
        lock (_syncLock)
        {
            if (_isCorrupted) return;
            if (_manifest.Segments.TryGetValue(segmentId, out var existing))
            {
                _manifest.Segments[segmentId] = existing with
                {
                    Status = status,
                    SizeBytes = sizeBytes
                };
                _manifest = _manifest with { UpdatedAtUtc = DateTime.UtcNow.ToString("o") };
                snapshot = _manifest;
            }
            else
            {
                return;
            }
        }
        PersistAtomic(snapshot);
    }

    public void RemoveSegment(string segmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        OwnershipRegistryManifest snapshot;
        lock (_syncLock)
        {
            if (_isCorrupted) return;
            if (_manifest.Segments.Remove(segmentId))
            {
                _manifest = _manifest with { UpdatedAtUtc = DateTime.UtcNow.ToString("o") };
                snapshot = _manifest;
            }
            else
            {
                return;
            }
        }
        PersistAtomic(snapshot);
    }

    public bool TryGetSegment(string segmentId, out OwnedSegmentRecord? record)
    {
        lock (_syncLock)
        {
            return _manifest.Segments.TryGetValue(segmentId, out record);
        }
    }

    public IReadOnlyList<OwnedSegmentRecord> GetOwnedSegments()
    {
        lock (_syncLock)
        {
            return new List<OwnedSegmentRecord>(_manifest.Segments.Values);
        }
    }

    private void PersistAtomic(OwnershipRegistryManifest manifest)
    {
        string? tempPath = null;
        try
        {
            string dir = Path.GetDirectoryName(_registryFilePath)!;
            Directory.CreateDirectory(dir);

            tempPath = _registryFilePath + ".tmp." + Guid.NewGuid().ToString("N");
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _registryFilePath, overwrite: true);
            tempPath = null;
        }
        catch
        {
            // Disk persistence error: do not crash caller; registry remains in-memory
        }
        finally
        {
            if (tempPath != null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
