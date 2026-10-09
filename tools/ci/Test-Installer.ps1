<#
.SYNOPSIS
  Installs the real Tersus installer silently on a clean Windows machine, verifies what it did (and did not do), runs the INSTALLED
  program through the interface check, uninstalls silently and verifies that the program is gone while the person's own data is not.

  Evidence collected: per-user locations and HKCU registration (no administrator-only locations), no autostart entry, no service,
  no scheduled task, no Startup-folder item, identical bytes to the published exe, uninstall leaves %LOCALAPPDATA%\Tersus alone.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$PublishedExe,
    [Parameter(Mandatory)][string]$WorkDir,
    [Parameter(Mandatory)][string]$ScanFolder,
    [Parameter(Mandatory)][string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Setup = (Resolve-Path -LiteralPath $Setup).Path
$PublishedExe = (Resolve-Path -LiteralPath $PublishedExe).Path
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$WorkDir = (Resolve-Path -LiteralPath $WorkDir).Path

$failures = New-Object System.Collections.Generic.List[string]
$rows = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $rows.Add([pscustomobject]@{ Check = $name; Result = $(if ($ok) { 'ok' } else { 'FALHOU' }); Detail = $detail })
    if (-not $ok) { $failures.Add("$name $detail") }
    Write-Host ("[{0}] {1} {2}" -f $(if ($ok) { ' ok ' } else { 'FAIL' }), $name, $detail)
}

$appId = '{8D3F1C52-6B5A-4E0B-9C7A-2F1E6A4D9B30}_is1'
$uninstallHkcu = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$uninstallHklm = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$uninstallHklm32 = "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Tersus'
$installedExe = Join-Path $installDir 'Tersus.exe'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Tersus.lnk'
$dataDir = Join-Path $env:LOCALAPPDATA 'Tersus'
$marker = Join-Path $dataDir 'marcador-do-teste.txt'

function Get-SystemSnapshot {
    $runKeys = foreach ($k in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run') {
        if (Test-Path -LiteralPath $k) { (Get-Item -LiteralPath $k).GetValueNames() | ForEach-Object { "${k}::$_" } }
    }
    $services = Get-Service | ForEach-Object { $_.Name } | Sort-Object
    $tasks = try { Get-ScheduledTask -ErrorAction Stop | ForEach-Object { $_.TaskPath + $_.TaskName } | Sort-Object } catch { @() }
    $startup = foreach ($d in @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('CommonStartup'))) {
        if ($d -and (Test-Path -LiteralPath $d)) { Get-ChildItem -LiteralPath $d -Force | ForEach-Object { $_.FullName } }
    }
    [pscustomobject]@{ Run = @($runKeys | Sort-Object); Services = @($services); Tasks = @($tasks); Startup = @($startup | Sort-Object) }
}
function Same($a, $b) { return (@(Compare-Object -ReferenceObject @($a) -DifferenceObject @($b)).Count -eq 0) }

Check 'o programa não estava instalado antes do teste' (-not (Test-Path -LiteralPath $installedExe) -and -not (Test-Path -LiteralPath $uninstallHkcu -ErrorAction SilentlyContinue))

# ---- before ---------------------------------------------------------------------------------------------------------------------------
$before = Get-SystemSnapshot
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
[System.IO.File]::WriteAllText($marker, "dados do usuario (criado pelo teste do instalador)`n")

# ---- install ----------------------------------------------------------------------------------------------------------------------------
$installLog = Join-Path $WorkDir 'instalacao.log'
$p = Start-Process -FilePath $Setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/LOG=`"$installLog`"") -PassThru -Wait
Check 'o instalador silencioso terminou com código 0' ($p.ExitCode -eq 0) "exit=$($p.ExitCode)"
$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-Path -LiteralPath $installedExe) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 400 }

Check 'Tersus.exe foi instalado na pasta do usuário (%LOCALAPPDATA%\Programs\Tersus)' (Test-Path -LiteralPath $installedExe) $installDir
Check 'o desinstalador foi criado' (Test-Path -LiteralPath (Join-Path $installDir 'unins000.exe'))
Check 'a licença acompanha o programa' (Test-Path -LiteralPath (Join-Path $installDir 'LICENSE'))
if (Test-Path -LiteralPath $installedExe) {
    $h1 = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
    $h2 = (Get-FileHash -LiteralPath $PublishedExe -Algorithm SHA256).Hash
    Check 'o exe instalado é idêntico, byte a byte, ao exe publicado' ($h1 -eq $h2) "SHA-256 $h1"
}
Check 'atalho no Menu Iniciar do usuário' (Test-Path -LiteralPath $startMenu) $startMenu

$reg = Get-ItemProperty -LiteralPath $uninstallHkcu -ErrorAction SilentlyContinue
Check 'registro de desinstalação em HKCU (por usuário)' ($null -ne $reg)
if ($null -ne $reg) {
    Check 'nome e versão no registro de desinstalação' ($reg.DisplayName -eq 'Tersus' -and $reg.DisplayVersion -eq $Version) "DisplayName=$($reg.DisplayName) DisplayVersion=$($reg.DisplayVersion)"
    Check 'o desinstalador registrado está na pasta do usuário' ($reg.UninstallString -like "*$($env:LOCALAPPDATA)*") $reg.UninstallString
}
Check 'nada foi registrado em HKLM (sem administrador)' (-not (Test-Path -LiteralPath $uninstallHklm -ErrorAction SilentlyContinue) -and -not (Test-Path -LiteralPath $uninstallHklm32 -ErrorAction SilentlyContinue))

$after = Get-SystemSnapshot
Check 'nenhuma entrada de inicialização automática (Run/RunOnce)' (Same $before.Run $after.Run) (@(Compare-Object @($before.Run) @($after.Run) | ForEach-Object { $_.InputObject }) -join ', ')
Check 'nenhum serviço novo' (Same $before.Services $after.Services) (@(Compare-Object @($before.Services) @($after.Services) | ForEach-Object { $_.InputObject }) -join ', ')
Check 'nenhuma tarefa agendada nova' (Same $before.Tasks $after.Tasks) (@(Compare-Object @($before.Tasks) @($after.Tasks) | ForEach-Object { $_.InputObject }) -join ', ')
Check 'nada novo na pasta Inicializar' (Same $before.Startup $after.Startup)

if (Test-Path -LiteralPath $installLog) {
    $adminLines = @(Select-String -LiteralPath $installLog -Pattern 'admin|elevat|privilege' -SimpleMatch:$false | ForEach-Object { $_.Line.Trim() } | Select-Object -First 8)
    Write-Host '--- trechos do log do instalador sobre privilégios ---'
    $adminLines | ForEach-Object { Write-Host $_ }
}

# ---- run the INSTALLED program through the interface check --------------------------------------------------------------------------------
if (Test-Path -LiteralPath $installedExe) {
    & (Join-Path $PSScriptRoot 'Invoke-SmokeTest.ps1') -Exe $installedExe -OutDir (Join-Path $WorkDir 'smoke-instalado') -ScanFolder $ScanFolder
    Check 'o programa INSTALADO passou no teste de fumaça e na verificação independente' ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"
}

# ---- normal start of the INSTALLED program (not the test mode) ------------------------------------------------------------------------------
if (Test-Path -LiteralPath $installedExe) {
    $app = Start-Process -FilePath $installedExe -PassThru
    $startDeadline = (Get-Date).AddSeconds(60)
    $title = ''
    while ((Get-Date) -lt $startDeadline -and -not $app.HasExited) {
        $app.Refresh()
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) { $title = $app.MainWindowTitle; break }
        Start-Sleep -Milliseconds 300
    }
    Check 'o programa instalado abre normalmente (janela principal "Tersus")' ((-not $app.HasExited) -and $title -eq 'Tersus') "título='$title'"
    Start-Sleep -Seconds 2
    Check 'o uso normal criou a pasta de dados e a de registros do Tersus' (Test-Path -LiteralPath (Join-Path $dataDir 'logs'))
    if (-not $app.HasExited) {
        [void]$app.CloseMainWindow()
        $closed = $app.WaitForExit(20000)
        if (-not $closed) { try { $app.Kill() } catch { } }
        Check 'o programa fecha normalmente pela janela (código 0)' ($closed -and $app.ExitCode -eq 0) "exit=$($app.ExitCode)"
    }
}

# ---- uninstall --------------------------------------------------------------------------------------------------------------------------------
$unins = Join-Path $installDir 'unins000.exe'
if (Test-Path -LiteralPath $unins) {
    $u = Start-Process -FilePath $unins -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $WorkDir 'desinstalacao.log')`"") -PassThru -Wait
    Check 'o desinstalador silencioso terminou com código 0' ($u.ExitCode -eq 0) "exit=$($u.ExitCode)"
    # The real work happens in a temporary copy of the uninstaller after this process exits: wait for it.
    $deadline = (Get-Date).AddSeconds(120)
    while ((Test-Path -LiteralPath $installedExe) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 2
}
Check 'Tersus.exe foi removido' (-not (Test-Path -LiteralPath $installedExe))
Check 'a pasta do programa foi removida' (-not (Test-Path -LiteralPath $installDir)) $installDir
Check 'o atalho do Menu Iniciar foi removido' (-not (Test-Path -LiteralPath $startMenu))
Check 'o registro de desinstalação foi removido' (-not (Test-Path -LiteralPath $uninstallHkcu -ErrorAction SilentlyContinue))
Check 'os dados do usuário (%LOCALAPPDATA%\Tersus) continuam intactos depois de desinstalar' ((Test-Path -LiteralPath $marker) -and ((Get-Content -LiteralPath $marker -Raw) -like 'dados do usuario*') -and (Test-Path -LiteralPath (Join-Path $dataDir 'logs')))

$final = Get-SystemSnapshot
Check 'depois da desinstalação o sistema voltou ao estado inicial (Run/serviços/tarefas/Inicializar)' ((Same $before.Run $final.Run) -and (Same $before.Services $final.Services) -and (Same $before.Tasks $final.Tasks) -and (Same $before.Startup $final.Startup))

Remove-Item -LiteralPath $dataDir -Recurse -Force -ErrorAction SilentlyContinue

# ---- summary ------------------------------------------------------------------------------------------------------------------------------------
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('### Instalador: instalar, usar, desinstalar (máquina Windows limpa)')
[void]$md.AppendLine('')
[void]$md.AppendLine('| Verificação | Resultado | Detalhe |')
[void]$md.AppendLine('|---|---|---|')
foreach ($r in $rows) { [void]$md.AppendLine("| $($r.Check -replace '\|','/') | $($r.Result) | $($r.Detail -replace '\|','/') |") }
[System.IO.File]::WriteAllText((Join-Path $WorkDir 'verificacao-instalador.md'), $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $md.ToString() }

if ($failures.Count -gt 0) {
    Write-Host "::error::$($failures.Count) verificação(ões) do instalador falharam"
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host "Instalador aprovado: $($rows.Count) verificações ok."
exit 0
