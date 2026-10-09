namespace Tersus.Core.Policy;

/// <summary>Why the policy (or a later safety check) refused to touch a file. Refusal is always the safe default.</summary>
public enum RefusalReason
{
    None = 0,
    InvalidPath,
    OutsideTemp,
    WrongExtension,
    NotFound,
    IsDirectory,
    ReparsePoint,
    IndirectPath,
    HardLinked,
    SystemFile,
    CloudPlaceholder,
    ReadOnly,
    TooNew,
    TimestampInvalid,
    TooLarge,
    InUse,
    AccessDenied,
    ChangedSinceAnalysis,
    ProbeFailed,
    UnsafeTempRoot,
    ExceedsRecycleBinCapacity,
    RecycleBinUnavailable,
    NotConfirmed,
    Cancelled,
    NotAttempted,
}

public readonly record struct PolicyDecision(bool Eligible, RefusalReason Reason, string Message, string? NormalizedPath = null)
{
    public static PolicyDecision Allow(string normalizedPath) =>
        new(true, RefusalReason.None, "Elegível: arquivo temporário antigo na pasta TEMP do seu usuário.", normalizedPath);

    public static PolicyDecision Refuse(RefusalReason reason, string? message = null) =>
        new(false, reason, message ?? RefusalText.Describe(reason));
}

public static class RefusalText
{
    /// <summary>Short, user-facing explanation (pt-BR) for each refusal reason.</summary>
    public static string Describe(RefusalReason reason) => reason switch
    {
        RefusalReason.None => string.Empty,
        RefusalReason.InvalidPath => "Caminho inválido ou em formato não aceito.",
        RefusalReason.OutsideTemp => "Fora da pasta TEMP do seu usuário; o Tersus só limpa ali.",
        RefusalReason.WrongExtension => "Extensão diferente de .tmp/.temp.",
        RefusalReason.NotFound => "O arquivo não existe mais.",
        RefusalReason.IsDirectory => "É uma pasta; o Tersus nunca limpa pastas.",
        RefusalReason.ReparsePoint => "É um link, junção ou espaço reservado; o Tersus nunca segue links.",
        RefusalReason.IndirectPath => "O caminho passa por um link ou nome alternativo; o arquivo real está em outro lugar.",
        RefusalReason.HardLinked => "Tem mais de um nome no disco (hardlink); pode ser o mesmo dado de outro arquivo.",
        RefusalReason.SystemFile => "Marcado como arquivo de sistema.",
        RefusalReason.CloudPlaceholder => "É um arquivo somente na nuvem (OneDrive ou similar).",
        RefusalReason.ReadOnly => "Marcado como somente leitura.",
        RefusalReason.TooNew => "Criado ou modificado recentemente.",
        RefusalReason.TimestampInvalid => "Data do arquivo ausente ou no futuro; não dá para julgar a idade.",
        RefusalReason.TooLarge => "Maior que o limite de segurança por arquivo.",
        RefusalReason.InUse => "Em uso por outro programa.",
        RefusalReason.AccessDenied => "Sem permissão de acesso.",
        RefusalReason.ChangedSinceAnalysis => "Mudou desde a análise (tamanho, data ou identidade); não foi tocado.",
        RefusalReason.ProbeFailed => "Não foi possível verificar o arquivo com segurança.",
        RefusalReason.UnsafeTempRoot => "A pasta TEMP está em um local não suportado; a limpeza está desativada.",
        RefusalReason.ExceedsRecycleBinCapacity => "Maior que a capacidade da Lixeira; o Windows o apagaria de vez.",
        RefusalReason.RecycleBinUnavailable => "A Lixeira está indisponível ou desativada; nada foi movido.",
        RefusalReason.NotConfirmed => "Sem confirmação explícita do usuário.",
        RefusalReason.Cancelled => "Cancelado antes de ser processado.",
        RefusalReason.NotAttempted => "Não processado porque a operação foi interrompida.",
        _ => "Recusado por segurança.",
    };
}
