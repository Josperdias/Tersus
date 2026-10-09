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
                // Clear attributes first so read-only test files do not block cleanup.
                foreach (string f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
