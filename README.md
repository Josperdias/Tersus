# Tersus

Utilitário **nativo para Windows 10/11** que mostra **de onde vem o espaço do seu disco**, explica o que cada coisa é e, **somente se você pedir e confirmar**,
move **arquivos temporários antigos para a Lixeira**. Funciona 100 % offline: sem conta, sem telemetria, sem serviço em segundo plano, sem atualizador.

> **Estado: versão 0.3.0, candidata a release, ainda *não assinada*.** Validada apenas em máquinas virtuais descartáveis do GitHub Actions (Windows Server 2025).
> Leia [`docs/PENDENCIAS.md`](docs/PENDENCIAS.md) e [`docs/SEGURANCA.md`](docs/SEGURANCA.md) antes de usar em um computador de trabalho. Teste primeiro numa VM.

## O que ele faz

| Tela | Para quê | Altera algo? |
|---|---|---|
| **Início** | escolher uma unidade ou pasta e analisar (cancelável, memória limitada) | não |
| **Visão geral** | mapa de blocos proporcional + lista navegável + “o que é isto?” com nível de confiança | não |
| **Maiores arquivos** | os maiores arquivos, filtráveis, com explicação e tamanho lógico × alocado | não |
| **Duplicados** | cópias idênticas (comparação progressiva, SHA-256) — **só relatório** | não |
| **Limpeza** | buscar → **simular** → **confirmar** → mover `.tmp`/`.temp` antigos para a **Lixeira** | **só aqui**, e só com confirmação |
| **Aplicativos** | programas instalados (somente leitura; sem “último uso” inventado) | não |
| **Histórico** | até 24 resumos locais (sem nomes de arquivos) e comparação com a análise anterior | só os próprios resumos do Tersus |
| **Recomendações** | orientações sobre hibernação, `Windows.old`, Lixeira, Downloads, nuvem… | não |
| **Segurança e sobre** | o que o Tersus nunca faz, onde ficam os dados dele | não |

### A única ação destrutiva

Mover para a **Lixeira do Windows** arquivos `.tmp`/`.temp` da pasta **TEMP do seu usuário**, criados **e** modificados há **14 dias ou mais**, com até **256 MiB**,
depois de uma simulação e de uma janela de confirmação (Cancelar é o botão padrão). Cada arquivo é verificado de novo imediatamente antes de ser movido e a chegada
à Lixeira é comprovada. O Tersus **nunca** esvazia a Lixeira, nunca apaga de forma permanente e nunca toca em documentos, navegadores, senhas, programas, registro ou arquivos do Windows.
Mover para a Lixeira **não libera espaço** até você esvaziá-la.

## Baixar

Cada execução da CI publica, em *Actions → Artifacts* (guardado por 90 dias), o pacote `Tersus-<versão>-win-x64` com:

- `Tersus.exe` — autossuficiente, arquivo único (não precisa instalar o .NET);
- `Tersus-Setup-<versão>-x64.exe` — instalador por usuário (sem administrador, sem inicialização automática);
- `SHA256SUMS.txt` — confira antes de usar. **Os arquivos não são assinados digitalmente**; o Windows pode mostrar um aviso do SmartScreen;
- `RELATORIO_DE_BUILD.md` — gerado pela própria pipeline, com os números medidos nessa execução (veja também [`docs/RELATORIO_DE_VALIDACAO.md`](docs/RELATORIO_DE_VALIDACAO.md)).

## Compilar e testar

Requer o SDK do .NET 10.

```powershell
dotnet run --project Tersus.Tests/Tersus.Tests.csproj -c Release   # testes (Core, auditoria estática; integração Windows quando em Windows)
dotnet build Tersus.sln -c Release -warnaserror
dotnet publish Tersus.App/Tersus.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o publish
```

O instalador é gerado com o Inno Setup 6: `ISCC.exe /DAppVersion=0.3.0 /DSourceExe=..\publish\Tersus.exe installer\Tersus.iss`.

## Estrutura

```
Tersus.Core/    análise e segurança (sem interface): política, scanner, planejador, executor, duplicados, histórico, explicador, camada Windows
Tersus.App/     interface WPF (.NET 10), MVVM leve, zero pacotes NuGet
Tersus.Tests/   executor de testes próprio (sem pacotes) + auditoria estática de segurança
installer/      Inno Setup 6 (instalação por usuário)
tools/ci/       scripts PowerShell de verificação em máquina Windows limpa
docs/           origem, escopo, segurança, testes, pendências
```

## Documentação

- [`docs/SEGURANCA.md`](docs/SEGURANCA.md) — travas, modelo de ameaças, o que **não** foi validado
- [`docs/TESTES.md`](docs/TESTES.md) — o que cada teste prova e como a CI valida o EXE e o instalador
- [`docs/RELATORIO_DE_VALIDACAO.md`](docs/RELATORIO_DE_VALIDACAO.md) — resultados reais, medições, defeitos achados e corrigidos
- [`docs/ESCOPO.md`](docs/ESCOPO.md) — escopo entregue × documentos do produto
- [`docs/PENDENCIAS.md`](docs/PENDENCIAS.md) — o que falta e decisões que dependem do dono
- [`docs/ORIGEM_E_LIMITACOES.md`](docs/ORIGEM_E_LIMITACOES.md) — de onde veio o código (inclui a limitação sobre o ZIP original)

## Licença

MIT — ver [`LICENSE`](LICENSE). Nenhum código de WinDirStat, Czkawka, BleachBit ou Bulk Crap Uninstaller foi usado.
