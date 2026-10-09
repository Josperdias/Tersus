using Tersus.Core.Apps;

namespace Tersus.Core.Advice;

public enum AdviceSeverity
{
    Info,
    Attention,
    Important,
}

/// <summary>One piece of read-only guidance. Tersus never changes any of these settings itself.</summary>
public sealed record Recommendation(
    string Id,
    string Title,
    string Detail,
    string? Evidence,
    string? HowTo,
    string? Caution,
    AdviceSeverity Severity);

public sealed record AdvisorInput
{
    public long? VolumeTotalBytes { get; init; }

    public long? VolumeFreeBytes { get; init; }

    public string? VolumeName { get; init; }

    public long? HibernationFileBytes { get; init; }

    public long? PageFileBytes { get; init; }

    public bool WindowsOldExists { get; init; }

    public long? RecycleBinBytes { get; init; }

    public long? DownloadsBytes { get; init; }

    public long? CloudOnlyBytes { get; init; }

    public IReadOnlyList<InstalledApp> Apps { get; init; } = [];

    public IReadOnlyList<string> BrowsersDetected { get; init; } = [];

    public bool BlenderDetected { get; init; }
}

/// <summary>
/// Turns facts about the PC into plain-language guidance: what uses space, why it exists, what would happen if it were changed and where
/// the official, reversible way to change it is. Pure function; the gathering of facts is separate and read-only.
/// </summary>
public static class SystemAdvisor
{
    public static IReadOnlyList<Recommendation> Build(AdvisorInput i)
    {
        ArgumentNullException.ThrowIfNull(i);
        var list = new List<Recommendation>();
        string vol = i.VolumeName ?? "a unidade";

        if (i.VolumeTotalBytes is > 0 && i.VolumeFreeBytes is >= 0)
        {
            double free = (double)i.VolumeFreeBytes.Value / i.VolumeTotalBytes.Value;
            if (free < 0.10)
            {
                list.Add(new Recommendation("low-space", $"Pouco espaço livre em {vol}",
                    "Com menos de 10% livre, o Windows e os programas podem ficar lentos e falhar ao atualizar ou salvar.",
                    $"Livre: {SizeText.Format(i.VolumeFreeBytes.Value)} de {SizeText.Format(i.VolumeTotalBytes.Value)} ({free:P0}).",
                    "Comece pelos maiores itens da aba Visão geral e pelos arquivos grandes; depois revise Downloads e a Lixeira.",
                    null, AdviceSeverity.Important));
            }
            else if (free < 0.20)
            {
                list.Add(new Recommendation("low-space", $"Espaço livre em {vol} está ficando baixo",
                    "Abaixo de 20% livre, vale começar a revisar o que ocupa espaço.",
                    $"Livre: {SizeText.Format(i.VolumeFreeBytes.Value)} de {SizeText.Format(i.VolumeTotalBytes.Value)} ({free:P0}).",
                    null, null, AdviceSeverity.Attention));
            }
        }

        if (i.RecycleBinBytes is > 50L * 1024 * 1024)
        {
            list.Add(new Recommendation("recycle-bin", "A Lixeira está ocupando espaço",
                "Arquivos na Lixeira continuam ocupando o disco até ela ser esvaziada. Enviar algo à Lixeira (inclusive pelo Tersus) não libera espaço por si só.",
                $"Tamanho atual: {SizeText.Format(i.RecycleBinBytes.Value)}.",
                "Abra a Lixeira, confira o conteúdo e esvazie quando tiver certeza.",
                "O Tersus nunca esvazia a Lixeira por você: esvaziar torna a exclusão definitiva.", AdviceSeverity.Attention));
        }

        if (i.HibernationFileBytes is > 0)
        {
            list.Add(new Recommendation("hibernation", "Arquivo de hibernação (hiberfil.sys)",
                "O Windows reserva este espaço para hibernar e para a inicialização rápida. Costuma ocupar de 40% a 75% da RAM.",
                $"Tamanho: {SizeText.Format(i.HibernationFileBytes.Value)}.",
                "Se você não usa hibernação nem inicialização rápida, abra o Prompt de Comando como administrador e execute: powercfg /h off  (para reverter: powercfg /h on).",
                "Isso desativa hibernação e inicialização rápida. O Tersus não altera esta configuração.", AdviceSeverity.Info));
        }

        if (i.PageFileBytes is > 0)
        {
            list.Add(new Recommendation("pagefile", "Arquivo de paginação (pagefile.sys)",
                "Memória virtual usada pelo Windows quando a RAM não basta.",
                $"Tamanho: {SizeText.Format(i.PageFileBytes.Value)}.",
                "Prefira deixar o Windows gerenciar. Para ver: Configurações avançadas do sistema > Desempenho > Avançado > Memória virtual.",
                "Reduzir demais pode causar travamentos e fechamento de programas.", AdviceSeverity.Info));
        }

        if (i.WindowsOldExists)
        {
            list.Add(new Recommendation("windows-old", "Existe uma instalação anterior do Windows (Windows.old)",
                "Fica guardada após uma atualização grande para você poder voltar à versão anterior. O Windows a remove sozinho depois de algumas semanas.",
                null,
                "Configurações > Sistema > Armazenamento > Arquivos temporários > 'Instalações anteriores do Windows'.",
                "Depois de remover não é possível reverter a atualização.", AdviceSeverity.Info));
        }

        if (i.DownloadsBytes is > 2L * 1024 * 1024 * 1024)
        {
            list.Add(new Recommendation("downloads", "A pasta Downloads está grande",
                "Instaladores, ISOs e arquivos antigos costumam se acumular ali.",
                $"Tamanho: {SizeText.Format(i.DownloadsBytes.Value)}.",
                "Abra Downloads, ordene por tamanho e revise com calma. O Tersus não limpa Downloads.",
                "Pode haver documentos importantes misturados.", AdviceSeverity.Attention));
        }

        var big = i.Apps.Where(a => a.EstimatedSizeBytes is > 1L * 1024 * 1024 * 1024).OrderByDescending(a => a.EstimatedSizeBytes).Take(5).ToList();
        if (big.Count > 0)
        {
            list.Add(new Recommendation("big-apps", "Programas grandes instalados",
                "Se algum destes você não usa mais, desinstalá-lo libera bastante espaço.",
                string.Join("; ", big.Select(a => $"{a.Name} ({SizeText.Format(a.EstimatedSizeBytes!.Value)})")),
                "Configurações > Aplicativos > Aplicativos instalados.",
                "O tamanho é o que o instalador informou e pode estar impreciso. O Tersus não sabe se você usa o programa e não o desinstala.", AdviceSeverity.Info));
        }

        if (i.BrowsersDetected.Count > 0)
        {
            list.Add(new Recommendation("browsers", "Cache de navegadores",
                $"Detectado: {string.Join(", ", i.BrowsersDetected)}. O cache pode ocupar gigabytes, mas fica junto de dados valiosos (logins, extensões, favoritos, apps web).",
                null,
                "Limpe pelo próprio navegador: Ctrl+Shift+Del, marcando só 'Imagens e arquivos em cache'.",
                "O Tersus nunca mexe em perfis de navegadores.", AdviceSeverity.Info));
        }

        if (i.BlenderDetected)
        {
            list.Add(new Recommendation("blender", "Blender detectado",
                "O Blender cria cópias automáticas (.blend1) e arquivos de recuperação. Seus projetos (.blend), preferências e add-ons ficam em pastas protegidas pelo Tersus.",
                null,
                "Gerencie versões em Preferências > Salvar e carregar. Mantenha cópias de segurança dos projetos.",
                "O Tersus nunca oferece limpar projetos nem configurações do Blender.", AdviceSeverity.Info));
        }

        if (i.CloudOnlyBytes is > 0)
        {
            list.Add(new Recommendation("cloud", "Arquivos somente na nuvem",
                "Arquivos do OneDrive marcados como 'somente online' aparecem com tamanho no Explorador, mas não ocupam espaço local.",
                $"{SizeText.Format(i.CloudOnlyBytes.Value)} (tamanho lógico) fora do cálculo de espaço local.",
                "Para liberar espaço de arquivos já sincronizados use 'Liberar espaço' no menu do OneDrive.",
                null, AdviceSeverity.Info));
        }

        list.Add(new Recommendation("storage-sense", "Sensor de Armazenamento do Windows",
            "Ferramenta oficial que limpa temporários e esvazia a Lixeira automaticamente nos intervalos que você escolher.",
            null,
            "Configurações > Sistema > Armazenamento > Sensor de Armazenamento.",
            "Ao ativar a limpeza automática da Lixeira, a exclusão passa a ser definitiva após o prazo escolhido. Essa decisão é sua e do Windows, não do Tersus.", AdviceSeverity.Info));

        list.Add(new Recommendation("appdata", "Pasta AppData",
            "Guarda configurações, caches e dados dos seus programas. Pode ser grande, mas apagar pastas inteiras costuma quebrar programas.",
            null,
            "Use as opções de limpeza de cada programa. Para ver o que pesa, veja a aba Visão geral.",
            "Nunca apague AppData por inteiro. O Tersus não oferece limpeza dessa área.", AdviceSeverity.Info));

        return list;
    }
}
