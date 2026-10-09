# Relatório de validação do Tersus 0.3.0

Registro **honesto e verificável** do que foi testado, do que falhou pelo caminho, de como cada defeito foi corrigido e do que **não** foi provado.
Os números abaixo vêm da execução da pipeline em [`actions/runs/37968779505`](https://github.com/Josperdias/Tersus/actions/runs/37968779505)
(commit `382692f`, PR #1). O relatório gerado pela própria pipeline (`RELATORIO_DE_BUILD.md`) acompanha o pacote de artefatos.

> **Ambiente:** máquinas virtuais descartáveis do GitHub Actions (`windows-latest`, Windows Server 2025, build 10.0.26100, .NET SDK 10.0.401, Inno Setup 6.7.1).
> **Não** foi usada máquina física, nem Windows 10, nem leitor de tela, nem reinicialização. O EXE e o instalador **não são assinados**.

## 1. Resultado final (execução verde)

| Item | Resultado |
|---|---|
| Build da solução inteira, `-warnaserror` | 0 avisos, 0 erros |
| Testes automatizados no Windows | **215 no total: 214 aprovados, 0 falhas, 1 ignorado** (44,9 s) |
| Testes portáteis no Linux | verde |
| Auditoria estática de segurança (12 testes) | verde, inclusive com os testes de mutação do próprio auditor |
| `Tersus.exe` (win-x64, autossuficiente, arquivo único) | 59,0 MB · SHA-256 `e6e5c50de17a0a6862bbef710e477ff3dc52495ada60b0fd9238a9d87c4cbfb6` · **sem assinatura** |
| `Tersus-Setup-0.3.0-x64.exe` (Inno Setup 6.7.1, por usuário) | SHA-256 `227df2668ff175b95ced5eada365d25d6dd435ca7c701ab12e8cdd08b1d0e9f3` · **sem assinatura** |
| Teste de fumaça da interface real, EXE publicado | 22 etapas ok · 0 erros de *binding* · 0 exceções não tratadas · 0 avisos de layout · 23 capturas · **66 verificações independentes ok** |
| Mesmo teste com o programa **instalado** (sem a etapa da árvore grande) | todas as etapas ok · 22 capturas · 66 verificações independentes ok |
| Instalador: instalar → usar → desinstalar | **26 verificações ok** |

Observação sobre os hashes: eles mudam a cada compilação (o commit entra na versão do arquivo). Confira os que acompanham o artefato que você baixar (`SHA256SUMS.txt`).

### Medições (EXE publicado, máquina virtual)

- Cancelar a análise da unidade inteira (`C:\`): a tela voltou ao normal em **0,40 s**; a análise parcial **não** entrou no histórico.
- Árvore grande (`C:\Windows`: 169.964 arquivos, 41.023 pastas, 32,1 GB lógicos, 3 pastas sem permissão):
  - passada 1, **disco frio**: 48,9 s (≈ 3.480 arquivos/s);
  - passada 2, **cache quente**: 1,4 s (≈ 119.750 arquivos/s).
- Memória: pico do conjunto de trabalho **299 MB** (264 MB antes de analisar); memória gerenciada retida depois das análises: 0,2 MB.
- Interface: o maior intervalo sem resposta do thread da interface durante as análises foi de **63 ms**.
- Programa instalado: janela principal visível **0,9 s** depois de iniciar (provavelmente com as bibliotecas nativas do arquivo único já extraídas pela execução anterior do mesmo arquivo; o tempo de uma primeira execução absoluta não foi medido).
- Experimento com os threads de listagem: 2 threads levaram 48,8 s e 4 threads 48,9 s no disco frio (uma medição de cada, em VMs diferentes, portanto indicativo); por isso o padrão **não** foi alterado. Nessa máquina o gargalo é o disco.

## 2. O que a verificação independente comprova (por fora do programa)

O script `tools/ci/Invoke-SmokeTest.ps1` prepara uma pasta de teste dentro do TEMP real e confere, sem confiar no relatório do programa:

- os **6 arquivos elegíveis** (inclusive um com nome Unicode e um em subpasta) saíram da pasta e estão na **Lixeira** (registros `$I`/`$R` lidos direto do disco);
- **16 iscas** continuam idênticas (tamanho, datas, atributos): recente, criada há 13 dias, criada agora/modificada há 30, modificada agora/criada há 30, `.txt`, `.jpg`, `.docx` de 3 anos, `.tmp.exe`, sem extensão, `.tmp` de 257 MiB, somente leitura, de sistema, par de hardlinks, `.tmp` dentro de pasta sem permissão e um arquivo **em uso** por outro programa;
- links: o symlink e a junção para fora da pasta **não foram seguidos** e os arquivos-alvo estão intactos; a pasta chamada `vazia.tmp` continua lá;
- a Lixeira **não foi esvaziada**: o item que já estava nela antes continua (1 de 1 no EXE publicado, 9 de 9 no instalado);
- o registro de limpeza lista os 6 movidos e o arquivo em uso como `Skipped … InUse`;
- o processo **não abriu conexões nem portas de rede** (amostragem a cada 0,3 s) e **não tocou** na pasta de dados real do usuário.

## 3. Linha do tempo: o que falhou, por quê e como foi corrigido

| # | Quando | O que apareceu | Causa | Correção |
|---|---|---|---|---|
| 1 | origem | o ZIP do Drive não pôde ser gravado em disco; a transcrição manual do base64 corrompeu | limitação do ambiente | só 3 arquivos recuperados e verificados; o resto foi reconstruído da especificação (ver `ORIGEM_E_LIMITACOES.md`) |
| 2 | testes de fuzz da política | duas combinações proibidas eram aprovadas: contagem de links igual a zero, e atributo de pasta sem o indicador de pasta | brechas reais na política | política endurecida (recusa os dois casos) e testes de regressão |
| 3 | 1ª rodada dos testes no Windows | 212 aprovados, 1 falha, 2 ignorados: um teste da varredura estourou 180 s | a limpeza do **próprio teste** seguia um laço de symlinks | a limpeza remove links como links; prazo de 60 s no teste |
| 4 | 1ª rodada do teste de fumaça | o programa terminou com código 2 e nenhum relatório; o script PowerShell acusou `.Count` sobre `$null` (modo estrito) | falha do roteiro; o programa não deixava rastro do que aconteceu | rastro passo a passo no programa, relatório tolerante a falhas e despejo de diagnóstico no log do job |
| 5 | 2ª rodada do teste de fumaça | `'System.Windows.Style' is not a valid value for property 'Background'` (e a limpeza ficou inutilizável) | a chave `WarnButton` existia como **pincel** e como **estilo**, em dicionários separados | pincel renomeado (`WarnButtonBg`); o lint de XAML agora rejeita chaves duplicadas. O código de saída 2 do item 4 não voltou a acontecer depois disso (causa provável: o relatório tentava registrar centenas de exceções repetidas deste defeito) |
| 6 | idem | o detector de “texto cortado” acusava 174 itens | ele não descontava margens | corrigido: 0 avisos |
| 7 | 3ª rodada | a limpeza **recusou** rodar: “pasta de dados e pasta TEMP em unidades diferentes” | o roteiro gravava os dados em `D:` e o TEMP está em `C:`. **A recusa é a trava de segurança funcionando** (nada foi movido) | o roteiro usa uma pasta de trabalho na unidade do TEMP e copia as evidências depois |
| 8 | idem | “esperava 6 candidatos, encontrou 7” | o arquivo **em uso** aparece na busca e na simulação (que só leem, por projeto) e só é barrado pela trava por arquivo na execução | expectativa corrigida (6 movidos + 1 ignorado como `InUse`) e conferida no registro |
| 9 | última rodada antes da verde final | passo do Inno Setup falhou | `ISCC /?` devolve código 1 e o GitHub propaga o último código | versão lida do registro de desinstalação |

## 4. O que ainda **não** foi provado

- Funcionamento em **máquina física** e em **Windows 10**; **reinicialização** após instalar/desinstalar (prova-se apenas a ausência de qualquer mecanismo de execução automática).
- **Revisão visual** das 23 capturas de tela: elas foram geradas e passaram nas verificações automáticas (sem erros de *binding*, sem texto cortado, arquivos de 100 a 180 KB, ou seja, com conteúdo), mas quem escreveu este relatório **não conseguiu abri-las**; vale olhar o artefato `evidencias-interface-e-instalador`.
- Leitor de tela, tema real de alto contraste (só a troca de paleta foi exercitada), outras escalas de DPI, Windows em outros idiomas.
- Convivência com antivírus de terceiros e com o SmartScreen.
- Um disco real do usuário: os tempos acima são de uma VM com disco de rede.

## 5. Como reproduzir

```powershell
# testes
dotnet run --project Tersus.Tests/Tersus.Tests.csproj -c Release
# build e publicação
dotnet build Tersus.sln -c Release -warnaserror
dotnet publish Tersus.App/Tersus.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o publish
# teste de fumaça + verificação independente (num Windows descartável!)
./tools/ci/Invoke-SmokeTest.ps1 -Exe publish/Tersus.exe -OutDir evidencias/exe -ScanFolder . -BigScanFolder $env:SystemRoot
```

> **Atenção:** os scripts de `tools/ci` movem arquivos para a Lixeira **de verdade** (os da pasta de teste que eles mesmos criam). Rode-os numa máquina virtual descartável.
