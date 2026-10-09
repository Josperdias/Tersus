using System.Text;

namespace Tersus.Core.Windows;

/// <summary>
/// Parser for the "$I" metadata file Windows writes next to every item in the Recycle Bin ($Recycle.Bin\SID\$Ixxxxxx.ext).
/// It records the original path, size and deletion time, which lets Tersus PROVE that a file reached the bin instead of being
/// deleted for good. Version 1 (Vista-8.1) has a fixed 520-byte path field; version 2 (Windows 10/11) stores the path length first.
/// Pure and portable so it can be unit-tested everywhere.
/// </summary>
public static class RecycleBinInfoFile
{
    public static bool TryParse(ReadOnlySpan<byte> data, out string originalPath, out long originalSize, out DateTime deletedUtc)
    {
        originalPath = string.Empty;
        originalSize = 0;
        deletedUtc = default;
        if (data.Length < 28)
        {
            return false;
        }

        long version = BitConverter.ToInt64(data[..8]);
        originalSize = BitConverter.ToInt64(data.Slice(8, 8));
        long fileTime = BitConverter.ToInt64(data.Slice(16, 8));
        if (originalSize < 0 || fileTime <= 0 || fileTime > 2_650_467_743_999_999_999L)
        {
            return false;
        }

        deletedUtc = DateTime.FromFileTimeUtc(fileTime);
        ReadOnlySpan<byte> pathBytes;
        if (version == 1)
        {
            int available = Math.Min(520, data.Length - 24);
            pathBytes = data.Slice(24, available);
        }
        else if (version == 2)
        {
            int chars = BitConverter.ToInt32(data.Slice(24, 4));
            if (chars <= 0 || chars > 32_768 || 28 + (chars * 2) > data.Length)
            {
                return false;
            }

            pathBytes = data.Slice(28, chars * 2);
        }
        else
        {
            return false;
        }

        string text = Encoding.Unicode.GetString(pathBytes);
        int nul = text.IndexOf('\0', StringComparison.Ordinal);
        originalPath = nul >= 0 ? text[..nul] : text;
        return originalPath.Length > 0;
    }

    /// <summary>The "$R" file holding the data has the same name as the "$I" file with the prefix swapped.</summary>
    public static string DataFileNameFor(string infoFileName) =>
        infoFileName.Length > 2 && infoFileName.StartsWith("$I", StringComparison.OrdinalIgnoreCase) ? "$R" + infoFileName[2..] : string.Empty;
}
