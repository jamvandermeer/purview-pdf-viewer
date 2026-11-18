<#
    Verwijdert Purview PDF Viewer (Intune, systeemcontext).
    Laat gebruikersgegevens (MIP-cache in %LOCALAPPDATA%\PurviewPdfViewer) staan;
    die bevat alleen versleutelde beleidscache en wordt bij herinstallatie hergebruikt.
#>
$ErrorActionPreference = 'Stop'

$AppName     = 'Purview PDF Viewer'
$ExeName     = 'PurviewPdfViewer.exe'
$ProgId      = 'PurviewPdfViewer.Document'
$ProgramDir  = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$InstallDir  = Join-Path $ProgramDir $AppName
$ShortcutDir = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
$LogDir      = Join-Path $env:ProgramData 'Microsoft\IntuneManagementExtension\Logs'
$LogFile     = Join-Path $LogDir 'PurviewPdfViewer-uninstall.log'

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
Start-Transcript -Path $LogFile -Append | Out-Null

try {
    Get-Process -Name 'PurviewPdfViewer' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1

    Remove-Item (Join-Path $ShortcutDir "$AppName.lnk") -Force -ErrorAction SilentlyContinue

    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)

    foreach ($path in @(
        "SOFTWARE\Classes\$ProgId",
        "SOFTWARE\Classes\Applications\$ExeName",
        'SOFTWARE\PurviewPdfViewer',
        'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PurviewPdfViewer'
    )) {
        $hklm.DeleteSubKeyTree($path, $false)
    }

    $openWith = $hklm.OpenSubKey('SOFTWARE\Classes\.pdf\OpenWithProgids', $true)
    if ($openWith) { $openWith.DeleteValue($ProgId, $false); $openWith.Dispose() }

    $registered = $hklm.OpenSubKey('SOFTWARE\RegisteredApplications', $true)
    if ($registered) { $registered.DeleteValue('PurviewPdfViewer', $false); $registered.Dispose() }

    $hklm.Dispose()

    # Programmamap verwijderen. Draait dit script vanuit die map (Apps en onderdelen),
    # dan eerst naar een tijdelijke locatie wisselen.
    Set-Location $env:SystemRoot
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }

    Write-Output 'Verwijderen voltooid.'
    $exitCode = 0
}
catch {
    Write-Output "FOUT: $($_.Exception.Message)"
    $exitCode = 1
}
finally {
    Stop-Transcript | Out-Null
}
exit $exitCode
