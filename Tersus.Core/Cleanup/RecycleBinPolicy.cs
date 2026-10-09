namespace Tersus.Core.Cleanup;

/// <summary>
/// Decides, from registry values read elsewhere, whether the Windows shell will really RECYCLE or silently delete for good.
/// Portable (no registry types) so the decision logic is unit-tested on every OS; the Windows wrapper only feeds it values.
/// </summary>
public static class RecycleBinPolicy
{
    public readonly record struct Result(bool Disabled, string? Reason, long? MaxCapacityBytes);

    /// <summary>Reads one DWORD value; <paramref name="localMachine"/> selects HKLM instead of HKCU. Null when absent or unreadable.</summary>
    public delegate int? DwordReader(bool localMachine, string subKey, string name);

    public const string PoliciesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    public const string BitBucketKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";

    public static Result Evaluate(string volumeRoot, DwordReader read, Func<string, string?> volumeGuid)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(volumeGuid);

        foreach (bool localMachine in new[] { true, false })
        {
            if (read(localMachine, PoliciesKey, "NoRecycleFiles") == 1)
            {
                return new Result(true, "Uma política do Windows desativa a Lixeira (NoRecycleFiles).", null);
            }
        }

        if (read(false, BitBucketKey, "NukeOnDelete") == 1)
        {
            return new Result(true, "A Lixeira está configurada para apagar de vez ('Não mover arquivos para a Lixeira').", null);
        }

        string? guid = volumeGuid(volumeRoot);
        long? capacity = null;
        if (guid is not null)
        {
            string volumeKey = BitBucketKey + @"\Volume\" + guid;
            if (read(false, volumeKey, "NukeOnDelete") == 1)
            {
                return new Result(true, $"A Lixeira da unidade {volumeRoot} está configurada para apagar de vez.", null);
            }

            int? maxMb = read(false, volumeKey, "MaxCapacity");
            if (maxMb is > 0)
            {
                capacity = (long)maxMb.Value * 1024 * 1024;
            }
        }

        return new Result(false, null, capacity);
    }
}
