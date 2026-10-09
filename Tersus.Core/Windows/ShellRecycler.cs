using System.Runtime.Versioning;
using System.Security.Principal;
using Tersus.Core.Cleanup;
using Tersus.Core.Policy;
using Tersus.Core.Storage;

namespace Tersus.Core.Windows;

/// <summary>
/// Sends ONE file to the Windows Recycle Bin and proves it got there.
/// <list type="bullet">
/// <item>Uses the shell delete with FOF_ALLOWUNDO (recycle) and FOF_WANTNUKEWARNING (the shell must warn instead of deleting for good).</item>
/// <item>Before the first real file of a session it checks the settings that make the shell delete permanently, then sends a tiny
/// self-test file from Tersus' own data folder through the exact same code path and looks for it in the bin.</item>
/// <item>After every file it looks for the matching "$I" record (original path + size) and the "$R" data file inside the user's
/// bin folder. A file that disappeared WITHOUT such proof is reported as <see cref="RecycleStatus.GoneWithoutProof"/>, which stops the batch.</item>
/// <item>The shell call runs on its own STA thread with a timeout; if it never returns, further cleaning is disabled for the session.</item>
/// <item>It never empties the bin and has no code path that deletes a file any other way.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellRecycler(AppDataFiles files, TimeSpan? shellTimeout = null) : IRecycler
{
    public const string CanaryPrefix = "Tersus-teste-lixeira-";

    private const ushort Flags = NativeMethods.FofAllowUndo | NativeMethods.FofNoConfirmation | NativeMethods.FofNoErrorUi
        | NativeMethods.FofSilent | NativeMethods.FofWantNukeWarning;

    private readonly TimeSpan _timeout = shellTimeout ?? TimeSpan.FromSeconds(45);
    private readonly HashSet<string> _verifiedVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private volatile bool _stuck;

    public RecyclerPreflight Preflight(string tempRoot)
    {
        if (_stuck)
        {
            return new RecyclerPreflight(false, "Uma operação anterior da Lixeira não respondeu. Feche e abra o Tersus antes de tentar de novo.");
        }

        string? root = Path.GetPathRoot(tempRoot);
        if (string.IsNullOrEmpty(root))
        {
            return new RecyclerPreflight(false, "Não foi possível identificar a unidade da pasta TEMP.");
        }

        try
        {
            if (new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                return new RecyclerPreflight(false, "A pasta TEMP não está em um disco fixo local; a Lixeira não é garantida ali.");
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new RecyclerPreflight(false, "Não foi possível consultar a unidade da pasta TEMP.");
        }

        RecycleBinPolicy.Result settings = RecycleBinSettings.Read(root);
        if (settings.Disabled)
        {
            return new RecyclerPreflight(false, settings.Reason!);
        }

        string? sid = CurrentSid();
        if (sid is null)
        {
            return new RecyclerPreflight(false, "Não foi possível identificar o seu usuário para localizar a Lixeira.");
        }

        lock (_gate)
        {
            if (!_verifiedVolumes.Contains(root))
            {
                RecyclerPreflight? failed = RunSelfTest(root, sid);
                if (failed is not null)
                {
                    return failed;
                }

                _verifiedVolumes.Add(root);
            }
        }

        return new RecyclerPreflight(true, "Lixeira verificada: um arquivo de teste foi enviado a ela e encontrado com sucesso.", settings.MaxCapacityBytes);
    }

    public RecycleOutcome Recycle(string path, long expectedLength)
    {
        if (_stuck)
        {
            return new RecycleOutcome(RecycleStatus.NotMoved, "Operações da Lixeira estão desativadas nesta sessão.");
        }

        string? sid = CurrentSid();
        string? root = Path.GetPathRoot(path);
        if (sid is null || string.IsNullOrEmpty(root))
        {
            return new RecycleOutcome(RecycleStatus.NotMoved, "Não foi possível localizar a Lixeira do usuário.");
        }

        return RecycleCore(path, expectedLength, root, sid);
    }

    private RecyclerPreflight? RunSelfTest(string root, string sid)
    {
        string canary;
        try
        {
            canary = files.CreateCanaryFile(CanaryPrefix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RecyclerPreflight(false, "Não foi possível criar o arquivo de teste da Lixeira: " + ex.Message);
        }

        string? canaryRoot = Path.GetPathRoot(canary);
        if (!string.Equals(canaryRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            files.TryDeleteOwnFile(canary);
            return new RecyclerPreflight(false, "A pasta de dados do Tersus e a pasta TEMP estão em unidades diferentes; o teste da Lixeira não é possível.");
        }

        long length = new FileInfo(canary).Length;
        RecycleOutcome outcome = RecycleCore(canary, length, root, sid);
        return outcome.Status == RecycleStatus.Recycled
            ? null
            : new RecyclerPreflight(false, "O teste da Lixeira falhou: " + outcome.Message);
    }

    private RecycleOutcome RecycleCore(string path, long expectedLength, string volumeRoot, string sid)
    {
        if (!PathGuard.TryNormalize(path, out string normalized, out PathProblem problem))
        {
            return new RecycleOutcome(RecycleStatus.NotMoved, PathGuard.Describe(problem));
        }

        FileInfo info;
        try
        {
            info = new FileInfo(normalized);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return new RecycleOutcome(RecycleStatus.NotMoved, "Não é um arquivo comum.");
            }

            if (info.Length != expectedLength)
            {
                return new RecycleOutcome(RecycleStatus.NotMoved, "O tamanho do arquivo mudou.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RecycleOutcome(RecycleStatus.NotMoved, "Não foi possível ler o arquivo: " + ex.Message);
        }

        DateTime started = DateTime.UtcNow;
        (int code, bool aborted, bool timedOut) = RunShellDelete(normalized);
        if (timedOut)
        {
            _stuck = true;
            return new RecycleOutcome(
                File.Exists(normalized) ? RecycleStatus.Failed : RecycleStatus.GoneWithoutProof,
                "O Windows não respondeu a tempo (pode haver uma janela de confirmação aberta).");
        }

        bool stillThere = File.Exists(normalized);
        if (stillThere)
        {
            return new RecycleOutcome(
                RecycleStatus.Failed,
                code != 0 || aborted
                    ? $"O Windows não moveu o arquivo (código 0x{code:X}{(aborted ? ", cancelado" : string.Empty)})."
                    : "O Windows informou sucesso, mas o arquivo continua no lugar.");
        }

        return FindBinEntry(volumeRoot, sid, normalized, expectedLength, started)
            ? new RecycleOutcome(RecycleStatus.Recycled, "Enviado à Lixeira.")
            : new RecycleOutcome(RecycleStatus.GoneWithoutProof, "O arquivo saiu da pasta, mas não foi encontrada a entrada correspondente na Lixeira.");
    }

    private (int Code, bool Aborted, bool TimedOut) RunShellDelete(string path)
    {
        var op = new NativeMethods.ShFileOpStruct
        {
            Hwnd = IntPtr.Zero,
            Func = NativeMethods.FoDelete,
            From = path + "\0", // the API wants a double-null-terminated list; the marshaller adds the second terminator
            To = null,
            Flags = Flags,
            ProgressTitle = null,
        };

        int code = -1;
        bool aborted = false;
        var thread = new Thread(() =>
        {
            code = NativeMethods.SHFileOperationW(ref op);
            aborted = op.AnyOperationsAborted;
        })
        {
            IsBackground = true,
            Name = "Tersus.RecycleBin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool finished = thread.Join(_timeout);
        return (code, aborted, !finished);
    }

    private static bool FindBinEntry(string volumeRoot, string sid, string originalPath, long expectedLength, DateTime sinceUtc)
    {
        string dir = Path.Combine(volumeRoot, "$Recycle.Bin", sid);
        DateTime from = sinceUtc - TimeSpan.FromSeconds(10);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (FileInfo info in new DirectoryInfo(dir).EnumerateFiles("$I*"))
                    {
                        if (info.LastWriteTimeUtc < from)
                        {
                            continue;
                        }

                        byte[] data = File.ReadAllBytes(info.FullName);
                        if (!RecycleBinInfoFile.TryParse(data, out string recorded, out long size, out _)
                            || !string.Equals(recorded, originalPath, StringComparison.OrdinalIgnoreCase)
                            || size != expectedLength)
                        {
                            continue;
                        }

                        string dataFile = Path.Combine(dir, RecycleBinInfoFile.DataFileNameFor(info.Name));
                        var dataInfo = new FileInfo(dataFile);
                        if (dataInfo.Exists && dataInfo.Length == expectedLength)
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The shell may still be writing the record; try again shortly.
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static string? CurrentSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
