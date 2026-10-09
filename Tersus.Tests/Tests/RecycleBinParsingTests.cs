using System.Text;
using Tersus.Core.Cleanup;
using Tersus.Core.Windows;
using Tersus.Tests.Framework;

namespace Tersus.Tests;

[Category("recycle")]
public class RecycleBinParsingTests
{
    private static byte[] V2(string path, long size, DateTime deletedUtc)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.Unicode);
        w.Write(2L);
        w.Write(size);
        w.Write(deletedUtc.ToFileTimeUtc());
        w.Write(path.Length + 1);
        w.Write(Encoding.Unicode.GetBytes(path + "\0"));
        return ms.ToArray();
    }

    private static byte[] V1(string path, long size, DateTime deletedUtc)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.Unicode);
        w.Write(1L);
        w.Write(size);
        w.Write(deletedUtc.ToFileTimeUtc());
        byte[] field = new byte[520];
        byte[] raw = Encoding.Unicode.GetBytes(path + "\0");
        Array.Copy(raw, field, raw.Length);
        w.Write(field);
        return ms.ToArray();
    }

    [Test]
    public void Parses_version_2_records_from_windows_10_and_11()
    {
        var when = new DateTime(2026, 10, 9, 15, 0, 0, DateTimeKind.Utc);
        byte[] data = V2(@"C:\Users\u\AppData\Local\Temp\abc \u00E7\u00E3o.tmp", 12345, when);
        Assert.True(RecycleBinInfoFile.TryParse(data, out string path, out long size, out DateTime deleted));
        Assert.Equal(@"C:\Users\u\AppData\Local\Temp\abc \u00E7\u00E3o.tmp", path);
        Assert.Equal(12345L, size);
        Assert.Equal(when, deleted);
    }

    [Test]
    public void Parses_version_1_records_with_the_fixed_path_field()
    {
        byte[] data = V1(@"C:\Temp\old.tmp", 77, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        Assert.Equal(544, data.Length);
        Assert.True(RecycleBinInfoFile.TryParse(data, out string path, out long size, out _));
        Assert.Equal(@"C:\Temp\old.tmp", path);
        Assert.Equal(77L, size);
    }

    [Test]
    public void Rejects_damaged_or_hostile_records_without_throwing()
    {
        var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        byte[] good = V2(@"C:\a.tmp", 1, when);
        Assert.False(RecycleBinInfoFile.TryParse([], out _, out _, out _));
        Assert.False(RecycleBinInfoFile.TryParse(good.AsSpan(0, 20), out _, out _, out _));
        Assert.False(RecycleBinInfoFile.TryParse(good.AsSpan(0, good.Length - 6), out _, out _, out _), "path shorter than its declared length");

        byte[] badVersion = (byte[])good.Clone();
        badVersion[0] = 9;
        Assert.False(RecycleBinInfoFile.TryParse(badVersion, out _, out _, out _));

        byte[] negativeSize = (byte[])good.Clone();
        BitConverter.GetBytes(-5L).CopyTo(negativeSize, 8);
        Assert.False(RecycleBinInfoFile.TryParse(negativeSize, out _, out _, out _));

        byte[] hugeLength = (byte[])good.Clone();
        BitConverter.GetBytes(int.MaxValue).CopyTo(hugeLength, 24);
        Assert.False(RecycleBinInfoFile.TryParse(hugeLength, out _, out _, out _));

        byte[] zeroLength = (byte[])good.Clone();
        BitConverter.GetBytes(0).CopyTo(zeroLength, 24);
        Assert.False(RecycleBinInfoFile.TryParse(zeroLength, out _, out _, out _));

        byte[] noTime = (byte[])good.Clone();
        BitConverter.GetBytes(0L).CopyTo(noTime, 16);
        Assert.False(RecycleBinInfoFile.TryParse(noTime, out _, out _, out _));

        var rng = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            byte[] noise = new byte[rng.Next(0, 700)];
            rng.NextBytes(noise);
            RecycleBinInfoFile.TryParse(noise, out _, out _, out _);
        }
    }

    [Test]
    public void The_data_file_name_swaps_the_prefix()
    {
        Assert.Equal("$R1A2B3C.tmp", RecycleBinInfoFile.DataFileNameFor("$I1A2B3C.tmp"));
        Assert.Equal("$R1A2B3C.tmp", RecycleBinInfoFile.DataFileNameFor("$i1A2B3C.tmp"));
        Assert.Equal(string.Empty, RecycleBinInfoFile.DataFileNameFor("other.tmp"));
        Assert.Equal(string.Empty, RecycleBinInfoFile.DataFileNameFor("$I"));
    }

    private const string Policies = RecycleBinPolicy.PoliciesKey;
    private const string BitBucket = RecycleBinPolicy.BitBucketKey;

    // key: (localMachine, subKey, name)
    private static RecycleBinPolicy.Result Settings(Dictionary<(bool, string, string), int> values, string? guid = "{AAAA-BBBB}") =>
        RecycleBinPolicy.Evaluate(@"C:\", (lm, k, n) => values.TryGetValue((lm, k, n), out int v) ? v : null, _ => guid);

    [Test]
    public void A_clean_registry_means_the_recycle_bin_is_usable()
    {
        RecycleBinPolicy.Result r = Settings([]);
        Assert.False(r.Disabled);
        Assert.Null(r.MaxCapacityBytes?.ToString());
    }

    [Test]
    public void Any_setting_that_makes_the_shell_delete_for_good_disables_cleaning()
    {
        Assert.True(Settings(new() { [(true, Policies, "NoRecycleFiles")] = 1 }).Disabled);
        Assert.True(Settings(new() { [(false, Policies, "NoRecycleFiles")] = 1 }).Disabled);
        Assert.True(Settings(new() { [(false, BitBucket, "NukeOnDelete")] = 1 }).Disabled);
        Assert.True(Settings(new() { [(false, BitBucket + @"\Volume\{AAAA-BBBB}", "NukeOnDelete")] = 1 }).Disabled);
        RecycleBinPolicy.Result off = Settings(new() { [(false, BitBucket, "NukeOnDelete")] = 1 });
        Assert.True(off.Reason!.Length > 10);
    }

    [Test]
    public void Zero_values_do_not_disable_and_capacity_is_read_in_megabytes()
    {
        Assert.False(Settings(new() { [(false, BitBucket, "NukeOnDelete")] = 0 }).Disabled);
        RecycleBinPolicy.Result r = Settings(new() { [(false, BitBucket + @"\Volume\{AAAA-BBBB}", "MaxCapacity")] = 2048 });
        Assert.False(r.Disabled);
        Assert.Equal(2048L * 1024 * 1024, r.MaxCapacityBytes);
        Assert.Null(Settings(new() { [(false, BitBucket + @"\Volume\{AAAA-BBBB}", "MaxCapacity")] = 0 }).MaxCapacityBytes?.ToString());
        Assert.Null(Settings(new() { [(false, BitBucket + @"\Volume\{AAAA-BBBB}", "MaxCapacity")] = 2048 }, guid: null).MaxCapacityBytes?.ToString());
    }
}
