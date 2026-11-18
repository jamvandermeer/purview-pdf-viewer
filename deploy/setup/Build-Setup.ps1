<#
.SYNOPSIS
    Bouwt een ondertekende setup.exe voor Purview PDF Viewer.

.DESCRIPTION
    1. Bouwt en ondertekent het MSI (via ..\msi\Build-Msi.ps1).
    2. Verpakt het MSI in een setup.exe (WiX Burn) met installatievenster.
    3. Ondertekent de setup-engine en de setup.exe (WiX roept de ondertekening zelf aan).

    Met -ClientId/-TenantId zet je een standaardconfiguratie in de setup.exe, handig voor
    intern gebruik: dubbelklikken is dan genoeg. Een CLIENTID/TENANTID op de opdrachtregel
    gaat altijd voor.

.EXAMPLE
    # Interne setup met meegebakken configuratie, ondertekend met Artifact Signing
    .\Build-Setup.ps1 -Version 1.0.4 -AcceptWixOsmfEula -ArtifactSigningMetadata ..\signing\metadata.json `
        -ClientId <client-id> -TenantId contoso.nl

.EXAMPLE
    # Neutrale setup (klant geeft CLIENTID mee), ondertekend met certificaat
    .\Build-Setup.ps1 -Version 1.0.4 -AcceptWixOsmfEula -CertThumbprint A1B2C3...

.EXAMPLE
    # Testbuild zonder ondertekening
    .\Build-Setup.ps1 -Version 1.0.4 -AcceptWixOsmfEula -SkipSigning -ClientId <id> -TenantId <tenant>
#>
[CmdletBinding(DefaultParameterSetName = 'ArtifactSigning')]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,

    [Parameter(Mandatory, ParameterSetName = 'ArtifactSigning')] [string] $ArtifactSigningMetadata,
    [Parameter(Mandatory, ParameterSetName = 'Certificate')]     [string] $CertThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'Unsigned')]        [switch] $SkipSigning,

    [ValidatePattern('^$|^[0-9a-fA-F-]{36}$')] [string] $ClientId = '',
    [string] $TenantId = '',

    [switch] $AcceptWixOsmfEula
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here       = $PSScriptRoot
$msiScript  = Join-Path $here '..\msi\Build-Msi.ps1'
$msiPath    = Join-Path $here "..\msi\out\PurviewPdfViewer-$Version.msi"
$wixProject = Join-Path $here 'PurviewPdfViewer.Setup.wixproj'
$outDir     = Join-Path $here 'out'
$setupPath  = Join-Path $outDir "PurviewPdfViewer-Setup-$Version.exe"
$mode       = $PSCmdlet.ParameterSetName

function Write-Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

if (-not $AcceptWixOsmfEula) {
    throw 'WiX Toolset v7 vereist acceptatie van de Open Source Maintenance Fee (OSMF) EULA. ' +
          'Lees https://wixtoolset.org/osmf/ en voer het script opnieuw uit met -AcceptWixOsmfEula.'
}
if ($TenantId -and -not $ClientId) { throw 'Geef bij -TenantId ook -ClientId op.' }

# ── 1. MSI bouwen (en ondertekenen) ──────────────────────────────────────────
Write-Step 'MSI bouwen'
$msiArgs = @{ Version = $Version; AcceptWixOsmfEula = $true }
switch ($mode) {
    'ArtifactSigning' { $msiArgs.ArtifactSigningMetadata = (Resolve-Path $ArtifactSigningMetadata).Path }
    'Certificate'     { $msiArgs.CertThumbprint = $CertThumbprint }
    default           { $msiArgs.SkipSigning = $true }
}
& $msiScript @msiArgs
if (-not (Test-Path $msiPath)) { throw "MSI niet gevonden: $msiPath" }
$msiPath = (Resolve-Path $msiPath).Path

# ── 2. setup.exe bouwen (WiX Burn) ───────────────────────────────────────────
Write-Step 'setup.exe bouwen'
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$buildArgs = @(
    'build', $wixProject, '-c', 'Release', '--nologo', '-o', $outDir,
    "-p:Version=$Version", "-p:MsiPath=$msiPath", '-p:AcceptEula=wix7'
)
if ($ClientId) { $buildArgs += "-p:DefaultClientId=$ClientId" }
if ($TenantId) { $buildArgs += "-p:DefaultTenantId=$TenantId" }
switch ($mode) {
    'ArtifactSigning' { $buildArgs += @('-p:SigningMode=ArtifactSigning', "-p:SigningValue=$((Resolve-Path $ArtifactSigningMetadata).Path)") }
    'Certificate'     { $buildArgs += @('-p:SigningMode=Certificate', "-p:SigningValue=$CertThumbprint") }
}
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Bouwen van de setup.exe is mislukt (exitcode $LASTEXITCODE)." }

$built = Get-ChildItem $outDir -Filter '*.exe' | Select-Object -First 1
if (-not $built) { throw 'Geen setup.exe gevonden in de uitvoermap.' }
Move-Item $built.FullName $setupPath -Force
Get-ChildItem $outDir -Exclude '*.exe' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# ── 3. Controle ──────────────────────────────────────────────────────────────
if ($mode -ne 'Unsigned') {
    Write-Step 'Handtekening controleren'
    $sig = Get-AuthenticodeSignature $setupPath
    if ($sig.Status -ne 'Valid') { throw "Handtekening van setup.exe is niet geldig: $($sig.Status) – $($sig.StatusMessage)" }
    Write-Host "    Geldig, ondertekend door: $($sig.SignerCertificate.Subject)" -ForegroundColor Green
}
else {
    Write-Warning 'setup.exe is niet ondertekend.'
}

$sizeMb = [math]::Round((Get-Item $setupPath).Length / 1MB, 1)
Write-Host "`nKlaar." -ForegroundColor Green
Write-Host "  Setup: $setupPath ($sizeMb MB)"
if ($ClientId) { Write-Host "  Standaardconfiguratie: ClientId $ClientId, TenantId $(if ($TenantId) { $TenantId } else { 'organizations' })" }
Write-Host "`nGebruik:"
Write-Host "  Dubbelklikken                                  -> installatie met venster"
Write-Host "  PurviewPdfViewer-Setup-$Version.exe /quiet $(if (-not $ClientId) { 'CLIENTID=<id> TENANTID=<tenant>' })"
Write-Host "  PurviewPdfViewer-Setup-$Version.exe /uninstall /quiet"
