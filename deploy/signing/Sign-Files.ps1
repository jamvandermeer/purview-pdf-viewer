<#
    Kleine wrapper rond CodeSigning.psm1, aangeroepen vanuit de WiX-build (setup.exe).
      -Mode ArtifactSigning  -Value <pad naar metadata.json>
      -Mode Certificate      -Value <thumbprint>
#>
param(
    [Parameter(Mandatory)] [ValidateSet('ArtifactSigning', 'Certificate')] [string] $Mode,
    [Parameter(Mandatory)] [string] $Value,
    [Parameter(Mandatory)] [string[]] $Files
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CodeSigning.psm1') -Force

try {
    if ($Mode -eq 'ArtifactSigning') {
        Invoke-CodeSigning -Files $Files -ArtifactSigningMetadata $Value
    }
    else {
        Invoke-CodeSigning -Files $Files -CertThumbprint $Value
    }
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
