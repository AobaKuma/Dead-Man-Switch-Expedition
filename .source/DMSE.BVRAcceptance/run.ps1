<#
  In-game BVR acceptance run (RimWorld 1.7).

  1. Builds DMSE.BVRAcceptance.dll (against the committed 1.7/Assemblies/DMSE.dll).
  2. Copies it into 1.7/Assemblies for this run only.
  3. Starts RimWorld with -quicktest (new Crashlanded colony, 250 map) and -dmse-bvr-acceptance=<OutDir>.
     The runner plays every test, writes bvr_acceptance_log.txt / bvr_acceptance_results.tsv into OutDir,
     takes screenshots, deletes its temporary save and quits the game.
  4. Removes the DLL again (also when the game crashes or times out) and copies the screenshots into OutDir.

  Usage:  powershell -ExecutionPolicy Bypass -File .source/DMSE.BVRAcceptance/run.ps1 -OutDir <dir> [-TimeoutMinutes 45] [-Only S3,W6]
  RimWorld must not be running (it locks 1.7/Assemblies). The active mod list is used as is.
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$TimeoutMinutes = 45,
    [string]$GameExe = '',
    [string]$Only = ''   # comma-separated test ids, e.g. 'S3,W6'; empty runs every test
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$modRoot = (Resolve-Path (Join-Path $here '..\..')).Path
if ($GameExe -eq '') { $GameExe = (Resolve-Path (Join-Path $modRoot '..\..\RimWorld.exe')).Path }   # 1.7 (Waylith) executable
$assemblies = Join-Path $modRoot '1.7\Assemblies'
$runnerDll = Join-Path $assemblies 'DMSE.BVRAcceptance.dll'

if (Get-Process -Name 'RimWorld', 'RimWorldWin64' -ErrorAction SilentlyContinue) { throw 'RimWorld is running; close it first.' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

dotnet build (Join-Path $here 'DMSE.BVRAcceptance.csproj') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'acceptance build failed' }

Copy-Item (Join-Path $here 'bin\DMSE.BVRAcceptance.dll') $runnerDll -Force
$started = Get-Date

try {
    $gameArgs = @('-quicktest', "-dmse-bvr-acceptance=$OutDir")
    if ($Only -ne '') { $gameArgs += "-dmse-bvr-only=$Only" }
    $proc = Start-Process -FilePath $GameExe -ArgumentList $gameArgs -WorkingDirectory (Split-Path $GameExe) -PassThru
    Write-Output "RimWorld started (pid $($proc.Id)); waiting up to $TimeoutMinutes min..."
    # Wait for every RimWorld process (the launcher may hand over to a child or be restarted through Steam).
    $deadline = $started.AddMinutes($TimeoutMinutes)
    Start-Sleep -Seconds 10
    while ((Get-Process -Name 'RimWorld', 'RimWorldWin64' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
    }
    if (Get-Process -Name 'RimWorld', 'RimWorldWin64' -ErrorAction SilentlyContinue) {
        Write-Output 'Timeout: stopping RimWorld.'
        Get-Process -Name 'RimWorld', 'RimWorldWin64' -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 3
    }
}
finally {
    for ($i = 0; $i -lt 20 -and (Test-Path $runnerDll); $i++) {
        try { Remove-Item $runnerDll -Force } catch { Start-Sleep -Seconds 1 }
    }
    if (Test-Path $runnerDll) { Write-Warning "Could not remove $runnerDll - delete it manually." }
}

$list = Join-Path $OutDir 'bvr_acceptance_screenshots.txt'
if (Test-Path $list) {
    foreach ($shot in Get-Content $list) { if (Test-Path $shot) { Move-Item $shot $OutDir -Force } }
}
$playerLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log'
if (Test-Path $playerLog) {
    $log = Get-Item $playerLog
    if ($log.LastWriteTime -ge $started) { Copy-Item $playerLog (Join-Path $OutDir 'Player.log') -Force }
}
$results = Join-Path $OutDir 'bvr_acceptance_results.tsv'
if (Test-Path $results) { Get-Content $results } else { Write-Output 'No results file: the run did not finish (see Player.log / bvr_acceptance_log.txt).' }
