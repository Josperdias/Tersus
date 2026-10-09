using Tersus.Core.IO;
using Tersus.Core.Policy;

namespace Tersus.Tests.Support;

/// <summary>
/// In-memory file system with Windows-style paths. It lets the tests drive every branch of the policy
/// (links, hard links, 8.3 aliases, locks, access denied, files that change between steps) on any OS.
/// </summary>
public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, FileFacts> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _locked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);
    private ulong _nextId = 100;

    public int ProbeCalls { get; private set; }

    public int HoldCalls { get; private set; }

    public int OpenHolds { get; private set; }

    /// <summary>Invoked at the start of Probe/TryHold with the normalised path; used to simulate races.</summary>
    public Action<string>? OnAccess { get; set; }

    public VolumeSpace? Space { get; set; } = new VolumeSpace(500_000_000_000, 100_000_000_000);

    public FileFacts AddFile(
        string path,
        long length,
        DateTime createdUtc,
        DateTime modifiedUtc,
        FileAttributes attributes = FileAttributes.Archive,
        uint hardLinks = 1,
        string? finalPath = null)
    {
        string normalized = Norm(path);
        var facts = new FileFacts
        {
            Path = normalized,
            Exists = true,
            IsDirectory = false,
            Attributes = attributes,
            Length = length,
            CreationTimeUtc = createdUtc,
            LastWriteTimeUtc = modifiedUtc,
            HardLinkCount = hardLinks,
            Identity = new FileIdentity(0xABCD, _nextId++, 0),
            FinalPath = finalPath ?? normalized,
            IsDeep = true,
        };
        _entries[normalized] = facts;
        return facts;
    }

    public FileFacts AddDirectory(string path, string? finalPath = null, FileAttributes extra = 0)
    {
        string normalized = Norm(path);
        var facts = new FileFacts
        {
            Path = normalized,
            Exists = true,
            IsDirectory = true,
            Attributes = FileAttributes.Directory | extra,
            FinalPath = finalPath ?? normalized,
            Identity = new FileIdentity(0xABCD, _nextId++, 0),
            HardLinkCount = 1,
            IsDeep = true,
        };
        _entries[normalized] = facts;
        return facts;
    }

    public void Update(string path, Func<FileFacts, FileFacts> change)
    {
        string normalized = Norm(path);
        _entries[normalized] = change(_entries[normalized]);
    }

    public void Remove(string path) => _entries.Remove(Norm(path));

    public void Lock(string path) => _locked.Add(Norm(path));

    public void Unlock(string path) => _locked.Remove(Norm(path));

    public void DenyAccess(string path) => _denied.Add(Norm(path));

    public FileFacts Get(string path) => _entries[Norm(path)];

    public bool Exists(string path) => _entries.ContainsKey(Norm(path));

    public IEnumerable<FileFacts> Files => _entries.Values.Where(e => !e.IsDirectory);

    public FileFacts Probe(string path)
    {
        ProbeCalls++;
        if (!PathGuard.TryNormalize(path, out string normalized, out _))
        {
            return FileFacts.Missing(path, ProbeError.Other, "invalid path");
        }

        OnAccess?.Invoke(normalized);
        if (_denied.Contains(normalized))
        {
            return FileFacts.Missing(normalized, ProbeError.AccessDenied);
        }

        return _entries.TryGetValue(normalized, out FileFacts? facts) ? facts : FileFacts.Missing(normalized);
    }

    public FileFacts TryHold(string path, out IDisposable? hold)
    {
        HoldCalls++;
        hold = null;
        if (!PathGuard.TryNormalize(path, out string normalized, out _))
        {
            return FileFacts.Missing(path, ProbeError.Other, "invalid path");
        }

        OnAccess?.Invoke(normalized);
        if (_denied.Contains(normalized))
        {
            return FileFacts.Missing(normalized, ProbeError.AccessDenied);
        }

        if (!_entries.TryGetValue(normalized, out FileFacts? facts))
        {
            return FileFacts.Missing(normalized);
        }

        if (_locked.Contains(normalized))
        {
            return FileFacts.Missing(normalized, ProbeError.SharingViolation);
        }

        OpenHolds++;
        hold = new Releaser(this);
        return facts;
    }

    public VolumeSpace? GetVolumeSpace(string path) => Space;

    private static string Norm(string path)
    {
        if (!PathGuard.TryNormalize(path, out string normalized, out PathProblem problem))
        {
            throw new ArgumentException($"Test path is not a plain drive path: {path} ({problem})");
        }

        return normalized;
    }

    private sealed class Releaser(FakeFileSystem owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.OpenHolds--;
            }
        }
    }
}
