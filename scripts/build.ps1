# build.ps1 — verifiche, pubblicazione e installer, su Windows (equivalente di scripts/build.sh).
#
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1
#   $env:SKIP_CHECKS = "1"; .\scripts\build.ps1      salta le verifiche
#
# Serve: .NET SDK 10 (winget install Microsoft.DotNet.SDK.10) e NSIS 3 (winget install NSIS.NSIS).
# L'installer NON è firmato: al primo avvio SmartScreen chiede "Ulteriori informazioni" → "Esegui comunque".
$ErrorActionPreference = "Stop"

$AppName = "activity-tracker"
# Nome del file che l'utente scarica (prodotto: «e-track agent»); progetto e eseguibile interno restano activity-tracker.
$SetupName = "e-track-agent-windows-setup.exe"
$Rid = "win-x64"
$Root = Split-Path -Parent $PSScriptRoot
$PublishDir = Join-Path $Root "build\publish"
$DistDir = Join-Path $Root "dist"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
if (-not $makensis) {
    foreach ($candidate in @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe")) {
        if (Test-Path $candidate) { $makensis = $candidate }
    }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "manca il .NET SDK 10: winget install Microsoft.DotNet.SDK.10" }
if (-not $makensis) { throw "manca NSIS: winget install NSIS.NSIS" }

[xml]$props = Get-Content (Join-Path $Root "Directory.Build.props")
$Version = $props.Project.PropertyGroup.Version
$Setup = Join-Path $DistDir $SetupName

if (-not (Test-Path (Join-Path $Root "assets\activity-tracker.ico"))) {
    Write-Host "==> icone"
    python (Join-Path $PSScriptRoot "make-icon.py")
}

if ($env:SKIP_CHECKS -ne "1") {
    Write-Host "==> verifiche (motore, regole, formato, storage, sync con server finto)"
    dotnet run --project (Join-Path $Root "tests\ActivityTracker.Checks") -c Release
    if ($LASTEXITCODE -ne 0) { throw "verifiche fallite" }
}

Write-Host "==> dotnet publish ($Rid, autosufficiente, un solo .exe)"
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
dotnet publish (Join-Path $Root "src\ActivityTracker.Windows") -c Release -r $Rid -o $PublishDir -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "publish fallito" }

Write-Host "==> installer NSIS"
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
if (Test-Path $Setup) { Remove-Item -Force $Setup }
& $makensis -V2 "-DVERSION=$Version" "-DSOURCE_DIR=$PublishDir" "-DOUT_FILE=$Setup" (Join-Path $Root "installer\activity-tracker.nsi")
if ($LASTEXITCODE -ne 0) { throw "makensis fallito" }
Copy-Item (Join-Path $Root "installer\LEGGIMI.txt") (Join-Path $DistDir "LEGGIMI.txt") -Force
(Get-FileHash $Setup -Algorithm SHA256).Hash.ToLower() + "  " + (Split-Path -Leaf $Setup) | Set-Content "$Setup.sha256"

$mb = "{0:N1} MB" -f ((Get-Item $Setup).Length / 1MB)
Write-Host "==> pronto: $Setup ($mb)"
