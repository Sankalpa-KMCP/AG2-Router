using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AG2Router.AG2.Persistence;

internal interface IDurableFileWriter
{
    Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken);
}

/// <summary>
/// Writes a same-directory temporary file, flushes its contents to the physical device,
/// and only then replaces the destination. This does not claim directory-entry durability
/// across every filesystem/controller failure mode.
/// </summary>
internal sealed class DurableFileWriter : IDurableFileWriter
{
    public async Task WriteAtomicAsync(
        string destinationPath,
        string content,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException($"Cannot resolve parent directory for '{fullPath}'.");
        Directory.CreateDirectory(directory);

        string tempPath = $"{fullPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        bool replacementCompleted = false;

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 16 * 1024,
                    leaveOpen: true);

                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // A timed-out owner may have released its lock while the asynchronous write
            // was pending. Do not let that stale snapshot replace a newer one afterward.
            cancellationToken.ThrowIfCancellationRequested();
            // No cancellation point is allowed after replacement: callers must be able to
            // publish the same committed snapshot to memory without an ambiguous outcome.
            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }

            replacementCompleted = true;
        }
        finally
        {
            if (!replacementCompleted && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Preserve the original failure. Only this operation's temp is targeted.
                }
            }
        }
    }
}

/// <summary>
/// Coordinates every in-process user of a canonical persistence path. Entries are retained
/// for process lifetime so removing a semaphore can never race a waiter.
/// </summary>
internal static class PathLockRegistry
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim Get(string path) =>
        Locks.GetOrAdd(Path.GetFullPath(path), static _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Cross-process lease shared by the .NET and TypeScript implementations. Atomic creation
/// serializes writers. On Windows the lock is delete-on-close, so normal exit and process
/// termination both release it without a stale-lock check/delete race.
/// </summary>
internal sealed class CrossProcessFileLease : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(15);

    private readonly string _lockPath;
    private readonly FileStream _stream;
    private bool _disposed;

    private CrossProcessFileLease(string lockPath, FileStream stream)
    {
        _lockPath = lockPath;
        _stream = stream;
    }

    public static async Task<CrossProcessFileLease> AcquireAsync(
        string resourcePath,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(resourcePath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException($"Cannot resolve parent directory for '{fullPath}'.");
        Directory.CreateDirectory(directory);

        string lockPath = fullPath + ".lock";
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileOptions options = FileOptions.Asynchronous | FileOptions.WriteThrough;
                if (OperatingSystem.IsWindows()) options |= FileOptions.DeleteOnClose;

                var stream = new FileStream(
                    lockPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 4096,
                    options: options);

                try
                {
                    var owner = JsonSerializer.Serialize(new
                    {
                        pid = Environment.ProcessId,
                        createdAt = DateTimeOffset.UtcNow.ToString("O")
                    });
                    byte[] bytes = Encoding.UTF8.GetBytes(owner);
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                    return new CrossProcessFileLease(lockPath, stream);
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    try { File.Delete(lockPath); } catch { }
                    throw;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Stopwatch.GetElapsedTime(started) >= AcquireTimeout)
                {
                    throw new IOException($"Timed out waiting for persistence lock '{lockPath}'.");
                }

                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _stream.DisposeAsync().ConfigureAwait(false);
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.Delete(_lockPath);
        }
        catch (FileNotFoundException)
        {
            // Already released.
        }
        catch (IOException)
        {
            // Non-Windows stale leases fail closed on the next acquisition.
        }
        catch (UnauthorizedAccessException)
        {
            // Fail closed on the next acquisition rather than masking a committed operation.
        }
    }
}
