<#
    Installeert Purview PDF Viewer voor alle gebruikers (Intune, systeemcontext).
    - Kopieert de app naar C:\Program Files\Purview PDF Viewer
    - Snelkoppeling in het Startmenu (alle gebruikers)
    - Registratie als PDF-app ("Openen met" en kiesbaar als standaard-app)
    - Vermelding in Apps en onderdelen / Programma's en onderdelen
    Werkt zowel vanuit een 32- als 64-bits PowerShell-proces.
#>
$ErrorActionPreference = 'Stop'

$AppName     = 'Purview PDF Viewer'
$ExeName     = 'PurviewPdfViewer.exe'
$ProgId      = 'PurviewPdfViewer.Document'
$Publisher   = 'Adoptify Technology B.V.'
$ProgramDir  = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$InstallDir  = Join-Path $ProgramDir $AppName
$SourceDir   = Join-Path $PSScriptRoot 'App'
$ShortcutDir = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
$LogDir      = Join-Path $env:ProgramData 'Microsoft\IntuneManagementExtension\Logs'
$LogFile     = Join-Path $LogDir 'PurviewPdfViewer-install.log'

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
Start-Transcript -Path $LogFile -Append | Out-Null

try {
    $exePath = Join-Path $InstallDir $ExeName
    $version = ((Get-Item (Join-Path $SourceDir $ExeName)).VersionInfo.ProductVersion -split '\+')[0]
    Write-Output "Installatie $AppName $version naar $InstallDir"

    # Draaiende instanties sluiten (anders zijn bestanden in gebruik)
    Get-Process -Name 'PurviewPdfViewer' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1

    # Schone installatie van de programmabestanden
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force
    Copy-Item -Path (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $InstallDir -Force

    # Snelkoppeling in het Startmenu
    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $ShortcutDir "$AppName.lnk"))
    $shortcut.TargetPath       = $exePath
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation     = "$exePath,0"
    $shortcut.Description      = 'PDF-viewer met Microsoft Purview-gevoeligheidslabels'
    $shortcut.Save()

    # Registry altijd in de 64-bits weergave
    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)

    function Set-RegValue([string]$path, [string]$name, $value, [Microsoft.Win32.RegistryValueKind]$kind = 'String') {
        $key = $hklm.CreateSubKey($path)
        try { $key.SetValue($name, $value, $kind) } finally { $key.Dispose() }
    }

    $openCommand = "`"$exePath`" `"%1`""

    # ProgId voor PDF
    Set-RegValue "SOFTWARE\Classes\$ProgId" '' 'PDF-document (Purview PDF Viewer)'
    # PDF-documenten houden hun bestaande PDF-pictogram (Acrobat → Reader → Edge → Windows-standaard);
    # het app-pictogram is alleen voor de .exe en snelkoppeling.
    $pdfIcon = "$env:SystemRoot\System32\imageres.dll,-102"
    foreach ($candidate in 'Acrobat.Document.DC', 'AcroExch.Document.DC', 'MSEdgePDF') {
        $iconKey = $hklm.OpenSubKey("SOFTWARE\Classes\$candidate\DefaultIcon")
        if ($iconKey) {
            $value = $iconKey.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $iconKey.Dispose()
            if ($value) { $pdfIcon = [string]$value; break }
        }
    }
    Set-RegValue "SOFTWARE\Classes\$ProgId\DefaultIcon" '' $pdfIcon ([Microsoft.Win32.RegistryValueKind]::ExpandString)
    Set-RegValue "SOFTWARE\Classes\$ProgId\shell\open\command" '' $openCommand

    # Verschijnt in "Openen met"
    Set-RegValue "SOFTWARE\Classes\.pdf\OpenWithProgids" $ProgId '' ([Microsoft.Win32.RegistryValueKind]::String)
    Set-RegValue "SOFTWARE\Classes\Applications\$ExeName" 'FriendlyAppName' $AppName
    Set-RegValue "SOFTWARE\Classes\Applications\$ExeName\SupportedTypes" '.pdf' ''
    Set-RegValue "SOFTWARE\Classes\Applications\$ExeName\shell\open\command" '' $openCommand

    # Kiesbaar als standaard-app (Instellingen > Standaard-apps / default app associations-XML)
    $capPath = "SOFTWARE\PurviewPdfViewer\Capabilities"
    Set-RegValue $capPath 'ApplicationName' $AppName
    Set-RegValue $capPath 'ApplicationDescription' 'PDF-viewer met Microsoft Purview-gevoeligheidslabels'
    Set-RegValue "$capPath\FileAssociations" '.pdf' $ProgId
    Set-RegValue 'SOFTWARE\RegisteredApplications' 'PurviewPdfViewer' $capPath

    # Apps en onderdelen
    $uninstallPath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PurviewPdfViewer'
    $uninstallCmd  = "powershell.exe -ExecutionPolicy Bypass -NoProfile -File `"$InstallDir\uninstall.ps1`""
    $sizeKb = [int]((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    Set-RegValue $uninstallPath 'DisplayName' $AppName
    Set-RegValue $uninstallPath 'DisplayVersion' $version
    Set-RegValue $uninstallPath 'Publisher' $Publisher
    Set-RegValue $uninstallPath 'DisplayIcon' "$exePath,0"
    Set-RegValue $uninstallPath 'InstallLocation' $InstallDir
    Set-RegValue $uninstallPath 'UninstallString' $uninstallCmd
    Set-RegValue $uninstallPath 'QuietUninstallString' $uninstallCmd
    Set-RegValue $uninstallPath 'NoModify' 1 ([Microsoft.Win32.RegistryValueKind]::DWord)
    Set-RegValue $uninstallPath 'NoRepair' 1 ([Microsoft.Win32.RegistryValueKind]::DWord)
    Set-RegValue $uninstallPath 'EstimatedSize' $sizeKb ([Microsoft.Win32.RegistryValueKind]::DWord)

    $hklm.Dispose()
    Write-Output 'Installatie voltooid.'
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
