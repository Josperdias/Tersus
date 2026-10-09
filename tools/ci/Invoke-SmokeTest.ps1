<#
.SYNOPSIS
  Runs a built Tersus.exe in its automated interface check ("--smoke-test") against a sandbox that THIS script prepares in the real
  TEMP folder, then verifies from OUTSIDE the program that exactly the intended files went to the Windows Recycle Bin and that every
  decoy (recent files, wrong extensions, read-only, system, hard links, links, junctions, folders named .tmp, files in use) was left alone.

  Nothing here is product code: it only exists to prove the product's safety rules on a real Windows machine.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$ScanFolder,
    [switch]$NoExecute,
    [int]$TimeoutSeconds = 420
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Exe = (Resolve-Path -LiteralPath $Exe).Path
$ScanFolder = (Resolve-Path -LiteralPath $ScanFolder).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

$failures = New-Object System.Collections.Generic.List[string]
$rows = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $rows.Add([pscustomobject]@{ Check = $name; Result = $(if ($ok) { 'ok' } else { 'FALHOU' }); Detail = $detail })
    if (-not $ok) { $failures.Add("$name $detail") }
    Write-Host ("[{0}] {1} {2}" -f $(if ($ok) { ' ok ' } else { 'FAIL' }), $name, $detail)
}

# ---- 1. sandbox inside the real TEMP folder -------------------------------------------------------------------------------------
Add-Type -Namespace Win32 -Name Native -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)] public static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint size);'
function Get-LongPath([string]$path) {
    $sb = New-Object System.Text.StringBuilder 2048
    $n = [Win32.Native]::GetLongPathName($path, $sb, 2048)
    if ($n -gt 0 -and $n -lt 2048) { return $sb.ToString() }
    return $path
}
$temp = Get-LongPath ([System.IO.Path]::GetTempPath())
$tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
$sandbox = Join-Path $temp "Tersus-smoke-$tag"
$outside = Join-Path $temp "Tersus-smoke-fora-$tag"
New-Item -ItemType Directory -Path $sandbox, (Join-Path $sandbox 'sub'), (Join-Path $sandbox 'vazia.tmp'), $outside | Out-Null

function New-File([string]$path, [long]$bytes, [int]$createdDaysAgo, [int]$modifiedDaysAgo) {
    $fs = [System.IO.File]::Create($path)
    try { $fs.SetLength($bytes) } finally { $fs.Dispose() }
    [System.IO.File]::SetCreationTime($path, (Get-Date).AddDays(-$createdDaysAgo))
    [System.IO.File]::SetLastWriteTime($path, (Get-Date).AddDays(-$modifiedDaysAgo))
}

# Files that MUST be moved to the Recycle Bin (old .tmp/.temp, small, ordinary):
$expectMoved = @(
    @{ Path = Join-Path $sandbox 'velho-1.tmp'; Bytes = 1024; Days = 30 },
    @{ Path = Join-Path $sandbox 'velho-2.tmp'; Bytes = 3072; Days = 20 },
    @{ Path = Join-Path $sandbox 'velho-3.temp'; Bytes = 2048; Days = 15 },
    @{ Path = Join-Path $sandbox 'sub\aninhado-1.tmp'; Bytes = 1024; Days = 40 },
    @{ Path = Join-Path $sandbox 'sub\aninhado-2.temp'; Bytes = 1024; Days = 400 }
)
foreach ($f in $expectMoved) { New-File $f.Path $f.Bytes $f.Days $f.Days }

# Decoys that MUST stay exactly where they are:
$decoys = New-Object System.Collections.Generic.List[string]
function Add-Decoy([string]$path) { $decoys.Add($path) }

$p = Join-Path $sandbox 'recente.tmp'; New-File $p 500 0 0; Add-Decoy $p
$p = Join-Path $sandbox 'modificado-recente.tmp'; New-File $p 500 30 0; Add-Decoy $p
$p = Join-Path $sandbox 'criado-recente.tmp'; New-File $p 500 0 30; Add-Decoy $p
$p = Join-Path $sandbox 'quase-14-dias.tmp'; New-File $p 500 13 13; Add-Decoy $p
$p = Join-Path $sandbox 'documento.txt'; New-File $p 500 60 60; Add-Decoy $p
$p = Join-Path $sandbox 'foto.jpg'; New-File $p 500 60 60; Add-Decoy $p
$p = Join-Path $sandbox 'velho.tmp.exe'; New-File $p 500 60 60; Add-Decoy $p
$p = Join-Path $sandbox 'sem-extensao'; New-File $p 500 60 60; Add-Decoy $p
$p = Join-Path $sandbox 'grande.tmp'; New-File $p (257MB) 60 60; Add-Decoy $p
$p = Join-Path $sandbox 'somente-leitura.tmp'; New-File $p 500 60 60; [System.IO.File]::SetAttributes($p, [System.IO.FileAttributes]::ReadOnly); Add-Decoy $p
$p = Join-Path $sandbox 'sistema.tmp'; New-File $p 500 60 60; [System.IO.File]::SetAttributes($p, [System.IO.FileAttributes]::System); Add-Decoy $p
$p = Join-Path $sandbox 'hardlink-a.tmp'; New-File $p 500 60 60; Add-Decoy $p
$hl = Join-Path $sandbox 'hardlink-b.tmp'
try { New-Item -ItemType HardLink -Path $hl -Target $p | Out-Null; Add-Decoy $hl } catch { Write-Host "hardlink not created: $($_.Exception.Message)" }

# In use by another program (exclusive handle held for the whole run):
$inUse = Join-Path $sandbox 'em-uso.tmp'; New-File $inUse 500 60 60; Add-Decoy $inUse
$holder = [System.IO.File]::Open($inUse, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)

# Files outside the sandbox that a junction / symlink points to (must never be followed or touched):
$outsideFiles = @()
foreach ($n in 'fora-1.tmp', 'fora-2.tmp') {
    $o = Join-Path $outside $n; New-File $o 700 90 90; $outsideFiles += $o
}
$junction = Join-Path $sandbox 'juncao'
$symlink = Join-Path $sandbox 'link-para-fora.tmp'
$linksCreated = @()
try { New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null; $linksCreated += $junction } catch { Write-Host "junction not created: $($_.Exception.Message)" }
try { New-Item -ItemType SymbolicLink -Path $symlink -Target $outsideFiles[0] | Out-Null; $linksCreated += $symlink } catch { Write-Host "symlink not created: $($_.Exception.Message)" }

# Record "before" state of every decoy to prove they are bit-for-bit unchanged afterwards.
$before = @{}
foreach ($d in ($decoys + $outsideFiles)) {
    $i = Get-Item -LiteralPath $d -Force
    $before[$d] = @{ Length = $i.Length; Write = $i.LastWriteTimeUtc.Ticks; Create = $i.CreationTimeUtc.Ticks; Attr = [int]$i.Attributes }
}
$emptyDirTmp = Join-Path $sandbox 'vazia.tmp'

# ---- 2. run the program -----------------------------------------------------------------------------------------------------------
$arguments = @('--smoke-test', "`"$OutDir`"", '--smoke-scan', "`"$ScanFolder`"", '--smoke-cleanup-folder', "`"$sandbox`"",
    '--smoke-expect-eligible', "$($expectMoved.Count)")
if (-not $NoExecute) { $arguments += @('--smoke-execute', '--smoke-expect-moved', "$($expectMoved.Count)") }

Write-Host "Starting: $Exe $($arguments -join ' ')"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru -WindowStyle Normal
$finished = $proc.WaitForExit($TimeoutSeconds * 1000)
if (-not $finished) {
    try { $proc.Kill($true) } catch { }
    Check 'o programa terminou dentro do tempo limite' $false "$TimeoutSeconds s"
} else {
    $proc.WaitForExit()
    Check 'código de saída do teste de fumaça é 0' ($proc.ExitCode -eq 0) "exit=$($proc.ExitCode) em $([int]$sw.Elapsed.TotalSeconds) s"
}

$holder.Dispose()

# ---- 3. independent verification --------------------------------------------------------------------------------------------------
$report = Join-Path $OutDir 'smoke-report.md'
Check 'o relatório do teste de fumaça foi gerado' (Test-Path -LiteralPath $report)
if (Test-Path -LiteralPath $report) {
    $text = Get-Content -LiteralPath $report -Raw
    Check 'o relatório diz APROVADO' ($text -match 'APROVADO')
    Check 'nenhum erro de binding' ($text -match 'binding\): 0')
    Check 'nenhuma exceção não tratada' ($text -match 'interface: 0')
}
$shots = @(Get-ChildItem -LiteralPath (Join-Path $OutDir 'screenshots') -Filter '*.png' -ErrorAction SilentlyContinue)
Check 'capturas de tela foram geradas' ($shots.Count -ge 12) "$($shots.Count) arquivo(s)"
Check 'as capturas não estão vazias' (($shots | Where-Object { $_.Length -lt 2000 }).Count -eq 0)

foreach ($d in $decoys) {
    $exists = Test-Path -LiteralPath $d
    $same = $false
    if ($exists) {
        $i = Get-Item -LiteralPath $d -Force; $b = $before[$d]
        $same = ($i.Length -eq $b.Length) -and ($i.LastWriteTimeUtc.Ticks -eq $b.Write) -and ($i.CreationTimeUtc.Ticks -eq $b.Create) -and ([int]$i.Attributes -eq $b.Attr)
    }
    Check ('isca intacta: ' + [System.IO.Path]::GetFileName($d)) ($exists -and $same)
}
foreach ($o in $outsideFiles) {
    $i = Get-Item -LiteralPath $o -Force -ErrorAction SilentlyContinue
    Check "arquivo fora da pasta (alvo de link) intacto: $([System.IO.Path]::GetFileName($o))" ($null -ne $i -and $i.Length -eq $before[$o].Length -and $i.LastWriteTimeUtc.Ticks -eq $before[$o].Write)
}
Check 'a pasta chamada vazia.tmp continua existindo' (Test-Path -LiteralPath $emptyDirTmp -PathType Container)
foreach ($l in $linksCreated) { Check "o link continua existindo: $([System.IO.Path]::GetFileName($l))" (Test-Path -LiteralPath $l) }

if ($NoExecute) {
    foreach ($f in $expectMoved) { Check "sem confirmação nada é movido: $([System.IO.Path]::GetFileName($f.Path))" (Test-Path -LiteralPath $f.Path) }
} else {
    foreach ($f in $expectMoved) { Check "saiu da pasta: $([System.IO.Path]::GetFileName($f.Path))" (-not (Test-Path -LiteralPath $f.Path)) }

    # Independent proof that they are in the Recycle Bin: read the $I records Windows wrote for this user on the sandbox's drive.
    $root = [System.IO.Path]::GetPathRoot($sandbox)
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $binDir = Join-Path $root ('$Recycle.Bin\' + $sid)
    $originals = @{}
    if (Test-Path -LiteralPath $binDir) {
        foreach ($info in Get-ChildItem -LiteralPath $binDir -Force -Filter '$I*' -ErrorAction SilentlyContinue) {
            try {
                $bytes = [System.IO.File]::ReadAllBytes($info.FullName)
                $version = [BitConverter]::ToInt64($bytes, 0)
                if ($version -eq 2) {
                    $chars = [BitConverter]::ToInt32($bytes, 24)
                    $orig = [System.Text.Encoding]::Unicode.GetString($bytes, 28, [Math]::Max(0, ($chars - 1) * 2))
                } else {
                    $orig = [System.Text.Encoding]::Unicode.GetString($bytes, 24, [Math]::Min(520, $bytes.Length - 24)).TrimEnd([char]0)
                }
                $payload = Join-Path $binDir ('$R' + $info.Name.Substring(2))
                $originals[$orig.ToLowerInvariant()] = @{ Info = $info.FullName; HasPayload = (Test-Path -LiteralPath $payload) }
            } catch { Write-Host "could not read $($info.FullName): $($_.Exception.Message)" }
        }
    }
    Check 'a Lixeira do usuário pôde ser lida para a verificação independente' ($originals.Count -gt 0) "$($originals.Count) registro(s) em $binDir"
    foreach ($f in $expectMoved) {
        $key = $f.Path.ToLowerInvariant()
        Check ('está na Lixeira (registro $I e conteúdo $R): ' + [System.IO.Path]::GetFileName($f.Path)) ($originals.ContainsKey($key) -and $originals[$key].HasPayload)
    }
    foreach ($d in $decoys) {
        Check ('isca NÃO está na Lixeira: ' + [System.IO.Path]::GetFileName($d)) (-not $originals.ContainsKey($d.ToLowerInvariant()))
    }

    $logs = @(Get-ChildItem -LiteralPath (Join-Path $OutDir 'logs') -Filter 'limpeza-*.log' -ErrorAction SilentlyContinue)
    Check 'o registro da limpeza foi gravado' ($logs.Count -ge 1)
    if ($logs.Count -ge 1) {
        $logText = Get-Content -LiteralPath $logs[0].FullName -Raw
        $movedLines = ([regex]::Matches($logText, 'MovedToRecycleBin')).Count
        Check 'o registro lista os arquivos movidos' ($movedLines -eq $expectMoved.Count) "$movedLines linha(s)"
    }
}

# ---- 4. clean up the sandbox (links first, never following them) -----------------------------------------------------------------
foreach ($l in $linksCreated) {
    try {
        $item = Get-Item -LiteralPath $l -Force
        if ($item.PSIsContainer) { [System.IO.Directory]::Delete($l, $false) } else { [System.IO.File]::Delete($l) }
    } catch { Write-Host "could not remove link ${l}: $($_.Exception.Message)" }
}
foreach ($dir in @($sandbox, $outside)) {
    try {
        foreach ($f in Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue) {
            try { [System.IO.File]::SetAttributes($f.FullName, [System.IO.FileAttributes]::Normal) } catch { }
        }
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    } catch { }
}

# ---- 5. summary --------------------------------------------------------------------------------------------------------------------------
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("### Teste de fumaça: ``$([System.IO.Path]::GetFileName($Exe))``")
[void]$md.AppendLine('')
[void]$md.AppendLine('| Verificação independente | Resultado | Detalhe |')
[void]$md.AppendLine('|---|---|---|')
foreach ($r in $rows) { [void]$md.AppendLine("| $($r.Check -replace '\|','/') | $($r.Result) | $($r.Detail -replace '\|','/') |") }
$mdPath = Join-Path $OutDir 'verificacao-independente.md'
[System.IO.File]::WriteAllText($mdPath, $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $md.ToString()
    if (Test-Path -LiteralPath $report) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value (Get-Content -LiteralPath $report -Raw) }
}

if ($failures.Count -gt 0) {
    Write-Host "::error::$($failures.Count) verificação(ões) falharam"
    $failures | ForEach-Object { Write-Host "  - $_" }
    if (Test-Path -LiteralPath $report) { Write-Host '----- smoke-report.md -----'; Get-Content -LiteralPath $report }
    exit 1
}
Write-Host "Teste de fumaça aprovado: $($rows.Count) verificações independentes ok."
exit 0
