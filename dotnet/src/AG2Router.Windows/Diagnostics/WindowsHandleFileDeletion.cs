using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AG2Router.Windows.Diagnostics;

/// <summary>
/// Native Windows handle-based file deletion adapter enforcing Option B and Finding A/B.
/// Verifies physical directory containment, rejects root and leaf reparse points/junctions,
/// resolves canonical volume paths via GetFinalPathNameByHandle, confirms authoritative
/// OS file identities (VolumeSerialNumber, FileIndex), and sets deletion disposition on
/// the exact open handle via SetFileInformationByHandle.
/// </summary>
public static class WindowsHandleFileDeletion
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint DELETE = 0x00010000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    private const int FileDispositionInfo = 4;
    private const int FileDispositionInfoEx = 21;

    private const uint FILE_DISPOSITION_FLAG_DELETE = 0x00000001;
    private const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x00000002;

    private const uint VOLUME_NAME_DOS = 0x0;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO_EX
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
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
        IntPtr lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        [Out] StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    /// <summary>
    /// Queries the authoritative OS file identity from an open file handle.
    /// </summary>
    public static bool TryGetFileIdentity(
        SafeFileHandle handle,
        out uint volumeSerialNumber,
        out uint fileIndexHigh,
        out uint fileIndexLow)
    {
        volumeSerialNumber = 0;
        fileIndexHigh = 0;
        fileIndexLow = 0;

        if (handle.IsInvalid || handle.IsClosed)
        {
            return false;
        }

        if (GetFileInformationByHandle(handle, out var info))
        {
            volumeSerialNumber = info.dwVolumeSerialNumber;
            fileIndexHigh = info.nFileIndexHigh;
            fileIndexLow = info.nFileIndexLow;
            return true;
        }

        return false;
    }

    public static bool TryDeleteOwnedFile(
        string filePath,
        string expectedDirectory,
        Func<Stream, bool> verifyOwnershipOnHandle)
    {
        ArgumentNullException.ThrowIfNull(verifyOwnershipOnHandle);
        return TryDeleteOwnedFile(filePath, expectedDirectory, (stream, _, _, _) => verifyOwnershipOnHandle(stream));
    }

    /// <summary>
    /// Executes safe handle-based deletion of an owned diagnostic file (Option B).
    /// </summary>
    /// <param name="filePath">Target candidate file path.</param>
    /// <param name="expectedDirectory">The expected enclosing directory boundary.</param>
    /// <param name="verifyOwnershipOnHandle">Callback verifying ownership on the exact open stream handle and file identity.</param>
    /// <returns>True if and only if physical containment, reparse-point rejection, ownership, and handle deletion disposition succeed.</returns>
    public static bool TryDeleteOwnedFile(
        string filePath,
        string expectedDirectory,
        Func<Stream, uint, uint, uint, bool> verifyOwnershipOnHandle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDirectory);
        ArgumentNullException.ThrowIfNull(verifyOwnershipOnHandle);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(filePath);
            string fullDir = Path.GetFullPath(expectedDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 1. Establish trusted owned-directory boundary using actual directory handle
            using SafeFileHandle dirHandle = CreateFile(
                fullDir,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);

            if (dirHandle.IsInvalid)
            {
                return false;
            }

            if (!GetFileInformationByHandle(dirHandle, out var dirInfo))
            {
                return false;
            }

            // Reject directory if it is a reparse point (junction / symlink root redirection)
            if ((dirInfo.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                return false;
            }

            if ((dirInfo.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                return false;
            }

            // Resolve canonical directory physical path
            var dirSb = new StringBuilder(1024);
            uint dirPathLen = GetFinalPathNameByHandle(dirHandle, dirSb, (uint)dirSb.Capacity, VOLUME_NAME_DOS);
            if (dirPathLen == 0 || dirPathLen >= dirSb.Capacity)
            {
                return false;
            }
            string canonicalDir = dirSb.ToString().TrimEnd('\\');

            // 2. Open candidate file non-destructively without following reparse points (FILE_FLAG_OPEN_REPARSE_POINT)
            uint desiredAccess = GENERIC_READ | DELETE;
            uint shareMode = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
            uint flags = FILE_ATTRIBUTE_NORMAL | FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT;

            using SafeFileHandle safeHandle = CreateFile(
                fullPath,
                desiredAccess,
                shareMode,
                IntPtr.Zero,
                OPEN_EXISTING,
                flags,
                IntPtr.Zero);

            if (safeHandle.IsInvalid)
            {
                return false;
            }

            if (!GetFileInformationByHandle(safeHandle, out var fileInfo))
            {
                return false;
            }

            // Reject candidate if it is a reparse point (leaf symlink / junction) or directory
            if ((fileInfo.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                return false;
            }

            if ((fileInfo.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                return false;
            }

            // Verify volume identity match between candidate file and trusted directory
            if (fileInfo.dwVolumeSerialNumber != dirInfo.dwVolumeSerialNumber)
            {
                return false;
            }

            // Resolve canonical file physical path
            var fileSb = new StringBuilder(1024);
            uint filePathLen = GetFinalPathNameByHandle(safeHandle, fileSb, (uint)fileSb.Capacity, VOLUME_NAME_DOS);
            if (filePathLen == 0 || filePathLen >= fileSb.Capacity)
            {
                return false;
            }
            string canonicalFile = fileSb.ToString();
            string? canonicalFileParent = Path.GetDirectoryName(canonicalFile)?.TrimEnd('\\');

            // Physical containment verification: canonical parent directory MUST match canonical directory boundary
            if (!string.Equals(canonicalFileParent, canonicalDir, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 3. Ownership validation directly on this exact open stream handle and authoritative OS file identity
            using (var stream = new FileStream(safeHandle, FileAccess.Read, bufferSize: 4096, isAsync: false))
            {
                if (!verifyOwnershipOnHandle(stream, fileInfo.dwVolumeSerialNumber, fileInfo.nFileIndexHigh, fileInfo.nFileIndexLow))
                {
                    // Not proven owned: handle closes normally without deletion disposition. File remains untouched!
                    return false;
                }

                // Ownership confirmed on this exact object. Request deletion through this SAME handle.
                bool dispositionSet = false;

                // Try POSIX delete semantics (FileDispositionInfoEx) first where supported
                var dispEx = new FILE_DISPOSITION_INFO_EX
                {
                    Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                };

                int exSize = Marshal.SizeOf<FILE_DISPOSITION_INFO_EX>();
                IntPtr exPtr = Marshal.AllocHGlobal(exSize);
                try
                {
                    Marshal.StructureToPtr(dispEx, exPtr, false);
                    dispositionSet = SetFileInformationByHandle(safeHandle, FileDispositionInfoEx, exPtr, (uint)exSize);
                }
                finally
                {
                    Marshal.FreeHGlobal(exPtr);
                }

                // Fall back to standard FileDispositionInfo if Ex is not supported
                if (!dispositionSet)
                {
                    var disp = new FILE_DISPOSITION_INFO { DeleteFile = true };
                    int infoSize = Marshal.SizeOf<FILE_DISPOSITION_INFO>();
                    IntPtr infoPtr = Marshal.AllocHGlobal(infoSize);
                    try
                    {
                        Marshal.StructureToPtr(disp, infoPtr, false);
                        dispositionSet = SetFileInformationByHandle(safeHandle, FileDispositionInfo, infoPtr, (uint)infoSize);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(infoPtr);
                    }
                }

                if (!dispositionSet)
                {
                    return false;
                }
            } // Stream disposal closes safeHandle, committing the deletion on Windows

            return !File.Exists(fullPath);
        }
        catch
        {
            return false;
        }
    }
}
