using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

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
/// Writes pre-serialized content to a durable same-directory temporary file (write-through,
/// flushed to disk) and returns its path; the caller publishes it with a non-replacing rename
/// or removes it. Shared by the switch journal and its transition marker, whose create-if-absent
/// publications must not use a replacing primitive.
/// </summary>
internal static class DurableTempFile
{
    public static async Task<string> WriteAsync(string destinationPath, string content, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException($"Cannot resolve parent directory for '{fullPath}'.");
        Directory.CreateDirectory(directory);

        string tempPath = $"{fullPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
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
                await using (var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 16 * 1024,
                    leaveOpen: true))
                {
                    await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                stream.Flush(flushToDisk: true);
            }

            return tempPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static void TryDelete(string? tempPath)
    {
        if (tempPath == null)
        {
            return;
        }

        try
        {
            File.Delete(tempPath);
        }
        catch
        {
            // Best-effort temp cleanup; the canonical file is unaffected either way.
        }
    }
}

/// <summary>
/// Outcome of an exact-content conditional file delete. The file is removed only when its
/// current content classifies as an exact match for what the caller owns; every other
/// outcome preserves the bytes untouched.
/// </summary>
internal enum ConditionalDeleteStatus
{
    Deleted,
    NotMatched,
    Absent,
    IoError,
    UnsupportedPlatform
}

internal sealed record ConditionalDeleteResult(
    ConditionalDeleteStatus Status,
    string? Message = null,
    Exception? Exception = null)
{
    public static ConditionalDeleteResult Deleted() => new(ConditionalDeleteStatus.Deleted);
    public static ConditionalDeleteResult NotMatched(string message) => new(ConditionalDeleteStatus.NotMatched, Message: message);
    public static ConditionalDeleteResult Absent() => new(ConditionalDeleteStatus.Absent);
    public static ConditionalDeleteResult IoError(Exception exception) => new(ConditionalDeleteStatus.IoError, Message: exception.Message, Exception: exception);
}

/// <summary>
/// Windows exact-content conditional delete used by the switch journal and its transition
/// marker. The file is opened with GENERIC_READ | DELETE and share mode
/// FILE_SHARE_READ | FILE_SHARE_DELETE, inspected through that same handle, and then marked
/// for delete disposition. The share mode deliberately PERMITS other actors to rename or
/// delete the file while the handle is open (only writers are excluded): deletion is
/// authorized by the content comparison, not by excluding external mutation. Whatever a
/// non-cooperating actor does is therefore still durable evidence — the create-if-absent
/// publish refuses to overwrite anything occupying the name afterwards, and recovery paths
/// reread the canonical state and fail closed on surviving evidence.
/// </summary>
internal static class WindowsConditionalFileDelete
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint DELETE = 0x00010000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const int FileDispositionInfo = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int FileInformationClass,
        ref FILE_DISPOSITION_INFO lpFileInformation,
        uint dwBufferSize);

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Deletes <paramref name="path"/> only if <paramref name="classifyContent"/> reports an
    /// exact match for the bytes read through the delete handle. Verification and disposition
    /// share one handle, so the compared object is the deleted object. A hook runs after the
    /// match is proven and before the disposition is set.
    /// </summary>
    public static async Task<ConditionalDeleteResult> DeleteIfExactAsync(
        string path,
        Func<string, (bool Match, string? NotMatchedReason)> classifyContent,
        Func<Task>? beforeDispositionHookAsync,
        CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return new ConditionalDeleteResult(ConditionalDeleteStatus.UnsupportedPlatform,
                "Conditional deletion is only supported on Windows.");
        }

        SafeFileHandle handle = CreateFileW(
            path,
            GENERIC_READ | DELETE,
            FILE_SHARE_READ | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 /* ERROR_FILE_NOT_FOUND */ or 3 /* ERROR_PATH_NOT_FOUND */)
            {
                return ConditionalDeleteResult.Absent();
            }

            return ConditionalDeleteResult.IoError(
                new Win32Exception(error, $"Failed to open file for verification: win32 error {error}"));
        }

        using (handle)
        {
            string text;
            try
            {
                long length = RandomAccess.GetLength(handle);
                if (length == 0)
                {
                    return ConditionalDeleteResult.NotMatched("File is empty (zero bytes).");
                }
                if (length > 1_000_000)
                {
                    return ConditionalDeleteResult.NotMatched("File exceeds maximum expected size.");
                }

                byte[] buffer = new byte[length];
                int read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(), 0, cancellationToken).ConfigureAwait(false);
                if (read != length)
                {
                    return ConditionalDeleteResult.NotMatched("Failed to read complete file contents.");
                }
                text = Encoding.UTF8.GetString(buffer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ConditionalDeleteResult.IoError(ex);
            }

            var (matches, notMatchedReason) = classifyContent(text);
            if (!matches)
            {
                return ConditionalDeleteResult.NotMatched(notMatchedReason ?? "File contents do not match the expected entry.");
            }

            if (beforeDispositionHookAsync != null)
            {
                await beforeDispositionHookAsync().ConfigureAwait(false);
            }

            var disposition = new FILE_DISPOSITION_INFO { DeleteFile = true };
            if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref disposition, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO>()))
            {
                int err = Marshal.GetLastWin32Error();
                return ConditionalDeleteResult.IoError(
                    new Win32Exception(err, $"Failed to set delete disposition: win32 error {err}"));
            }

            return ConditionalDeleteResult.Deleted();
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
