using System.Globalization;
using Tersus.Core.Policy;

namespace Tersus.App.Services;

/// <summary>
/// Plain-language statements about what Tersus does and never does. There is exactly one copy, used by the cleanup page, the
/// confirmation window and the "Segurança" page, so they can never contradict each other (or the policy actually enforced).
/// </summary>
public static class SafetyTexts
{
    public const string OnlyAction =
        "A única ação que o Tersus pode fazer é mover para a Lixeira do Windows arquivos temporários (.tmp e .temp) antigos da pasta TEMP do seu usuário.";

    public const string RecycleBinNote =
        "Mover para a Lixeira NÃO libera espaço: os arquivos continuam ocupando disco até você esvaziar a Lixeira. O Tersus nunca esvazia a Lixeira por você.";

    public const string Revalidation =
        "Cada arquivo é verificado de novo imediatamente antes de ser movido. Se algo mudou, estiver em uso ou parecer diferente, ele é ignorado.";

    public const string Simulation =
        "A simulação mostra exatamente o que seria movido e o que foi recusado. Ela não altera nada no seu computador.";

    public const string SizesAreLogical =
        "Os tamanhos mostrados são LÓGICOS (a soma do tamanho dos arquivos). Compressão, hardlinks e arquivos esparsos podem fazer o uso real do disco ser diferente.";

    public const string UnsignedNote =
        "Este programa ainda não é assinado digitalmente e não tem reputação no Windows SmartScreen: o Windows pode pedir confirmação ao abrir o instalador.";

    public static IReadOnlyList<string> NeverList { get; } =
    [
        "Perfis, senhas, cookies, extensões e aplicativos web (PWAs) do Chrome, Edge, Firefox e outros navegadores.",
        "Documentos, Imagens, Músicas, Vídeos, Área de Trabalho, Downloads e pastas sincronizadas (OneDrive).",
        "Projetos e arquivos do Blender e de qualquer outro programa.",
        "Programas instalados e os dados deles (Arquivos de Programas, AppData, ProgramData).",
        "Arquivos do Windows, registro do Windows, pagefile.sys, hiberfil.sys e Windows.old.",
        "Arquivos recentes: só entram arquivos criados E modificados há 14 dias ou mais.",
        "Arquivos grandes (acima de 256 MiB), links, atalhos, arquivos em uso, somente leitura ou só na nuvem.",
        "Duplicados: o Tersus só mostra, nunca apaga nem move.",
        "Exclusão permanente, esvaziar a Lixeira, mexer em serviços, tarefas agendadas ou inicialização do Windows.",
    ];

    public static IReadOnlyList<string> AlwaysList { get; } =
    [
        "Funciona 100% offline: não envia dados, não tem telemetria, não atualiza sozinho.",
        "Guarda localmente só o histórico (até 24 resumos, sem nomes de arquivos) e o registro das limpezas, em %LOCALAPPDATA%\\Tersus.",
        "Pede confirmação explícita, ligada à lista exata de arquivos, antes de mover qualquer coisa.",
        "Na dúvida, desativa a ação de limpeza e mantém só a análise.",
    ];

    public static string RulesLine(FilePolicy policy) =>
        string.Create(CultureInfo.CurrentCulture,
            $"Regras desta limpeza: só arquivos .tmp/.temp dentro de {policy.TempRoot}, criados e modificados há {Math.Round(policy.MinAge.TotalDays)} dias ou mais, com até {Tersus.Core.SizeText.Format(policy.MaxFileBytes)}. Tudo vai para a Lixeira.");
}
