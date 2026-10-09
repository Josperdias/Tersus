# Segurança do Tersus: modelo de ameaças, travas e verificações

> Compilar com sucesso **não** prova segurança. Este documento lista cada risco, a trava que o impede e **onde a verificação está**
> (teste automatizado, auditoria estática ou etapa da CI em Windows).

## 1. Princípio

O Tersus tem **uma única ação destrutiva**: mover para a **Lixeira do Windows** arquivos `.tmp` e `.temp` **antigos** da pasta **TEMP do usuário atual**,
depois de uma simulação e de uma confirmação explícita. Tudo o mais (análise, duplicados, aplicativos, histórico, recomendações) é **somente leitura**.
Se algo parecer duvidoso, o Tersus desativa **só** a ação de limpeza e mantém a análise funcionando.

### O que o Tersus nunca faz

Perfis, senhas, cookies, extensões e PWAs de navegadores · Documentos, Imagens, Músicas, Vídeos, Área de Trabalho, Downloads, OneDrive · projetos do Blender ·
programas instalados e seus dados · registro do Windows · arquivos do Windows, `pagefile.sys`, `hiberfil.sys`, `Windows.old` · exclusão permanente ·
esvaziar a Lixeira · apagar ou mover duplicados · desinstalar programas · mexer em serviços, tarefas agendadas ou inicialização do Windows ·
usar a rede · enviar telemetria · executar como administrador.

## 2. Regras de elegibilidade (`Tersus.Core/Policy/FilePolicy.cs`)

Um arquivo só é candidato se **todas** as condições valerem. Os limites abaixo são **pisos rígidos**: a configuração só pode ficar mais estrita, nunca mais frouxa.

| # | Condição | Motivo da recusa quando falha |
|---|---|---|
| 1 | caminho em formato aceito (sem `\\?\`, UNC, dispositivos, fluxos alternativos `:`, nomes reservados, ponto/espaço final, ≤ 259 caracteres) | `InvalidPath` |
| 2 | extensão exatamente `.tmp` ou `.temp` | `WrongExtension` |
| 3 | existe e não é pasta (nem pelo atributo) | `NotFound`, `IsDirectory` |
| 4 | não é link simbólico, junção ou ponto de reanálise | `ReparsePoint` |
| 5 | data de criação **e** de modificação válidas e com **≥ 14 dias** | `TimestampInvalid`, `TooNew` |
| 6 | tamanho **≤ 256 MiB** | `TooLarge` |
| 7 | não é arquivo só-na-nuvem (OneDrive e similares) | `CloudPlaceholder` |
| 8 | sem atributo *Sistema* | `SystemFile` |
| 9 | sem atributo *Somente leitura* | `ReadOnly` |
| 10 | fatos lidos por **identificador aberto** (caminho final, identidade, nº de links) | `ProbeFailed` |
| 11 | caminho final == caminho pedido (sem nome 8.3, link ou junção no meio) | `IndirectPath` |
| 12 | caminho final **dentro** da pasta TEMP canônica | `OutsideTemp` |
| 13 | exatamente **1** nome no disco (sem hardlink) | `HardLinked` |
| 14 | não está em uso por outro programa | `InUse` |
| 15 | não mudou desde a análise (tamanho, datas, identidade) | `ChangedSinceAnalysis` |

A própria pasta TEMP só é aceita se, depois de resolvida, estiver **dentro do perfil do usuário**, com profundidade ≥ 3, e **não** for igual, interna nem ancestral
de pastas protegidas (Documentos, Área de Trabalho, Imagens, Músicas, Vídeos, Downloads, OneDrive, Windows, Arquivos de Programas, ProgramData). Caso contrário a limpeza é desativada (`UnsafeTempRoot`).

## 3. Modelo de ameaças

| Ameaça | Trava | Verificação |
|---|---|---|
| Caminho fora do TEMP (`..`, UNC, `\\?\`, ADS, nome reservado, nome longo) | `PathGuard` + condição 12; só texto, sem I/O | `PathGuardTests`, `FilePolicyTests`, `PolicyFuzzTests` (100 mil caminhos/fatos aleatórios; nenhuma combinação proibida é aprovada) |
| Link simbólico / junção / ponto de reanálise | condição 4 e 11; o scanner nunca segue links | `WindowsFileSystemTests`, `WindowsMiscTests`, smoke da CI (junção e symlink para fora da pasta de teste) |
| Hardlink (o mesmo dado em outro lugar) | condição 13 (contagem lida do identificador aberto) | testes Windows de hardlink; smoke da CI |
| Nome curto 8.3 / caminho alternativo | condição 11 (caminho final do identificador) | `WindowsFileSystemTests` |
| **TOCTOU**: arquivo trocado entre a análise e a ação | cada arquivo é **aberto e mantido** (sem escritores) enquanto é revalidado e comparado ao instantâneo da análise; o plano tem impressão digital SHA-256 | `WindowsCleanupTests` (troca/forja de candidato), `CleanupExecutorTests` |
| Arquivo em uso | a abertura exclusiva falha → recusado | testes de trava; smoke da CI (arquivo com handle exclusivo) |
| Lixeira desativada, “apagar de vez” ou arquivo maior que a capacidade | verificação prévia da configuração da Lixeira e arquivo de autoteste; nada é movido se não estiver pronta | `RecycleBinParsingTests`, `WindowsCleanupTests` |
| Arquivo some **sem prova** de ter ido para a Lixeira | prova por registro `$I`/`$R`; sem prova → lote **interrompido** e usuário avisado | `WindowsCleanupTests`; verificação independente da CI lê os `$I`/`$R` por fora do programa |
| Confirmação acidental | janela própria: **Cancelar é o padrão** (Enter/Esc), botão de confirmar desabilitado até marcar a caixa, texto diz que é Lixeira e que não libera espaço | verificado na CI (`smoke-test`) |
| Confirmar um plano e executar outro | `ExecutionConfirmation` amarra id + impressão digital do plano; qualquer divergência → nada acontece | `CleanupExecutorTests` |
| Lista antiga reaproveitada | mudar a seleção descarta a simulação; após executar, a lista é apagada | smoke da CI |
| Registro (log) não pode ser gravado | a limpeza só pede confirmação depois de provar que o registro é gravável | revisão de código + teste de unidade |
| Duas instâncias limpando ao mesmo tempo | exclusão mútua por instância única | revisão de código |
| Ampliação futura da superfície destrutiva | **auditoria estática** falha a compilação se aparecer qualquer API de exclusão/gravação/movimentação/registro/processo/rede fora de arquivos revisados | `StaticAuditTests` (com testes de mutação do próprio auditor) |
| Rede, telemetria, atualização em segundo plano | nenhuma API de rede no código (auditoria); nenhum pacote NuGet; verificação em tempo de execução na CI | auditoria; `Invoke-SmokeTest.ps1` |
| Persistência (inicialização automática, serviço, tarefa agendada) | nada disso existe no código; instalador por usuário | `Test-Installer.ps1` compara antes/depois |
| Arquivo de histórico corrompido | movido para quarentena (nunca apagado), recomeça vazio; gravação atômica | `HistoryStoreTests` |
| Cadeia de suprimentos | zero pacotes NuGet; só SDK/runtime oficiais; ações do GitHub fixadas por versão principal | `No_project_uses_third_party_packages_or_trimming` |

## 4. Uma única porta para apagar

- `AppDataFiles` é o **único** lugar do produto autorizado a apagar, mover ou gravar arquivos, e só na pasta de dados do próprio Tersus (`%LOCALAPPDATA%\Tersus`). Ele rejeita qualquer caminho fora dela.
- `ShellRecycler` é o **único** lugar que chama a API de envio à Lixeira (`SHFileOperationW` com *allow undo*); só o `CleanupExecutor` pode chamá-lo.
- `ShellLauncher` é o **único** lugar que inicia outro programa (Explorador, Configurações, Lixeira), com argumentos fixos.
- Funções nativas permitidas: `CreateFileW`, `GetFileInformationByHandle(Ex)`, `GetFinalPathNameByHandleW`, `GetCompressedFileSizeW`, `GetVolumeNameForVolumeMountPointW`, `SHFileOperationW`, `SHQueryRecycleBinW`. Não existe importação para esvaziar a Lixeira.

## 5. O que **não** foi validado (ver também `PENDENCIAS.md`)

- Máquina Windows **física**: toda a validação automatizada roda em máquinas virtuais descartáveis do GitHub Actions. Nada aqui deve ser descrito como “testado em máquina física”.
- Windows 10 (a CI usa o runner `windows-latest`, Windows Server 2025); reinicialização após a desinstalação; leitor de tela; tema real de alto contraste; escalas de DPI diferentes; Windows em outros idiomas.
- Assinatura de código e reputação no SmartScreen: o executável e o instalador **não são assinados** e **não têm reputação**.
- Pastas de rede, discos externos e arquivos só-na-nuvem reais (apenas simulados por atributos).
- Antivírus de terceiros reagindo a um executável autossuficiente de arquivo único não assinado.

## 6. Decisão antes de usar no seu computador

O dono pediu que a limpeza real só seja autorizada depois de testes em ambiente Windows isolado. Os testes estão descritos em `TESTES.md` e os resultados no relatório de build.
Recomendação: instalar primeiro em uma máquina virtual, rodar uma limpeza com poucos arquivos, conferir a Lixeira e o registro em `%LOCALAPPDATA%\Tersus\logs`, e só então usar no computador de trabalho.
