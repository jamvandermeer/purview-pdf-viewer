<#
.SYNOPSIS
    Bouwt een ondertekend MSI-installatiepakket voor Purview PDF Viewer.

.DESCRIPTION
    1. Publiceert de app self-contained (geen .NET-runtime nodig op de pc).
    2. Ondertekent de eigen binaries (PurviewPdfViewer.exe/.dll).
    3. Bouwt het MSI met WiX Toolset v7 (via NuGet, geen aparte installatie nodig).
    4. Ondertekent het MSI en controleert de handtekening.

    Het MSI bevat GEEN tenantgegevens: je geeft CLIENTID/TENANTID mee bij installatie.
    Daardoor gebruik je hetzelfde ondertekende MSI voor elke organisatie.

    WiX Toolset v7 valt onder de Open Source Maintenance Fee (OSMF) EULA. Geef
    -AcceptWixOsmfEula mee om die te accepteren (zie README-MSI.md).

.EXAMPLE
    # Ondertekenen met Azure Artifact Signing
    .\Build-Msi.ps1 -Version 1.0.0 -AcceptWixOsmfEula -ArtifactSigningMetadata ..\signing\metadata.json

.EXAMPLE
    # Ondertekenen met een certificaat (thumbprint uit certlm.msc/certmgr.msc of hardwaretoken)
    .\Build-Msi.ps1 -Version 1.0.0 -AcceptWixOsmfEula -CertThumbprint A1B2C3D4...

.EXAMPLE
    # Testbuild zonder ondertekening
    .\Build-Msi.ps1 -Version 1.0.0 -AcceptWixOsmfEula -SkipSigning
#>
[CmdletBinding(DefaultParameterSetName = 'ArtifactSigning')]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,

    [Parameter(Mandatory, ParameterSetName = 'ArtifactSigning')] [string] $ArtifactSigningMetadata,
    [Parameter(Mandatory, ParameterSetName = 'Certificate')]     [string] $CertThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'Unsigned')]        [switch] $SkipSigning,

    [switch] $AcceptWixOsmfEula
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here        = $PSScriptRoot
$projectFile = (Resolve-Path (Join-Path $here '..\..\PurviewPdfViewer.csproj')).Path
$wixProject  = Join-Path $here 'PurviewPdfViewer.Installer.wixproj'
$stagingDir  = Join-Path $here 'staging\app'
$outDir      = Join-Path $here 'out'
$msiPath     = Join-Path $outDir "PurviewPdfViewer-$Version.msi"

Import-Module (Join-Path $here '..\signing\CodeSigning.psm1') -Force

function Write-Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

$signMode = $PSCmdlet.ParameterSetName

function Invoke-Signing([string[]] $files) {
    switch ($signMode) {
        'ArtifactSigning' { Invoke-CodeSigning -Files $files -ArtifactSigningMetadata $ArtifactSigningMetadata }
        'Certificate'     { Invoke-CodeSigning -Files $files -CertThumbprint $CertThumbprint }
        default           { Write-Warning "Niet ondertekend: $($files | ForEach-Object { Split-Path $_ -Leaf })" }
    }
}

if (-not $AcceptWixOsmfEula) {
    throw 'WiX Toolset v7 vereist acceptatie van de Open Source Maintenance Fee (OSMF) EULA. ' +
          'Lees https://wixtoolset.org/osmf/ en voer het script opnieuw uit met -AcceptWixOsmfEula.'
}

# ── 1. App publiceren ────────────────────────────────────────────────────────
Write-Step "App publiceren (self-contained, versie $Version)"
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
& dotnet publish $projectFile -c Release -r win-x64 --self-contained true `
    "-p:Version=$Version" -o $stagingDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish is mislukt (exitcode $LASTEXITCODE)." }

# Neutrale appsettings.json: de tenantconfiguratie komt uit het MSI/register.
[ordered]@{ ClientId = ''; TenantId = 'organizations'; Locale = 'nl-NL' } |
    ConvertTo-Json | Set-Content -Path (Join-Path $stagingDir 'appsettings.json') -Encoding UTF8

# ── 2. Eigen binaries ondertekenen ───────────────────────────────────────────
Write-Step 'App-bestanden ondertekenen'
Invoke-Signing @(
    (Join-Path $stagingDir 'PurviewPdfViewer.exe'),
    (Join-Path $stagingDir 'PurviewPdfViewer.dll')
)

# ── 3. MSI bouwen ────────────────────────────────────────────────────────────
Write-Step 'MSI bouwen (WiX Toolset v7)'
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
& dotnet build $wixProject -c Release --nologo `
    "-p:Version=$Version" "-p:AppSourceDir=$stagingDir" '-p:AcceptEula=wix7' `
    -o $outDir
if ($LASTEXITCODE -ne 0) { throw "Bouwen van het MSI is mislukt (exitcode $LASTEXITCODE)." }

$built = Get-ChildItem $outDir -Filter '*.msi' -Recurse | Select-Object -First 1
if (-not $built) { throw 'Geen MSI gevonden in de uitvoermap.' }
Move-Item $built.FullName $msiPath -Force
Get-ChildItem $outDir -Exclude '*.msi' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# ── 4. MSI ondertekenen ──────────────────────────────────────────────────────
Write-Step 'MSI ondertekenen'
Invoke-Signing @($msiPath)

# ── Resultaat ────────────────────────────────────────────────────────────────
$productCode = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db   = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msiPath, 0))
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT Value FROM Property WHERE Property='ProductCode'"))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $rec  = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    $productCode = $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, 1)
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
} catch { }

$sizeMb = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
Write-Host "`nKlaar." -ForegroundColor Green
Write-Host "  MSI:         $msiPath ($sizeMb MB)"
if ($productCode) { Write-Host "  ProductCode: $productCode" }
Write-Host "`nInstalleren (stil):"
Write-Host "  msiexec /i `"PurviewPdfViewer-$Version.msi`" CLIENTID=<client-id> TENANTID=<tenant> /qn /l*v `"%TEMP%\PurviewPdfViewer-install.log`""
Write-Host "Verwijderen (stil):"
if ($productCode) { Write-Host "  msiexec /x $productCode /qn" } else { Write-Host '  msiexec /x <ProductCode> /qn' }
