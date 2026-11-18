# Intune-detectiescript voor Purview PDF Viewer.
# Geïnstalleerd = exe aanwezig met versie >= __VERSION__.
# Intune: uitvoer op STDOUT + exitcode 0 = gedetecteerd.

$required   = [version]'__VERSION__'
$programDir = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$exe        = Join-Path $programDir 'Purview PDF Viewer\PurviewPdfViewer.exe'

if (Test-Path $exe) {
    $raw = (Get-Item $exe).VersionInfo.FileVersion
    $installed = $null
    if ([version]::TryParse(($raw -split '[ +]')[0], [ref]$installed) -and $installed -ge $required) {
        Write-Output "Purview PDF Viewer $installed gedetecteerd"
        exit 0
    }
}
exit 1
