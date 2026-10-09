using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Tersus.Core.Windows;

/// <summary>
/// The complete list of native functions Tersus calls. The static audit in the test suite compares this list with an allow-list:
/// adding a function that deletes, writes, kills a process or edits the registry fails the build.
/// Read-only except <see cref="SHFileOperationW"/>, which is only ever used with FOF_ALLOWUNDO (Recycle Bin).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    internal const uint FileReadAttributes = 0x0080;
    internal const uint GenericRead = 0x80000000;
    internal const uint FileShareRead = 0x1;
    internal const uint FileShareWrite = 0x2;
    internal const uint FileShareDelete = 0x4;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const int FileIdInfoClass = 18;

    internal const uint FoDelete = 0x0003;
    internal const ushort FofSilent = 0x0004;
    internal const ushort FofNoConfirmation = 0x0010;
    internal const ushort FofAllowUndo = 0x0040;
    internal const ushort FofNoErrorUi = 0x0400;
    internal const ushort FofWantNukeWarning = 0x4000;

    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorSharingViolation = 32;
    internal const int ErrorLockViolation = 33;
    internal const int ErrorFilenameExcedRange = 206;
    internal const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly long Ticks => ((long)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct ShQueryRbInfo
    {
        public uint Size;
        public long SizeBytes;
        public long NumItems;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    internal static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInfo info, uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    internal static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint pathChars, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetCompressedFileSizeW")]
    internal static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeNameForVolumeMountPointW(string volumeMountPoint, StringBuilder volumeName, uint bufferLength);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    internal static extern int SHFileOperationW(ref ShFileOpStruct fileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    internal static extern int SHQueryRecycleBinW(string? rootPath, ref ShQueryRbInfo info);
}
