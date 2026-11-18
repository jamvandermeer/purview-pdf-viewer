<#
    Ondertekenen met SignTool, op twee manieren:

    1. Azure Artifact Signing (voorheen Trusted Signing) — aanbevolen.
       Geen certificaat of USB-token nodig; sleutel staat in Azure. Aanmelden via
       'az login', Visual Studio of omgevingsvariabelen (DefaultAzureCredential).
       Parameter: -ArtifactSigningMetadata <pad naar metadata.json>

    2. Een code-signing-certificaat in de Windows-certificaatopslag of op een hardwaretoken.
       Parameter: -CertThumbprint <thumbprint>

    Altijd met SHA-256 en een RFC 3161-tijdstempel, zodat de handtekening geldig blijft
    nadat het certificaat verlopen is.
#>

Set-StrictMode -Version Latest

function Find-SignTool {
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path $_) }

    $candidates = foreach ($root in $roots) {
        Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } |
            ForEach-Object {
                $exe = Join-Path $_.FullName 'x64\signtool.exe'
                if (Test-Path $exe) { [pscustomobject]@{ Version = [version]$_.Name; Path = $exe } }
            }
    }

    # Artifact Signing ondersteunt build 20348 niet; neem de hoogste andere versie.
    $best = $candidates | Where-Object { $_.Version.Build -ne 20348 } |
            Sort-Object Version -Descending | Select-Object -First 1
    if (-not $best) {
        throw 'signtool.exe niet gevonden. Installeer de Windows SDK (onderdeel "Windows SDK Signing Tools for Desktop Apps"), bijv.: winget install Microsoft.WindowsSDK.10.0.26100'
    }
    return $best.Path
}

function Get-ArtifactSigningDlib {
    param([Parameter(Mandatory)] [string] $ToolsDir)

    $existing = Get-ChildItem $ToolsDir -Recurse -Filter 'Azure.CodeSigning.Dlib.dll' -ErrorAction SilentlyContinue |
                Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
    if ($existing) { return $existing.FullName }

    Write-Host '    Artifact Signing-client (Microsoft.ArtifactSigning.Client) downloaden van NuGet…'
    New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
    $pkg = Join-Path $ToolsDir 'artifactsigning.client.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://www.nuget.org/api/v2/package/Microsoft.ArtifactSigning.Client' -OutFile $pkg -UseBasicParsing
    $dest = Join-Path $ToolsDir 'Microsoft.ArtifactSigning.Client'
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Expand-Archive -Path $pkg -DestinationPath $dest -Force
    Remove-Item $pkg -Force

    $dlib = Get-ChildItem $dest -Recurse -Filter 'Azure.CodeSigning.Dlib.dll' |
            Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1
    if (-not $dlib) { throw 'Azure.CodeSigning.Dlib.dll (x64) niet gevonden in het NuGet-pakket.' }
    return $dlib.FullName
}

function Invoke-CodeSigning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string[]] $Files,
        [string] $CertThumbprint,
        [string] $ArtifactSigningMetadata,
        [string] $Description = 'Purview PDF Viewer',
        [string] $ToolsDir = (Join-Path $PSScriptRoot 'tools')
    )

    if (-not $CertThumbprint -and -not $ArtifactSigningMetadata) {
        throw 'Geef -CertThumbprint of -ArtifactSigningMetadata op.'
    }

    $signtool = Find-SignTool
    $targets = @($Files | Where-Object { Test-Path $_ } | ForEach-Object { (Resolve-Path $_).Path })
    if ($targets.Count -eq 0) { throw 'Geen bestanden om te ondertekenen.' }

    if ($ArtifactSigningMetadata) {
        $metadataPath = (Resolve-Path $ArtifactSigningMetadata).Path
        $dlib = Get-ArtifactSigningDlib -ToolsDir $ToolsDir
        $signArgs = @('sign', '/v', '/fd', 'SHA256',
                  '/tr', 'http://timestamp.acs.microsoft.com', '/td', 'SHA256',
                  '/d', $Description,
                  '/dlib', $dlib, '/dmdf', $metadataPath) + $targets
    }
    else {
        $signArgs = @('sign', '/v', '/fd', 'SHA256', '/sha1', $CertThumbprint,
                  '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256',
                  '/d', $Description) + $targets
    }

    & $signtool @signArgs
    if ($LASTEXITCODE -ne 0) { throw "Ondertekenen is mislukt (signtool exitcode $LASTEXITCODE)." }

    # Controle: handtekening + tijdstempel volgens het standaard Windows-beleid.
    & $signtool verify /pa /tw $targets | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Verificatie van de handtekening is mislukt.' }
    foreach ($t in $targets) { Write-Host "    ondertekend: $(Split-Path $t -Leaf)" -ForegroundColor Green }
}

Export-ModuleMember -Function Invoke-CodeSigning, Find-SignTool
