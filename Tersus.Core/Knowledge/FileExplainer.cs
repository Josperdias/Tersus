namespace Tersus.Core.Knowledge;

public enum RiskLevel
{
    /// <summary>Tersus never offers any action here; losing it can break Windows, a program or personal data.</summary>
    Protected,

    /// <summary>Probably safe for a person to review, but never automatic: look before touching.</summary>
    Caution,

    /// <summary>Low risk by nature (caches, temporary leftovers) - still only reviewed, never silently removed.</summary>
    Low,

    /// <summary>Neutral information.</summary>
    Info,
}

public enum Confidence
{
    High,
    Medium,
    Low,
}

public sealed record Explanation(
    string Title,
    string WhatItIs,
    string Origin,
    RiskLevel Risk,
    string Consequence,
    Confidence Confidence,
    string Recommendation);

/// <summary>
/// A small, reviewable knowledge base (rules below) that explains what a file or folder probably is, where it comes from and what
/// happens if it is removed. It states its own uncertainty: path-pattern matches that are not specific enough are marked Medium/Low,
/// and unknown things are reported as unknown instead of guessed. Explaining never implies permission to delete.
/// </summary>
public static class FileExplainer
{
    private sealed record Ctx(string Original, string P, string Name, string Ext, bool IsDir)
    {
        public bool Has(string fragment) => P.Contains(fragment, StringComparison.Ordinal);

        public bool Under(string prefix) => P.StartsWith(prefix, StringComparison.Ordinal);
    }

    private sealed record Rule(Func<Ctx, bool> Match, Explanation Result);

    private static readonly Rule[] Rules = BuildRules();

    public static Explanation Explain(string path, bool isDirectory)
    {
        string p = ProtectedLocations.Canonical(path);
        string trimmed = p.TrimEnd('\\');
        int slash = trimmed.LastIndexOf('\\');
        string name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        int dot = name.LastIndexOf('.');
        string ext = !isDirectory && dot >= 0 ? name[dot..] : string.Empty;
        var ctx = new Ctx(path, p, name, ext, isDirectory);

        foreach (Rule r in Rules)
        {
            if (r.Match(ctx))
            {
                return r.Result;
            }
        }

        return isDirectory ? FolderFallback(ctx) : FileFallback(ctx);
    }

    private static Explanation FolderFallback(Ctx c)
    {
        if (ProtectedLocations.IsPersonalData(c.Original))
        {
            return new Explanation("Pasta pessoal", "Uma pasta dentro das suas pastas pessoais (Documentos, Imagens, Downloads...).", "Criada por você ou por programas que salvam arquivos aqui.",
                RiskLevel.Protected, "Apagar pode perder trabalho, fotos ou documentos que não existem em outro lugar.", Confidence.Medium,
                "Abra a pasta e revise o conteúdo no Explorador. O Tersus nunca limpa pastas pessoais.");
        }

        if (ProtectedLocations.IsAppData(c.Original))
        {
            return new Explanation("Dados de um programa", "Pasta de configurações, cache ou dados de algum programa (AppData).", "Criada automaticamente por um aplicativo instalado ou portátil.",
                RiskLevel.Caution, "Remover pode apagar configurações, logins ou bancos de dados do programa; ele pode parar de abrir ou perder o histórico.", Confidence.Medium,
                "Use as opções de limpeza do próprio programa. Não apague pastas inteiras do AppData.");
        }

        return new Explanation("Pasta", "Uma pasta comum. O Tersus mede o tamanho dela, mas não consegue saber o propósito só pelo nome.", "Não identificada.",
            RiskLevel.Info, "Depende do conteúdo. O tamanho grande, por si só, não torna uma pasta descartável.", Confidence.Low,
            "Abra no Explorador e veja o que há dentro antes de decidir qualquer coisa.");
    }

    private static Explanation FileFallback(Ctx c)
    {
        FileCategory cat = FileCategories.Of(c.Ext);
        string label = FileCategories.Label(cat);
        if (ProtectedLocations.IsPersonalData(c.Original))
        {
            return new Explanation(label == "Outros" ? "Arquivo pessoal" : label, "Um arquivo dentro das suas pastas pessoais.", "Provavelmente criado, baixado ou salvo por você.",
                RiskLevel.Protected, "Pode ser insubstituível se não houver cópia de segurança.", Confidence.Medium,
                "Revise manualmente no Explorador. O Tersus não oferece limpeza para arquivos pessoais.");
        }

        if (cat == FileCategory.Other)
        {
            return new Explanation("Arquivo não identificado", "O Tersus não reconhece este tipo de arquivo.", "Não identificada.", RiskLevel.Info,
                "Desconhecido: não é possível dizer o que acontece se for removido.", Confidence.Low,
                "Pesquise o nome do arquivo ou descubra qual programa o usa antes de qualquer ação.");
        }

        return new Explanation(label, $"Arquivo do tipo {c.Ext} ({label.ToLowerInvariant()}).", "Criado por algum programa ou por você.", RiskLevel.Info,
            "Depende de onde o arquivo está e de quem o usa. Apagar sem verificar pode causar perda de dados.", Confidence.Medium,
            "Veja o conteúdo e a pasta de origem antes de decidir.");
    }

    private static Rule[] BuildRules()
    {
        static Rule R(Func<Ctx, bool> m, string title, string what, string origin, RiskLevel risk, string consequence, Confidence conf, string rec) =>
            new(m, new Explanation(title, what, origin, risk, consequence, conf, rec));

        static bool Root(Ctx c, string file) => c.P == "\\" + file + "\\";

        return
        [
            R(c => Root(c, "pagefile.sys"), "Arquivo de paginação", "Memória virtual do Windows: parte da RAM é despejada aqui quando falta memória.", "Criado e gerenciado pelo Windows.",
                RiskLevel.Protected, "Sem ele, programas podem fechar por falta de memória e o Windows pode ficar instável.", Confidence.High,
                "Deixe o Windows gerenciar. Para ajustar: Configurações do sistema avançadas > Desempenho > Memória virtual. O Tersus não altera isso."),
            R(c => Root(c, "hiberfil.sys"), "Arquivo de hibernação", "Cópia da memória gravada ao hibernar e usada pela inicialização rápida.", "Criado pelo Windows.",
                RiskLevel.Protected, "Desativar remove hibernação e inicialização rápida; o espaço só volta se você fizer isso conscientemente.", Confidence.High,
                "Se não usa hibernação, o comando oficial 'powercfg /h off' (prompt de administrador) o remove. O Tersus nunca executa isso por você."),
            R(c => Root(c, "swapfile.sys"), "Arquivo de troca de apps modernos", "Área de troca usada por aplicativos da Microsoft Store.", "Criado pelo Windows.",
                RiskLevel.Protected, "Gerenciado pelo sistema; não deve ser removido manualmente.", Confidence.High, "Nenhuma ação necessária."),
            R(c => c.Has(@"\$recycle.bin\"), "Lixeira do Windows", "Arquivos que você apagou e que ainda podem ser restaurados.", "Criada pelo Windows em cada unidade.",
                RiskLevel.Protected, "Esvaziar torna a exclusão definitiva.", Confidence.High,
                "Esvazie pela própria Lixeira se tiver certeza. O Tersus nunca esvazia a Lixeira por você."),
            R(c => c.Has(@"\system volume information\"), "Informações de volume do sistema", "Pontos de restauração e cópias de sombra do Windows.", "Criado pelo Windows.",
                RiskLevel.Protected, "Remover impede restaurar o sistema para um ponto anterior.", Confidence.High,
                "Gerencie em Proteção do Sistema (Propriedades do sistema)."),
            R(c => c.Has(@"\windows\winsxs\"), "Repositório de componentes do Windows (WinSxS)", "Versões de componentes usados para atualizar e reparar o Windows. Muitos arquivos são hardlinks, então o tamanho aparente engana.",
                "Gerenciado pelo Windows Update.", RiskLevel.Protected, "Apagar manualmente quebra atualizações e pode impedir o Windows de iniciar.", Confidence.High,
                "Use a Limpeza de Disco / Configurações > Armazenamento. Nunca apague à mão."),
            R(c => c.Has(@"\windows\installer\"), "Cache do Windows Installer", "Pacotes necessários para reparar, atualizar ou desinstalar programas instalados por MSI.", "Criado por instaladores.",
                RiskLevel.Protected, "Sem eles, alguns programas não podem ser reparados nem desinstalados.", Confidence.High, "Não remova."),
            R(c => c.Has(@"\windows\softwaredistribution\"), "Downloads do Windows Update", "Arquivos baixados para atualizar o Windows.", "Windows Update.",
                RiskLevel.Protected, "Mexer manualmente pode corromper o histórico de atualizações.", Confidence.High,
                "Use Configurações > Sistema > Armazenamento > Arquivos temporários."),
            R(c => c.Under(@"\windows.old\"), "Instalação anterior do Windows", "Cópia do Windows anterior mantida após uma atualização grande, para permitir reverter.", "Criada pelo Windows durante a atualização.",
                RiskLevel.Caution, "Remover impede voltar à versão anterior. O Windows a apaga sozinho após cerca de 10 dias.", Confidence.High,
                "Para liberar o espaço use Configurações > Armazenamento > Arquivos temporários > 'Instalações anteriores do Windows'. O Tersus não faz isso."),
            R(c => c.Under(@"\windows\"), "Arquivos do Windows", "Parte do sistema operacional.", "Windows.", RiskLevel.Protected,
                "Remover ou alterar pode impedir o Windows de iniciar.", Confidence.High, "Não toque. Use as ferramentas oficiais do Windows."),
            R(c => c.Under(@"\program files\") || c.Under(@"\program files (x86)\"), "Programas instalados", "Arquivos de aplicativos instalados no computador.", "Instaladores de programas.",
                RiskLevel.Protected, "Apagar arquivos soltos quebra o programa e deixa sobras no registro.", Confidence.High, "Desinstale pelo Windows: Configurações > Aplicativos."),
            R(c => c.Has(@"\programdata\package cache\"), "Cache de instaladores", "Pacotes usados por instaladores para reparar ou desinstalar programas.", "Visual Studio, runtimes e outros instaladores.",
                RiskLevel.Caution, "Sem eles, reparar ou desinstalar o programa pode exigir baixar o instalador de novo.", Confidence.Medium, "Deixe como está, a menos que saiba o que está fazendo."),
            R(c => c.Under(@"\programdata\"), "Dados compartilhados de programas", "Configurações e dados de programas que valem para todos os usuários.", "Aplicativos instalados.",
                RiskLevel.Caution, "Pode apagar licenças, bancos de dados ou configurações.", Confidence.Medium, "Use as opções do próprio programa."),

            R(c => c.Has(@"\google\chrome\user data"), "Perfil do Google Chrome", "Seus logins, cookies, extensões, favoritos, senhas salvas, histórico e aplicativos web (PWAs).", "Google Chrome.",
                RiskLevel.Protected, "Apagar desconecta contas, perde extensões, favoritos e dados de PWAs. Pode ser irreversível.", Confidence.High,
                "Limpe cache pelo próprio Chrome (Ctrl+Shift+Del). O Tersus nunca limpa perfis de navegadores."),
            R(c => c.Has(@"\microsoft\edge\user data"), "Perfil do Microsoft Edge", "Logins, cookies, extensões, favoritos e dados de aplicativos web.", "Microsoft Edge.",
                RiskLevel.Protected, "Apagar desconecta contas e perde dados do navegador.", Confidence.High, "Use as configurações de privacidade do Edge. O Tersus não limpa navegadores."),
            R(c => c.Has(@"\mozilla\firefox"), "Perfil do Firefox", "Logins, cookies, extensões, favoritos e histórico.", "Mozilla Firefox.",
                RiskLevel.Protected, "Apagar perde o perfil.", Confidence.High, "Use as opções de limpeza do próprio navegador (Firefox)."),
            R(c => ProtectedLocations.IsBrowserProfile(c.Original), "Perfil de navegador", "Dados de um navegador: contas, extensões, favoritos e cache.", "Navegador instalado.",
                RiskLevel.Protected, "Apagar pode desconectar contas e perder dados.", Confidence.Medium, "Limpe pelo próprio navegador."),

            R(c => !c.IsDir && c.Ext == ".blend", "Projeto do Blender", "Arquivo de cena/projeto do Blender com o seu trabalho.", "Criado por você no Blender.",
                RiskLevel.Protected, "Perder este arquivo pode significar perder horas ou dias de trabalho.", Confidence.High, "Mantenha cópias de segurança. O Tersus nunca oferece limpar projetos."),
            R(c => !c.IsDir && (c.Ext == ".blend1" || c.Ext == ".blend2"), "Cópia automática do Blender", "Versão anterior de um projeto, salva automaticamente quando você salva de novo.", "Blender (Salvar versões).",
                RiskLevel.Caution, "Remover tira a possibilidade de voltar à versão anterior do projeto.", Confidence.High,
                "Revise manualmente; só apague se o projeto principal estiver correto e salvo."),
            R(c => c.Has(@"\blender foundation\"), "Configurações do Blender", "Preferências, add-ons e atalhos do Blender.", "Blender.", RiskLevel.Protected,
                "Perder isso reseta suas preferências e add-ons.", Confidence.High, "Faça backup antes de qualquer mudança."),

            R(c => c.Has(@"\onedrive"), "Pasta sincronizada com a nuvem (OneDrive)", "Arquivos sincronizados com a sua conta Microsoft.", "OneDrive.",
                RiskLevel.Caution, "Apagar aqui apaga também na nuvem e nos outros dispositivos (a Lixeira do OneDrive guarda por tempo limitado).", Confidence.High,
                "Para liberar espaço local use 'Liberar espaço' no OneDrive: o arquivo continua na nuvem."),
            R(c => c.Has(@"\steamapps\"), "Jogos da Steam", "Arquivos de jogos instalados pela Steam.", "Steam.", RiskLevel.Caution,
                "Apagar manualmente corrompe o jogo; saves podem estar em outro lugar.", Confidence.High, "Desinstale pela biblioteca da Steam."),
            R(c => !c.IsDir && (c.Ext is ".tmp" or ".temp"), "Arquivo temporário", "Arquivo criado por um programa para uso passageiro.", "Aplicativos em geral.", RiskLevel.Low,
                "Se o programa que o criou ainda está aberto, pode precisar dele. Arquivos antigos costumam ser sobras.", Confidence.Medium,
                "O Tersus só propõe .tmp/.temp com 14+ dias dentro da sua pasta TEMP e sempre envia à Lixeira, nunca exclui de vez."),
            R(c => c.Has(@"\appdata\local\temp\") || c.P.EndsWith(@"\appdata\local\temp\", StringComparison.Ordinal), "Pasta temporária do usuário", "Arquivos temporários criados por programas enquanto trabalham.", "Windows e aplicativos.",
                RiskLevel.Low, "Programas em uso podem depender de arquivos recentes aqui. Arquivos .tmp/.temp com 14+ dias costumam ser sobras.", Confidence.Medium,
                "Use a 'Limpeza' do Tersus: ela só propõe .tmp/.temp antigos e envia à Lixeira."),
            R(c => c.Has(@"\appdata\local\packages\") && c.Has(@"ext4.vhdx"), "Disco virtual do WSL", "Sistema Linux inteiro do Subsistema Windows para Linux, dentro de um arquivo.", "WSL.",
                RiskLevel.Protected, "Apagar destrói a distribuição Linux e todos os arquivos dentro dela.", Confidence.High, "Exporte com 'wsl --export' antes de qualquer mudança."),
            R(c => c.Has(@"\appdata\local\packages\"), "Dados de apps da Microsoft Store", "Configurações e dados de aplicativos instalados pela Store.", "Aplicativos UWP/MSIX.",
                RiskLevel.Caution, "Pode resetar o app e apagar dados locais.", Confidence.Medium, "Use Configurações > Aplicativos > Opções avançadas > Redefinir, se necessário."),

            R(c => c.IsDir && c.Name == "node_modules", "node_modules", "Dependências baixadas de um projeto JavaScript/Node.", "npm / yarn / pnpm.",
                RiskLevel.Low, "Podem ser baixadas de novo com 'npm install', mas isso exige internet e pode obter versões diferentes.", Confidence.High,
                "Se o projeto está parado, apague pela linha de comando do projeto. O Tersus não faz isso."),
            R(c => c.IsDir && c.Name == ".git", "Repositório Git", "Todo o histórico de versões do projeto.", "Git.", RiskLevel.Protected,
                "Apagar perde o histórico e as branches que não estejam em outro servidor.", Confidence.High, "Não remova sem confirmar que existe cópia remota."),
            R(c => c.IsDir && (c.Name == "__pycache__" || c.Name == ".pytest_cache" || c.Name == ".mypy_cache"), "Cache do Python", "Arquivos gerados automaticamente para acelerar o Python.", "Python.",
                RiskLevel.Low, "São recriados automaticamente.", Confidence.High, "Seguro de remover pelo seu fluxo de desenvolvimento."),
            R(c => c.IsDir && (c.Name == "bin" || c.Name == "obj") && c.Has(@"\"), "Saída de compilação (bin/obj)", "Resultados de compilação de um projeto .NET e similares.", "Compiladores.",
                RiskLevel.Caution, "Podem ser regenerados compilando de novo, mas nem toda pasta 'bin' é descartável.", Confidence.Low,
                "Confirme que é de um projeto de código antes de qualquer ação."),
            R(c => c.Has(@"\.nuget\packages\") || c.Has(@"\.gradle\caches\") || c.Has(@"\.m2\repository\"), "Cache de pacotes de desenvolvimento", "Bibliotecas baixadas por ferramentas de build.", "NuGet/Gradle/Maven.",
                RiskLevel.Low, "Serão baixadas de novo quando necessário (exige internet).", Confidence.High, "Limpe pelo comando da própria ferramenta."),

            R(c => !c.IsDir && (c.Ext is ".vhd" or ".vhdx" or ".vmdk" or ".vdi" or ".qcow2" or ".ova"), "Disco de máquina virtual", "Um disco virtual: pode conter um sistema operacional inteiro e seus arquivos.", "Hyper-V, VirtualBox, VMware, WSL, Docker...",
                RiskLevel.Protected, "Apagar destrói a máquina virtual e tudo que há nela.", Confidence.High, "Gerencie pelo programa de virtualização."),
            R(c => !c.IsDir && (c.Ext is ".iso" or ".img"), "Imagem de disco", "Cópia de um disco/DVD, normalmente de instaladores ou sistemas.", "Baixada ou criada por você.", RiskLevel.Caution,
                "Se for a única cópia de um instalador, terá de baixar de novo.", Confidence.Medium, "Costuma ser seguro apagar após instalar, se puder baixar de novo."),
            R(c => !c.IsDir && (c.Ext is ".crdownload" or ".part" or ".partial"), "Download incompleto", "Arquivo de um download que não terminou.", "Navegador/gerenciador de downloads.", RiskLevel.Low,
                "Se o download ainda está em andamento, apagar o interrompe.", Confidence.Medium, "Verifique se não há downloads ativos."),
            R(c => !c.IsDir && (c.Ext is ".dmp" or ".mdmp"), "Despejo de memória (crash dump)", "Registro do estado de um programa ou do Windows quando travou.", "Windows Error Reporting.", RiskLevel.Low,
                "Só é útil para diagnosticar falhas.", Confidence.High, "Pode ser removido pela Limpeza de Disco do Windows."),
            R(c => !c.IsDir && (c.Ext is ".pst" or ".ost"), "Arquivo de e-mail do Outlook", "Caixa de correio local (mensagens, calendário, contatos).", "Microsoft Outlook.", RiskLevel.Protected,
                "Um .pst pode ser a única cópia dos seus e-mails.", Confidence.High, "Gerencie pelo Outlook."),
            R(c => !c.IsDir && c.Ext is ".bak", "Cópia de segurança", "Cópia de um arquivo ou banco de dados feita por um programa.", "Programas e bancos de dados.", RiskLevel.Caution,
                "Pode ser a sua única proteção contra perda de dados.", Confidence.Medium, "Confirme que há outra cópia antes de apagar."),
            R(c => !c.IsDir && c.Ext is ".log" or ".etl", "Registro (log)", "Arquivo de registro de eventos de um programa.", "Aplicativos e Windows.", RiskLevel.Low,
                "Só é útil para diagnóstico; programas costumam recriá-lo.", Confidence.Medium, "Pode crescer muito; revise a configuração do programa que o gera."),
            R(c => !c.IsDir && c.Ext is ".sqlite" or ".sqlite3" or ".db", "Banco de dados local", "Banco de dados usado por um programa (histórico, configurações, dados).", "Aplicativos.", RiskLevel.Caution,
                "Apagar pode resetar o programa e perder dados.", Confidence.Medium, "Use as opções do próprio programa."),
            R(c => !c.IsDir && c.Ext is ".exe" or ".msi" or ".msix", "Programa ou instalador", "Executável ou pacote de instalação.", "Baixado ou instalado.", RiskLevel.Caution,
                "Instaladores baixados podem ser baixados de novo; executáveis de programas instalados não devem ser apagados.", Confidence.Medium,
                "Se está em Downloads e o programa já foi instalado, costuma ser descartável, mas confira."),
        ];
    }
}
