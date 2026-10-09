namespace Tersus.Tests.Support;

/// <summary>A unique scratch directory under the system temp folder, removed on dispose (test code only).</summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string? prefix = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Tersus-test-" + (prefix ?? "t") + "-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relative, string content = "x", DateTime? modifiedUtc = null, DateTime? createdUtc = null)
    {
        string full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (createdUtc is not null)
        {
            File.SetCreationTimeUtc(full, createdUtc.Value);
        }

        if (modifiedUtc is not null)
        {
            File.SetLastWriteTimeUtc(full, modifiedUtc.Value);
        }

        return full;
    }

    public string WriteBytes(string relative, byte[] content)
    {
        string full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                DeleteTreeWithoutFollowingLinks(Path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes a directory tree WITHOUT ever following a symbolic link or junction (a link is removed itself, its target is untouched),
    /// so a symlink loop can never make the cleanup run away or reach outside the scratch folder.
    /// </summary>
    private static void DeleteTreeWithoutFollowingLinks(string dir)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            try
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (entry is DirectoryInfo)
                    {
                        Directory.Delete(entry.FullName, recursive: false);
                    }
                    else
                    {
                        File.Delete(entry.FullName);
                    }
                }
                else if (entry is DirectoryInfo sub)
                {
                    DeleteTreeWithoutFollowingLinks(sub.FullName);
                }
                else
                {
                    File.SetAttributes(entry.FullName, FileAttributes.Normal);
                    File.Delete(entry.FullName);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(dir, recursive: false);
    }
}
