<#
.SYNOPSIS
  Builds RELATORIO_DE_BUILD.md from what the pipeline itself measured (nothing here is typed by hand): environment, toolchain, hashes,
  test summary, interface smoke test, independent verification and installer verification.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutFile,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Commit,
    [string]$RunUrl = '',
    [string]$InnoVersion = '',
    [string]$TestSummary = '',
    [string]$SmokeDir = '',
    [string]$InstallerDir = '',
    [string]$SumsFile = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-IfExists([string]$path) { if ($path -and (Test-Path -LiteralPath $path)) { return (Get-Content -LiteralPath $path -Raw) } return $null }

$sb = New-Object System.Text.StringBuilder
function Add([string]$line = '') { [void]$sb.AppendLine($line) }

$os = [System.Environment]::OSVersion.VersionString
$dotnet = (& dotnet --version) 2>$null
Add "# Relatório de build — Tersus $Version"
Add ''
Add 'Gerado automaticamente pela própria pipeline (GitHub Actions). Os números abaixo foram **medidos** na execução, não digitados.'
Add ''
Add '## Identificação'
Add ''
Add "- Versão: **$Version**"
Add "- Commit: ``$Commit``"
if ($RunUrl) { Add "- Execução da pipeline: $RunUrl" }
Add "- Data (UTC): $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss'))"
Add "- Máquina: runner hospedado ``windows-latest`` (máquina virtual descartável) — $os"
Add "- SDK do .NET: $dotnet"
if ($InnoVersion) { Add "- Inno Setup: $InnoVersion" }
Add ''
Add '> **Ambiente de teste:** tudo foi validado em máquinas **virtuais** descartáveis do GitHub Actions (Windows Server 2025). Nada foi testado em máquina física, em Windows 10, com leitor de tela ou após reinicialização. Veja `docs/PENDENCIAS.md`.'
Add ''

Add '## Entregáveis'
Add ''
$sums = Read-IfExists $SumsFile
if ($sums) { Add '```'; Add $sums.TrimEnd(); Add '```' }
Add ''
Add '- O executável e o instalador **não são assinados digitalmente** e não têm reputação no SmartScreen; confira os SHA-256 acima.'
Add ''

Add '## Testes automatizados (suíte própria, sem pacotes)'
Add ''
$tests = Read-IfExists $TestSummary
if ($tests) { Add $tests.TrimEnd() } else { Add '_resumo indisponível_' }
Add ''

Add '## Teste de fumaça da interface real (EXE publicado)'
Add ''
$smokeIndep = Read-IfExists (Join-Path $SmokeDir 'exe-publicado\verificacao-independente.md')
$smokeReport = Read-IfExists (Join-Path $SmokeDir 'exe-publicado\smoke-report.md')
if ($smokeReport) { Add $smokeReport.TrimEnd() } else { Add '_relatório indisponível_' }
Add ''
Add '### Verificação independente (feita por fora do programa)'
Add ''
if ($smokeIndep) { Add $smokeIndep.TrimEnd() } else { Add '_indisponível_' }
Add ''

Add '## Instalador: instalar, usar, desinstalar'
Add ''
$inst = Read-IfExists (Join-Path $InstallerDir 'verificacao-instalador.md')
if ($inst) { Add $inst.TrimEnd() } else { Add '_indisponível_' }
Add ''
$smokeInstalled = Read-IfExists (Join-Path $InstallerDir 'smoke-instalado\verificacao-independente.md')
if ($smokeInstalled) { Add '### Programa instalado: verificação independente'; Add ''; Add $smokeInstalled.TrimEnd(); Add '' }

Add '## O que este relatório NÃO prova'
Add ''
Add '- Funcionamento em Windows 10 ou em máquina física; reinício do sistema após desinstalar (prova-se a ausência de qualquer mecanismo de execução automática, não o reinício).'
Add '- Acessibilidade com leitor de tela, tema real de alto contraste, outras escalas de DPI e outros idiomas do Windows.'
Add '- Convivência com antivírus de terceiros e com o SmartScreen (o programa não é assinado).'
Add ''
[System.IO.File]::WriteAllText($OutFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "Relatório de build gerado: ``$([System.IO.Path]::GetFileName($OutFile))`` (no artefato do pacote)." }
Write-Host "Relatório escrito em $OutFile"
