<#
.SYNOPSIS
  Writes the body of the GitHub Release, which is the download page: what each file is, how to check and install it, the unsigned / VM-only
  warnings and the SHA-256 of THIS build. Whatever changes from build to build (version, commit, run, hashes) is read from the pipeline, never typed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutFile,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$SumsFile,
    [string]$RunUrl = '',
    [string]$Repo = 'Josperdias/Tersus'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $SumsFile)) { throw "SHA256SUMS.txt não encontrado: $SumsFile" }
$sums = (Get-Content -LiteralPath $SumsFile -Raw).TrimEnd()
$setup = "Tersus-Setup-$Version-x64.exe"
if ($sums -notmatch [regex]::Escape($setup) -or $sums -notmatch [regex]::Escape('*Tersus.exe')) {
    throw "SHA256SUMS.txt não lista $setup e Tersus.exe: o pacote não é o esperado."
}

$short = $Commit.Substring(0, [Math]::Min(7, $Commit.Length))
$docs = "https://github.com/$Repo/blob/$Commit/docs"
$fence = '```'

$lines = New-Object System.Collections.Generic.List[string]
function Add([string]$line = '') { $lines.Add($line) }

Add "# Tersus $Version — versão candidata (não assinada)"
Add ''
Add 'Utilitário nativo para **Windows 64 bits** que mostra **de onde vem o espaço do seu disco** e, **somente se você pedir e confirmar**, move **arquivos temporários antigos para a Lixeira**. Funciona 100 % offline: sem conta, sem telemetria, sem serviço em segundo plano.'
Add ''
Add '> ⚠️ **Leia antes de baixar**'
Add '>'
Add '> - **Não é assinado digitalmente.** O Windows (SmartScreen) vai mostrar um aviso na primeira vez. É esperado; confira o SHA-256 abaixo antes de continuar.'
Add '> - **Foi testado apenas em máquinas virtuais descartáveis do GitHub** (Windows Server 2025). **Não** foi testado em Windows 10, em máquina física, depois de reiniciar, com leitor de tela nem com antivírus de terceiros.'
Add '> - A **única** ação que altera algo é a **Limpeza**: ela move `.tmp`/`.temp` antigos da sua pasta TEMP para a **Lixeira**, só depois de uma simulação e da sua confirmação. Nada é apagado de forma permanente. Mesmo assim, **teste primeiro numa máquina virtual** antes de usar no computador de trabalho.'
Add ''
Add '## Arquivos desta versão'
Add ''
Add '| Arquivo | Para quê |'
Add '|---|---|'
Add "| **``$setup``** | **Instalador (recomendado).** Instala só para o seu usuário, sem administrador, sem iniciar junto com o Windows. |"
Add '| `Tersus.exe` | Versão **portátil**: um arquivo só, não instala nada (os dados ficam em `%LOCALAPPDATA%\Tersus`). |'
Add '| `SHA256SUMS.txt` | Códigos SHA-256 dos dois arquivos acima, para você conferir. |'
Add '| `RELATORIO_DE_BUILD.md` | Relatório de testes gerado pela própria pipeline para **esta** versão (testes, interface real, instalar/usar/desinstalar). |'
Add ''
Add 'Os arquivos têm entre 50 e 60 MB e **não precisam do .NET instalado**.'
Add ''
Add '## 1. Confira o arquivo (30 segundos)'
Add ''
Add 'No PowerShell, na pasta onde você baixou:'
Add ''
Add "${fence}powershell"
Add "Get-FileHash .\$setup -Algorithm SHA256"
Add $fence
Add ''
Add 'O resultado precisa ser **idêntico** a este (maiúsculas e minúsculas não importam):'
Add ''
Add $fence
Add $sums
Add $fence
Add ''
Add 'Se for diferente, **não abra o arquivo** e baixe de novo. Para a versão portátil, use `Get-FileHash .\Tersus.exe -Algorithm SHA256`.'
Add ''
Add '## 2. Instale'
Add ''
Add "1. Abra ``$setup``."
Add '2. Se aparecer **“O Windows protegeu seu computador”**, clique em **Mais informações → Executar assim mesmo** (somente se o SHA-256 bateu).'
Add '3. Siga o assistente, que está em português. **Não é pedida senha de administrador.** O programa fica em `%LOCALAPPDATA%\Programs\Tersus`; o atalho na Área de Trabalho é opcional.'
Add '4. Para remover: *Configurações → Aplicativos → Tersus → Desinstalar*. O histórico e os registros de limpeza ficam em `%LOCALAPPDATA%\Tersus`; apague a pasta se quiser.'
Add ''
Add '## 3. Primeiro uso, com segurança'
Add ''
Add '- **Início → Analisar** não altera nada. Todas as telas, menos a **Limpeza**, só leem dados.'
Add '- Na **Limpeza**: *Procurar → Simular → Confirmar*. “Cancelar” é o botão padrão da confirmação. Depois, confira a Lixeira: dá para restaurar tudo de lá.'
Add '- Mover para a Lixeira **não libera espaço** até você esvaziá-la, e o Tersus nunca faz isso por você.'
Add ''
Add '## Detalhes desta versão'
Add ''
Add "- Versão **$Version** · commit [``$short``](https://github.com/$Repo/commit/$Commit)"
if ($RunUrl) { Add "- Gerado, testado (testes automáticos, interface real, instalar → usar → desinstalar) e empacotado nesta execução da pipeline: $RunUrl" }
Add "- Guia completo: [Baixar e instalar]($docs/BAIXAR_E_INSTALAR.md) · [Segurança]($docs/SEGURANCA.md) · [Relatório de validação]($docs/RELATORIO_DE_VALIDACAO.md) · [Pendências e limitações]($docs/PENDENCIAS.md)"
Add ''

[System.IO.File]::WriteAllText($OutFile, ($lines -join "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Notas da versão escritas em $OutFile ($($lines.Count) linhas)"
