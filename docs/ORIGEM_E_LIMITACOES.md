# Origem do código e limitações conhecidas

Este documento existe por honestidade: ele diz exatamente de onde veio cada parte do Tersus 0.3.0 e o que **não** foi possível fazer.

## O que foi pedido

Obter o arquivo `Tersus_v0.2.0_codigo_completo.zip` da pasta *Tersus — Aplicativo de Limpeza* no Google Drive e continuar o desenvolvimento a partir dele.
O SHA-256 declarado pelo dono para esse arquivo é:

```
a447bded3b3b42cf8a759c7e3272c1955d65c45ec9a170a4ec074a11869fde25  Tersus_v0.2.0_codigo_completo.zip
```

## O que realmente aconteceu

- Os quatro documentos de texto da pasta (`00_COMECE_AQUI`, `01_REPASSE_CLAUDE`, `02_README_TERSUS`, `03_ESCOPO_PRODUTO_E_PROXIMAS_IMPLEMENTACOES`) foram lidos por inteiro.
- O conector do Drive entrega o conteúdo de um ZIP apenas como **texto base64 dentro da conversa**. O ambiente em que esta sessão rodou não permite gravar essa saída diretamente em disco; o único caminho era transcrever o base64 manualmente.
- A transcrição manual falhou: a partir de certo ponto o texto reconstruído deixou de decodificar (erros de *inflate* e CRC32 incompatível). Isso foi detectado, não escondido: cada entrada do ZIP foi conferida por CRC32.
- **Somente 3 arquivos foram recuperados e verificados** por CRC32 e tamanho. Eles estão em [`docs/origem-verificada/`](origem-verificada/):
  `App.v0.2.0.xaml`, `gitignore.v0.2.0` e `windows-build.v0.2.0.yml`.
- O ZIP original **não pôde ser materializado**; portanto o SHA-256 acima **não pôde ser conferido** nesta sessão.

## Consequência: o 0.3.0 é uma reconstrução, não um fork byte a byte

O código de `Tersus.Core`, `Tersus.App`, `Tersus.Tests` e do instalador foi **escrito de novo a partir da especificação** (os quatro documentos + os três arquivos originais verificados), preservando os nomes de componentes descritos no README do 0.2.0:

| Componente descrito no 0.2.0 | Onde está no 0.3.0 |
|---|---|
| `StorageScanner` | `Tersus.Core/Scanning/StorageScanner.cs` |
| `FilePolicy` (regras de elegibilidade) | `Tersus.Core/Policy/FilePolicy.cs` (+ `PathGuard`, `TempRootResolver`) |
| `FilePolicy.Explain` (explicador) | `Tersus.Core/Knowledge/FileExplainer.cs` |
| `CleanupPlanner` (simulação) | `Tersus.Core/Cleanup/CleanupPlanner.cs` |
| `RecycleService` (envio à Lixeira) | `Tersus.Core/Cleanup/CleanupExecutor.cs` + `Tersus.Core/Windows/ShellRecycler.cs` |
| `DuplicateAnalyzer` | `Tersus.Core/Duplicates/DuplicateAnalyzer.cs` |
| `AppCatalog` | `Tersus.Core/Windows/RegistryAppCatalog.cs` + `Tersus.Core/Apps/InstalledApp.cs` |
| `HistoryStore` | `Tersus.Core/History/HistoryStore.cs` |
| `SystemAdvisor` | `Tersus.Core/Advice/SystemAdvisor.cs` |

Riscos dessa abordagem, que o dono deve conhecer:

1. Comportamentos do 0.2.0 que **não estão descritos** nos documentos podem não existir aqui (ou existir de outro jeito).
2. As **regras de segurança** (extensões, 14 dias, 256 MiB, só TEMP do usuário, só Lixeira, nunca esvaziar a Lixeira, confirmação explícita, revalidação antes de cada ação, bloqueio de links/hardlinks/arquivos em uso) foram implementadas **mais estritas ou iguais** às descritas, nunca mais frouxas, e são verificadas por testes automatizados e por uma auditoria estática do código-fonte.
3. Não há como afirmar compatibilidade de formato com um `historico.json` gerado pelo 0.2.0.

## Como reconciliar com o ZIP original

Se o dono quiser que a reconstrução seja comparada com o original, basta **colocar o ZIP no repositório** (por exemplo em `docs/origem/Tersus_v0.2.0_codigo_completo.zip`) ou entregá-lo por um canal que permita gravar arquivos. Com ele em disco será possível: (a) conferir o SHA-256; (b) extrair e comparar arquivo a arquivo; (c) portar para o 0.3.0 qualquer comportamento do original que falte aqui, sempre sem afrouxar nenhuma trava de segurança.

## Outras limitações de origem

- O nome do detentor de direitos na licença MIT (`LICENSE`) foi preenchido como "Tersus contributors"; o dono deve confirmar o nome correto.
- O nome do repositório foi escolhido como `Tersus` (o dono pode renomear).
- Ferramentas de referência (WinDirStat, Czkawka, BleachBit, Bulk Crap Uninstaller) foram usadas apenas como inspiração conceitual descrita nos documentos; **nenhum código delas foi copiado**.
