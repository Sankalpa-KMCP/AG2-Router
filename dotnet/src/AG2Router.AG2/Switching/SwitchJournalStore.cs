using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AG2Router.AG2.Persistence;

namespace AG2Router.AG2.Switching;

/// <summary>
/// File-backed durable switch transaction journal store conforming to ADR-001.
/// Receives an already-resolved journal file path and delegates atomic replacement to IDurableFileWriter.
/// Does not resolve environment or data-root paths internally.
/// </summary>
public sealed class SwitchJournalStore : ISwitchJournalStore
{
    public const string Magic = SwitchJournalConstants.Magic;
    public const int CurrentSchemaVersion = SwitchJournalConstants.CurrentSchemaVersion;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter<SwitchJournalState>(allowIntegerValues: false) }
    };

    private readonly string _journalFilePath;
    private readonly IDurableFileWriter _fileWriter;

    public string JournalFilePath => _journalFilePath;

    public SwitchJournalStore(string journalFilePath)
        : this(journalFilePath, new DurableFileWriter())
    {
    }

    internal SwitchJournalStore(string journalFilePath, IDurableFileWriter? fileWriter)
    {
        if (string.IsNullOrWhiteSpace(journalFilePath))
        {
            throw new ArgumentException("Journal file path cannot be null or whitespace.", nameof(journalFilePath));
        }

        _journalFilePath = Path.GetFullPath(journalFilePath);
        _fileWriter = fileWriter ?? new DurableFileWriter();
    }

    /// <summary>
    /// Reads and classifies the on-disk switch journal without mutating or deleting corrupt/unsupported files.
    /// </summary>
    public async Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string text;
        try
        {
            if (!File.Exists(_journalFilePath))
            {
                return SwitchJournalReadResult.Absent();
            }

            using var stream = new FileStream(
                _journalFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous);

            if (stream.Length == 0)
            {
                return SwitchJournalReadResult.Corrupt("Journal file is empty (zero bytes).");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SwitchJournalReadResult.IoError(ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return SwitchJournalReadResult.Corrupt("Journal file contains only whitespace.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return SwitchJournalReadResult.Corrupt($"Malformed JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SwitchJournalReadResult.Corrupt("Journal JSON root must be an object.");
            }

            var root = doc.RootElement;

            // 1. Validate Magic
            if (!TryGetProperty(root, "magic", out var magicProp) || magicProp.ValueKind != JsonValueKind.String)
            {
                return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'magic'.");
            }

            string? magic = magicProp.GetString();
            if (!string.Equals(magic, Magic, StringComparison.Ordinal))
            {
                return SwitchJournalReadResult.UnsupportedVersion($"Unsupported magic '{magic}', expected '{Magic}'.");
            }

            // 2. Validate SchemaVersion
            if (!TryGetProperty(root, "schemaVersion", out var versionProp) || !versionProp.TryGetInt32(out int version))
            {
                return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'schemaVersion'.");
            }

            if (version != CurrentSchemaVersion)
            {
                return SwitchJournalReadResult.UnsupportedVersion($"Unsupported schema version {version}, expected {CurrentSchemaVersion}.");
            }

            // 3. Validate TransactionId
            if (!TryGetProperty(root, "transactionId", out var txIdProp) || txIdProp.ValueKind != JsonValueKind.String)
            {
                return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'transactionId'.");
            }

            string? txId = txIdProp.GetString();
            if (string.IsNullOrWhiteSpace(txId))
            {
                return SwitchJournalReadResult.Corrupt("Property 'transactionId' cannot be empty.");
            }

            if (!Guid.TryParse(txId, out _))
            {
                return SwitchJournalReadResult.Corrupt($"Property 'transactionId' '{txId}' is not a valid UUID format.");
            }

            // 4. Validate State
            if (!TryGetProperty(root, "state", out var stateProp) || stateProp.ValueKind != JsonValueKind.String)
            {
                return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'state'.");
            }

            string? stateStr = stateProp.GetString();
            if (string.IsNullOrWhiteSpace(stateStr) ||
                !Enum.TryParse<SwitchJournalState>(stateStr, ignoreCase: false, out var state) ||
                !Enum.IsDefined(state) ||
                state.ToString() != stateStr)
            {
                return SwitchJournalReadResult.Corrupt($"Property 'state' '{stateStr}' is not a valid canonical SwitchJournalState.");
            }

            // 5. Validate UpdatedAt
            if (!TryGetProperty(root, "updatedAt", out var updatedAtProp) || !updatedAtProp.TryGetDateTimeOffset(out var updatedAt))
            {
                return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'updatedAt'.");
            }

            // 6. Validate SourceAccountId
            if (!TryGetProperty(root, "sourceAccountId", out var sourceProp) || sourceProp.ValueKind != JsonValueKind.String)
            {
                return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'sourceAccountId'.");
            }

            string? sourceId = sourceProp.GetString();
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                return SwitchJournalReadResult.Corrupt("Property 'sourceAccountId' cannot be empty.");
            }

            // 7. Validate TargetAccountId
            if (!TryGetProperty(root, "targetAccountId", out var targetProp) || targetProp.ValueKind != JsonValueKind.String)
            {
                return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'targetAccountId'.");
            }

            string? targetId = targetProp.GetString();
            if (string.IsNullOrWhiteSpace(targetId))
            {
                return SwitchJournalReadResult.Corrupt("Property 'targetAccountId' cannot be empty.");
            }

            // 8. Optional QuarantineReasonCode
            string? quarantineReason = null;
            if (TryGetProperty(root, "quarantineReasonCode", out var qProp) && qProp.ValueKind != JsonValueKind.Null)
            {
                if (qProp.ValueKind == JsonValueKind.String)
                {
                    quarantineReason = qProp.GetString();
                }
                else
                {
                    return SwitchJournalReadResult.Corrupt("Property 'quarantineReasonCode' must be a string or null.");
                }
            }

            var entry = new SwitchJournalEntry
            {
                Magic = magic!,
                SchemaVersion = version,
                TransactionId = txId!,
                State = state,
                UpdatedAt = updatedAt,
                SourceAccountId = sourceId!,
                TargetAccountId = targetId!,
                QuarantineReasonCode = quarantineReason
            };

            return SwitchJournalReadResult.Valid(entry);
        }
    }

    /// <summary>
    /// Validates and writes a journal entry atomically to disk.
    /// Overwrites existing journal state via atomic replacement.
    /// </summary>
    public async Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.TransactionId))
        {
            throw new ArgumentException("TransactionId cannot be null or whitespace.", nameof(entry));
        }

        if (!Guid.TryParse(entry.TransactionId, out _))
        {
            throw new ArgumentException($"TransactionId '{entry.TransactionId}' must be a valid UUID format.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.SourceAccountId))
        {
            throw new ArgumentException("SourceAccountId cannot be null or whitespace.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.TargetAccountId))
        {
            throw new ArgumentException("TargetAccountId cannot be null or whitespace.", nameof(entry));
        }

        if (!Enum.IsDefined(entry.State))
        {
            throw new ArgumentException($"State '{entry.State}' is not a defined SwitchJournalState.", nameof(entry));
        }

        var normalized = entry with
        {
            Magic = Magic,
            SchemaVersion = CurrentSchemaVersion,
            UpdatedAt = entry.UpdatedAt == default ? DateTimeOffset.UtcNow : entry.UpdatedAt
        };

        string json = JsonSerializer.Serialize(normalized, SerializerOptions);
        await _fileWriter.WriteAtomicAsync(_journalFilePath, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the journal file if it exists. Idempotent if absent.
    /// Allows I/O and permission exceptions to propagate.
    /// </summary>
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (File.Exists(_journalFilePath))
            {
                File.Delete(_journalFilePath);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Directory does not exist, so file does not exist; cleanly idempotent.
        }

        return Task.CompletedTask;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
