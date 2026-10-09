namespace Tersus.Core.Scanning;

public sealed record DriveEntry(string Root, string Label, string Format, DriveType Type, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public double UsedFraction => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes;

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Root : $"{Root}  {Label}";
}

public static class DriveCatalog
{
    /// <summary>Local fixed and removable drives that are ready. Network and optical drives are left out on purpose (slow, and not what a disk cleaner is for).</summary>
    public static IReadOnlyList<DriveEntry> List()
    {
        var list = new List<DriveEntry>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return list;
        }

        foreach (DriveInfo d in drives)
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady)
                {
                    continue;
                }

                list.Add(new DriveEntry(d.Name, d.VolumeLabel, d.DriveFormat, d.DriveType, d.TotalSize, d.TotalFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that disappears while being queried is simply not listed.
            }
        }

        return list;
    }
}
