namespace Tersus.Tests.Support;

/// <summary>Builds random directory trees on disk and remembers what it created, so scan results can be checked independently.</summary>
public sealed class TreeFactory
{
    public List<(string Path, long Length)> Files { get; } = [];

    public List<string> Folders { get; } = [];

    public long TotalBytes => Files.Sum(f => f.Length);

    public static TreeFactory Build(string root, int seed, int folders, int files, int maxDepth = 6, int maxLength = 20_000)
    {
        var factory = new TreeFactory();
        var rng = new Random(seed);
        var dirs = new List<(string Path, int Depth)> { (root, 0) };
        for (int i = 0; i < folders; i++)
        {
            (string parent, int depth) = dirs[rng.Next(dirs.Count)];
            if (depth >= maxDepth)
            {
                continue;
            }

            string path = Path.Combine(parent, "d" + i.ToString("D4"));
            Directory.CreateDirectory(path);
            dirs.Add((path, depth + 1));
            factory.Folders.Add(path);
        }

        for (int i = 0; i < files; i++)
        {
            string dir = dirs[rng.Next(dirs.Count)].Path;
            int length = rng.Next(5) == 0 ? 0 : rng.Next(1, maxLength);
            string name = "f" + i.ToString("D5") + (rng.Next(3) switch { 0 => ".txt", 1 => ".TMP", _ => ".dat" });
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, new byte[length]);
            factory.Files.Add((path, length));
        }

        return factory;
    }
}
