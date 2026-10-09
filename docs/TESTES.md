# Testes: o que existe, como rodar e o que cada parte prova

## Rodar localmente

```powershell
dotnet run --project Tersus.Tests/Tersus.Tests.csproj -c Release            # tudo que roda no seu sistema
dotnet run --project Tersus.Tests/Tersus.Tests.csproj -c Release -- --filter Policy
dotnet build Tersus.sln -c Release -warnaserror                               # avisos viram erros
```

O executor de testes é próprio (sem pacotes NuGet): `--filter TEXTO`, `--junit ARQUIVO.xml`, `--markdown ARQUIVO.md`. Os testes que exigem Windows são **ignorados** (e contados como tal) em outros sistemas.

## Camadas

| Camada | Onde | O que prova |
|---|---|---|
| Política e caminhos | `PathGuardTests`, `FilePolicyTests`, `TempRootResolverTests` | cada recusa da tabela de `SEGURANCA.md` (extensão, idade 14 dias, 256 MiB, links, atributos, fora do TEMP, caminhos exóticos) |
| **Fuzz** da política | `PolicyFuzzTests` | 100 mil combinações aleatórias de caminho/fatos: nada proibido é aprovado (este teste já achou duas brechas reais, corrigidas) |
| Planejamento e execução | `CleanupPlannerTests`, `CleanupExecutorTests` | simulação não altera nada; confirmação amarrada ao plano; revalidação sob trava; cancelamento; interrupção do lote sem prova de chegada à Lixeira; nunca esvazia a Lixeira |
| Scanner e treemap | `StorageScannerTests`, `TreemapTests`, `CandidateScannerTests` | contagens exatas, links/laços não seguidos, memória limitada, cancelamento parcial, geometria do treemap |
| Duplicados, histórico, apps, recomendações, explicador | `DuplicateAnalyzerTests`, `HistoryStoreTests`, `AppsAndAdvisorTests`, `ExplainerTests` | comportamento e rótulos de incerteza |
| **Auditoria estática** | `StaticAuditTests` | o código-fonte não contém APIs de exclusão/gravação/movimentação/registro/processo/rede/código dinâmico fora dos poucos arquivos revisados; lista exata de funções nativas; constantes da política não foram afrouxadas; nenhum pacote NuGet. Inclui **testes do próprio auditor** (mutação) |
| **Integração em Windows real** | `WindowsFileSystemTests`, `WindowsCleanupTests`, `WindowsMiscTests` | junção, link simbólico, hardlink, nome 8.3, arquivo travado, ACL negando acesso, troca de arquivo entre simulação e execução (TOCTOU), candidato forjado, **Lixeira real** com prova pelos registros `$I`/`$R` |

## Teste de fumaça da interface (`tools/ci/Invoke-SmokeTest.ps1`)

Roda o **EXE publicado** e, depois, o **EXE instalado**, numa máquina Windows virtual limpa:

1. O script cria, na pasta TEMP real da máquina de teste, uma pasta de teste com:
   - **6 arquivos que devem ser movidos** (`.tmp`/`.temp` de 15 a 400 dias, pequenos, comuns, inclusive em subpasta e um com nome **Unicode**: `acentuação-ü-日本語.tmp`);
   - **iscas que não podem ser tocadas**: arquivo recente, criado há 30 dias mas modificado agora, criado agora, de 13 dias, `.txt`, `.jpg`, `.docx` de 3 anos, `.tmp.exe`, sem extensão, `.tmp` de 257 MiB, somente leitura, de sistema, par de hardlinks, um `.tmp` antigo numa **pasta sem permissão de leitura**, um symlink `.tmp` e uma **junção** que apontam para arquivos fora da pasta, e uma **pasta** chamada `vazia.tmp`;
   - **um arquivo em uso** por outro programa (handle exclusivo): ele aparece na busca (a simulação só lê, nunca segura arquivos), mas na execução a trava por arquivo o recusa (`InUse`) e ele fica onde está;
   - um item que **já estava na Lixeira** antes da execução: ele precisa continuar lá, provando que o programa não esvazia a Lixeira.
2. Executa `Tersus.exe --smoke-test ...`: abre a janela real, percorre as 9 telas tirando capturas, analisa uma pasta, faz uma busca de limpeza (esperando **exatamente 7** candidatos: os 6 movíveis + o arquivo em uso), simula (conferindo que nada mudou), confere que os botões **reais** “Simular” e “Mover para a Lixeira…” estão visíveis e habilitados no estágio certo, verifica as propriedades de segurança da janela de confirmação (Cancelar é o padrão; confirmar começa desabilitado; a caixa de confirmação e os dois botões aparecem inteiros, sem depender de rolagem), **cancela** (nada é movido) e depois **confirma** (só funciona dentro da pasta de teste). Depois repete a busca (só deve restar o que foi ignorado), analisa duas vezes (histórico com comparação), **cancela** a análise da unidade inteira (tempo de resposta; análise parcial não entra no histórico) e analisa uma árvore grande (`C:\Windows`) duas vezes, medindo tempo, memória de pico, memória retida e o maior intervalo sem resposta da interface. Por fim testa o zoom e a paleta de alto contraste.
3. Por fora do programa, o script confere: os 6 arquivos saíram da pasta e estão na **Lixeira** (lê os registros `$I`/`$R` do Windows), **cada isca está idêntica** (tamanho, datas, atributos), os alvos dos links continuam intactos, a Lixeira **não foi esvaziada**, o programa **não abriu conexões de rede** (amostragem do processo), **não tocou na pasta de dados real** do usuário e o registro da limpeza foi gravado (inclusive o arquivo em uso ignorado).
4. O relatório do programa precisa dizer **APROVADO**, com **zero** erros de *binding* e **zero** exceções não tratadas.

## Teste do instalador (`tools/ci/Test-Installer.ps1`)

Instalação silenciosa → confere pasta do usuário, atalho, registro de desinstalação em **HKCU** (nada em HKLM), exe idêntico byte a byte ao publicado,
**nenhuma** entrada de inicialização automática, serviço, tarefa agendada ou item da pasta Inicializar (comparação antes/depois) → roda o **programa instalado** pelo teste de fumaça →
desinstalação silenciosa → confere que tudo foi removido e que a pasta de dados do usuário (`%LOCALAPPDATA%\Tersus`) continua intacta.

## Evidências publicadas pela CI

| Artefato | Conteúdo |
|---|---|
| `resultados-testes-windows` | `junit.xml` e `resumo.md` da suíte (Windows) |
| `Tersus-<versão>-win-x64` | `Tersus.exe`, `Tersus-Setup-<versão>-x64.exe`, `SHA256SUMS.txt` |
| `evidencias-interface-e-instalador` | capturas de tela, `smoke-report.md`, `verificacao-independente.md`, `verificacao-instalador.md`, registros do instalador |

As capturas e os registros são gerados com arquivos **artificiais**; não há dados pessoais.

Para **ver as capturas no navegador**, sem baixar o ZIP: em *Actions → Tersus - Testes, EXE e instalador (Windows) → Run workflow*, marque `publicar_capturas`.
No fim da execução a CI recria a branch `evidencias` (um único commit, sobrescrito a cada vez; pode ser apagada) com as capturas e os relatórios.
Esse é o **único** job da pipeline com permissão de escrita no repositório e ele só roda nessa execução manual; builds comuns (pull request e push) nunca gravam nada no repositório.
