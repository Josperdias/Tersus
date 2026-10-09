using Tersus.Core.IO;

namespace Tersus.Core.Policy;

public sealed record TempRootResult(string? CanonicalRoot, string? Problem)
{
    public bool IsSafe => CanonicalRoot is not null;
}

/// <summary>
/// Decides which folder counts as "the user's TEMP" and refuses to clean when that folder is anywhere unexpected.
/// A mis-set TEMP (for example pointing at C:\, the user profile or Documents) would otherwise turn a harmless
/// ".tmp in TEMP" rule into ".tmp anywhere", so the root must: exist, be a real directory, resolve (through links
/// and 8.3 aliases) to a path strictly inside the user's profile, be at least 3 levels deep, and never equal or
/// contain a protected folder such as Documents, Desktop, Downloads, Pictures, Music, Videos, OneDrive,
/// Windows, Program Files or ProgramData.
/// </summary>
public static class TempRootResolver
{
    public static TempRootResult Resolve(
        IFileSystem fs,
        string tempPath,
        string userProfile,
        IEnumerable<string> protectedFolders)
    {
        ArgumentNullException.ThrowIfNull(fs);

        if (!TryCanonicalDirectory(fs, tempPath, out string root, out string? problem))
        {
            return new TempRootResult(null, $"Pasta TEMP inválida: {problem}");
        }

        if (!TryCanonicalDirectory(fs, userProfile, out string profile, out problem))
        {
            return new TempRootResult(null, $"Perfil do usuário inválido: {problem}");
        }

        if (PathGuard.Depth(root) < 3)
        {
            return new TempRootResult(null, "A pasta TEMP é rasa demais para ser uma pasta por usuário.");
        }

        if (!PathGuard.IsStrictlyUnder(profile, root))
        {
            return new TempRootResult(null,
                "A pasta TEMP está fora do perfil do usuário. Nesta versão o Tersus só limpa um TEMP dentro do perfil.");
        }

        foreach (string folder in protectedFolders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            // Protected folders that do not exist are simply not relevant.
            if (!TryCanonicalDirectory(fs, folder, out string protectedPath, out _))
            {
                if (!PathGuard.TryNormalize(folder, out protectedPath, out _))
                {
                    continue;
                }
            }

            if (PathGuard.IsSameOrAncestorOf(root, protectedPath))
            {
                return new TempRootResult(null, $"A pasta TEMP coincide com ou contém uma pasta protegida ({protectedPath}).");
            }

            if (PathGuard.IsSameOrAncestorOf(protectedPath, root))
            {
                return new TempRootResult(null, $"A pasta TEMP está dentro de uma pasta protegida ({protectedPath}).");
            }
        }

        return new TempRootResult(root, null);
    }

    private static bool TryCanonicalDirectory(IFileSystem fs, string path, out string canonical, out string? problem)
    {
        canonical = string.Empty;
        if (!PathGuard.TryNormalize(path, out string normalized, out PathProblem pathProblem))
        {
            problem = PathGuard.Describe(pathProblem);
            return false;
        }

        FileFacts facts = fs.Probe(normalized);
        if (!facts.Exists || !facts.IsDirectory)
        {
            problem = "a pasta não existe.";
            return false;
        }

        string resolved = normalized;
        if (facts.FinalPath is not null)
        {
            if (!PathGuard.TryNormalize(PathGuard.StripExtendedPrefix(facts.FinalPath), out resolved, out pathProblem))
            {
                problem = PathGuard.Describe(pathProblem);
                return false;
            }
        }

        canonical = resolved;
        problem = null;
        return true;
    }
}
