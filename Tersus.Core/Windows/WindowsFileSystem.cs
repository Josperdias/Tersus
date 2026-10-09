using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Tersus.Core.IO;

namespace Tersus.Core.Windows;

/// <summary>
/// File-system access for the safety checks, based on open handles. A handle gives facts that cannot be faked by path tricks:
/// the real final path (links, junctions and 8.3 aliases resolved), the number of hard links and a stable file identity.
/// Everything is read-only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileSystem : IFileSystem
{
    private const uint AllShares = NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete;

    public FileFacts Probe(string path)
    {
        // Attributes-only access with every share mode: probing must never disturb or be blocked by other programs.
        using SafeFileHandle handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.FileReadAttributes,
            AllShares,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return Failure(path, Marshal.GetLastWin32Error());
        }

        return FactsFromHandle(path, handle);
    }

    public FileFacts TryHold(string path, out IDisposable? hold)
    {
        // Read access with FILE_SHARE_READ|DELETE only: if any other program has the file open for writing (or exclusively)
        // this open fails, and while we hold it nobody can open it for writing. Deleting/renaming stays possible, which is what
        // the Recycle Bin needs. FILE_FLAG_OPEN_REPARSE_POINT: a link in the last component is opened as the link itself.
        SafeFileHandle handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.GenericRead,
            NativeMethods.FileShareRead | NativeMethods.FileShareDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            hold = null;
            return Failure(path, error);
        }

        try
        {
            FileFacts facts = FactsFromHandle(path, handle);
            hold = handle;
            return facts;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public VolumeSpace? GetVolumeSpace(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return new VolumeSpace(drive.TotalSize, drive.TotalFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IEnumerable<DirectoryEntry> EnumerateDirectory(string path)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
        };

        return new FileSystemEnumerable<DirectoryEntry>(
            path,
            (ref FileSystemEntry e) => new DirectoryEntry(
                e.FileName.ToString(),
                e.IsDirectory,
                e.Attributes,
                e.IsDirectory ? 0 : e.Length,
                e.CreationTimeUtc.UtcDateTime,
                e.LastWriteTimeUtc.UtcDateTime),
            options);
    }

    /// <summary>Size actually allocated on disk (differs from the logical size for compressed or sparse files). Null if unknown.</summary>
    public static long? AllocatedSize(string path)
    {
        uint low = NativeMethods.GetCompressedFileSizeW(path, out uint high);
        if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
        {
            return null;
        }

        return ((long)high << 32) | low;
    }

    private static FileFacts FactsFromHandle(string path, SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out NativeMethods.ByHandleFileInformation info))
        {
            return Failure(path, Marshal.GetLastWin32Error());
        }

        var attributes = (FileAttributes)info.FileAttributes;
        FileIdentity identity = new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, 0);
        if (NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileIdInfoClass, out NativeMethods.FileIdInfo id128, (uint)Marshal.SizeOf<NativeMethods.FileIdInfo>()))
        {
            identity = new FileIdentity(id128.VolumeSerialNumber, id128.FileIdLow, id128.FileIdHigh);
        }

        return new FileFacts
        {
            Path = path,
            Exists = true,
            IsDirectory = (attributes & FileAttributes.Directory) != 0,
            Attributes = attributes,
            Length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow,
            CreationTimeUtc = ToUtc(info.CreationTime),
            LastWriteTimeUtc = ToUtc(info.LastWriteTime),
            HardLinkCount = info.NumberOfLinks,
            Identity = identity,
            FinalPath = FinalPath(handle),
            IsDeep = true,
        };
    }

    private static string? FinalPath(SafeFileHandle handle)
    {
        var sb = new StringBuilder(512);
        uint needed = NativeMethods.GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
        if (needed == 0)
        {
            return null;
        }

        if (needed >= sb.Capacity)
        {
            sb = new StringBuilder((int)needed + 1);
            needed = NativeMethods.GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
            if (needed == 0 || needed >= sb.Capacity)
            {
                return null;
            }
        }

        return sb.ToString();
    }

    private static DateTime ToUtc(NativeMethods.FileTime t)
    {
        long ticks = t.Ticks;
        return ticks <= 0 ? DateTime.FromFileTimeUtc(0) : DateTime.FromFileTimeUtc(ticks);
    }

    private static FileFacts Failure(string path, int error)
    {
        ProbeError kind = error switch
        {
            NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound => ProbeError.NotFound,
            NativeMethods.ErrorAccessDenied => ProbeError.AccessDenied,
            NativeMethods.ErrorSharingViolation or NativeMethods.ErrorLockViolation => ProbeError.SharingViolation,
            NativeMethods.ErrorFilenameExcedRange => ProbeError.PathTooLong,
            _ => ProbeError.Other,
        };

        return FileFacts.Missing(path, kind, $"Win32 error {error}");
    }
}
