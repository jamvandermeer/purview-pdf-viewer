<#
.SYNOPSIS
    Bouwt het Intune Win32-pakket (.intunewin) voor Purview PDF Viewer.

.DESCRIPTION
    1. Publiceert de app self-contained (gebruikers hebben geen .NET-runtime nodig).
    2. Schrijft appsettings.json met de ClientId/TenantId van de doeltenant.
    3. Ondertekent optioneel de .exe met een code-signing-certificaat.
    4. Genereert het detectiescript voor deze versie.
    5. Pakt alles in met IntuneWinAppUtil.exe (wordt zo nodig gedownload).

    Resultaat in .\out\:
      PurviewPdfViewer-<versie>.intunewin   -> uploaden in Intune
      Detect-PurviewPdfViewer.ps1           -> detectieregel in Intune

.EXAMPLE
    .\Build-IntunePackage.ps1 -Version 1.0.0 -ClientId 11111111-2222-3333-4444-555555555555 -TenantId contoso.nl

.EXAMPLE
    # Met ondertekening (certificaat in CurrentUser\My of LocalMachine\My)
    .\Build-IntunePackage.ps1 -Version 1.0.1 -ClientId <id> -TenantId <tenant> -CertThumbprint A1B2C3...
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')] [string] $Version,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F-]{36}$')] [string] $ClientId,
    [string] $TenantId = 'organizations',
    [string] $Locale = 'nl-NL',
    [string] $CertThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here        = $PSScriptRoot
$projectFile = (Resolve-Path (Join-Path $here '..\..\PurviewPdfViewer.csproj')).Path
$sourceDir   = Join-Path $here 'Source'
$appDir      = Join-Path $sourceDir 'App'
$outDir      = Join-Path $here 'out'
$toolsDir    = Join-Path $here 'tools'
$prepTool    = Join-Path $toolsDir 'IntuneWinAppUtil.exe'

function Write-Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

# ── 1. Publiceren ────────────────────────────────────────────────────────────
Write-Step "Publiceren (self-contained, versie $Version)"
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
& dotnet publish $projectFile -c Release -r win-x64 --self-contained true `
    "-p:Version=$Version" -o $appDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish is mislukt (exitcode $LASTEXITCODE)." }

# ── 2. Configuratie voor de doeltenant ───────────────────────────────────────
Write-Step "appsettings.json schrijven (TenantId: $TenantId)"
[ordered]@{
    ClientId = $ClientId
    TenantId = $TenantId
    Locale   = $Locale
} | ConvertTo-Json | Set-Content -Path (Join-Path $appDir 'appsettings.json') -Encoding UTF8

# ── 3. Ondertekenen (optioneel) ──────────────────────────────────────────────
if ($CertThumbprint) {
    Write-Step "Ondertekenen met certificaat $CertThumbprint"
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe niet gevonden. Installeer de Windows SDK (onderdeel "Signing Tools").' }

    $targets = @(
        (Join-Path $appDir 'PurviewPdfViewer.exe'),
        (Join-Path $appDir 'PurviewPdfViewer.dll')
    ) | Where-Object { Test-Path $_ }

    & $signtool.FullName sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $targets
    if ($LASTEXITCODE -ne 0) { throw "Ondertekenen is mislukt (exitcode $LASTEXITCODE)." }
}
else {
    Write-Warning 'Niet ondertekend. Gebruikers kunnen een SmartScreen-melding krijgen; WDAC/AppLocker kan de app blokkeren.'
}

# ── 4. Detectiescript voor deze versie ───────────────────────────────────────
Write-Step 'Detectiescript genereren'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$detectTemplate = Get-Content (Join-Path $here 'Detect-PurviewPdfViewer.template.ps1') -Raw
$detectScript   = $detectTemplate.Replace('__VERSION__', $Version)
$detectPath     = Join-Path $outDir 'Detect-PurviewPdfViewer.ps1'
Set-Content -Path $detectPath -Value $detectScript -Encoding UTF8

# ── 5. IntuneWinAppUtil ──────────────────────────────────────────────────────
if (-not (Test-Path $prepTool)) {
    Write-Step 'IntuneWinAppUtil.exe downloaden (Microsoft Win32 Content Prep Tool)'
    New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $url = 'https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool/raw/master/IntuneWinAppUtil.exe'
    Invoke-WebRequest -Uri $url -OutFile $prepTool -UseBasicParsing
}

Write-Step 'Pakket maken (.intunewin)'
$generated = Join-Path $outDir 'install.intunewin'
if (Test-Path $generated) { Remove-Item $generated -Force }
& $prepTool -c $sourceDir -s 'install.ps1' -o $outDir -q
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $generated)) { throw 'IntuneWinAppUtil heeft geen pakket gemaakt.' }

$final = Join-Path $outDir "PurviewPdfViewer-$Version.intunewin"
Move-Item $generated $final -Force

Write-Host "`nKlaar." -ForegroundColor Green
Write-Host "  Pakket:      $final"
Write-Host "  Detectie:    $detectPath"
Write-Host "  Installeren: powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\install.ps1"
Write-Host "  Verwijderen: powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\uninstall.ps1"
