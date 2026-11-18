<#
.SYNOPSIS
    Maakt (of werkt bij) de Entra app-registratie voor Purview PDF Viewer in een tenant
    en verleent tenant-brede admin consent.

.DESCRIPTION
    Het script:
      1. Meldt je aan bij de doeltenant (Microsoft Graph PowerShell).
      2. Zoekt de benodigde Microsoft-services op in de tenant en leest daar de permissie-ID's uit
         (geen hardcoded GUID's van permissies).
      3. Maakt de app-registratie aan, of werkt een bestaande met dezelfde naam bij:
           - single-tenant (alleen deze organisatie)
           - public client met redirect-URI's voor de Windows-broker en http://localhost
           - delegated rechten: Graph User.Read, Azure RMS user_impersonation,
             MIP Sync Service UnifiedPolicy.User.Read
      4. Maakt de Enterprise Application (service principal) aan.
      5. Verleent admin consent voor de hele organisatie.
      6. Optioneel: beperkt het gebruik tot één groep (bijv. een pilotgroep).
      7. Toont de ClientId/TenantId en de build-opdracht voor het Intune-pakket,
         en slaat die op in een JSON-bestand.

    Het script is idempotent: opnieuw uitvoeren werkt de bestaande registratie bij.

    Vereist rol: Global Administrator of Cloud Application Administrator in de doeltenant.
    Vereist module: Microsoft.Graph (Install-Module Microsoft.Graph -Scope CurrentUser).

.PARAMETER TenantId
    Tenantdomein of -ID van de klant, bijv. contoso.onmicrosoft.com of contoso.nl.

.PARAMETER DisplayName
    Naam van de app-registratie. Standaard "Purview PDF Viewer".

.PARAMETER AllowedGroupId
    Object-ID van een Entra-groep. Indien opgegeven kunnen alleen leden van deze groep
    zich aanmelden bij de app ("Assignment required").

.PARAMETER Notes
    Notitie op de app-registratie (bijv. beheerder/contact).

.EXAMPLE
    .\New-PurviewPdfViewerAppRegistration.ps1 -TenantId contoso.nl

.EXAMPLE
    .\New-PurviewPdfViewerAppRegistration.ps1 -TenantId contoso.nl -AllowedGroupId 9f1c...-... -Notes "Beheer: Adoptify"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [string] $DisplayName = 'Purview PDF Viewer',
    [ValidatePattern('^$|^[0-9a-fA-F-]{36}$')] [string] $AllowedGroupId = '',
    [string] $Notes = 'Purview PDF Viewer: PDF-viewer met Microsoft Purview-gevoeligheidslabels (MIP SDK).'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "    $text" -ForegroundColor Green }

# ── Benodigde services en delegated rechten ──────────────────────────────────
# AppId's van de Microsoft first-party services. Als een AppId niet gevonden wordt,
# zoekt het script terug op weergavenaam.
$requiredApis = @(
    [pscustomobject]@{
        Label        = 'Microsoft Graph'
        AppId        = '00000003-0000-0000-c000-000000000000'
        DisplayNames = @('Microsoft Graph')
        Scope        = 'User.Read'
    },
    [pscustomobject]@{
        Label        = 'Azure Rights Management Services'
        AppId        = '00000012-0000-0000-c000-000000000000'
        DisplayNames = @('Microsoft Rights Management Services', 'Azure Rights Management Services')
        Scope        = 'user_impersonation'
    },
    [pscustomobject]@{
        Label        = 'Microsoft Information Protection Sync Service'
        AppId        = '870c4f2e-85b6-4d43-bdda-6ed9a579b725'
        DisplayNames = @('Microsoft Information Protection Sync Service', 'Microsoft Purview Information Protection Sync Service')
        Scope        = 'UnifiedPolicy.User.Read'
    }
)

# ── Module en aanmelden ──────────────────────────────────────────────────────
Write-Step 'Microsoft Graph PowerShell laden'
foreach ($module in 'Microsoft.Graph.Authentication', 'Microsoft.Graph.Applications', 'Microsoft.Graph.Identity.SignIns') {
    if (-not (Get-Module -ListAvailable -Name $module)) {
        throw "Module $module ontbreekt. Installeer met: Install-Module Microsoft.Graph -Scope CurrentUser"
    }
    Import-Module $module -ErrorAction Stop
}

$scopes = @('Application.ReadWrite.All', 'DelegatedPermissionGrant.ReadWrite.All')
if ($AllowedGroupId) { $scopes += 'AppRoleAssignment.ReadWrite.All' }

Write-Step "Aanmelden bij tenant $TenantId"
Connect-MgGraph -TenantId $TenantId -Scopes $scopes -NoWelcome
$context = Get-MgContext
if (-not $context -or -not $context.TenantId) { throw 'Aanmelden is mislukt.' }
Write-Ok "Aangemeld als $($context.Account) in tenant $($context.TenantId)"

# ── Service principals van de Microsoft-services opzoeken ────────────────────
function Get-ResourceServicePrincipal($api) {
    $sp = Get-MgServicePrincipal -Filter "appId eq '$($api.AppId)'" -All | Select-Object -First 1
    if (-not $sp) {
        foreach ($name in $api.DisplayNames) {
            $sp = Get-MgServicePrincipal -Filter "displayName eq '$name'" -All | Select-Object -First 1
            if ($sp) { break }
        }
    }
    if (-not $sp) {
        # Sommige first-party services hebben pas een service principal na eerste gebruik.
        Write-Host "    $($api.Label) bestaat nog niet in deze tenant; wordt aangemaakt…" -ForegroundColor Yellow
        try {
            $sp = New-MgServicePrincipal -BodyParameter @{ appId = $api.AppId }
        }
        catch {
            throw "Kon de service principal voor '$($api.Label)' niet vinden of aanmaken. " +
                  "Controleer of Azure Information Protection / Purview-labels in deze tenant actief is. Details: $($_.Exception.Message)"
        }
    }
    return $sp
}

Write-Step 'Benodigde Microsoft-services en rechten opzoeken'
$resolved = foreach ($api in $requiredApis) {
    $sp = Get-ResourceServicePrincipal $api
    $scope = $sp.Oauth2PermissionScopes | Where-Object { $_.Value -eq $api.Scope } | Select-Object -First 1
    if (-not $scope) {
        $available = ($sp.Oauth2PermissionScopes | ForEach-Object Value) -join ', '
        throw "Recht '$($api.Scope)' niet gevonden op '$($sp.DisplayName)'. Beschikbaar: $available"
    }
    Write-Ok ("{0,-48} {1}" -f $sp.DisplayName, $api.Scope)
    [pscustomobject]@{
        Label        = $api.Label
        ResourceSp   = $sp
        ScopeName    = $api.Scope
        ScopeId      = $scope.Id
    }
}

$requiredResourceAccess = @(
    foreach ($r in $resolved) {
        @{
            resourceAppId  = $r.ResourceSp.AppId
            resourceAccess = @(@{ id = $r.ScopeId; type = 'Scope' })
        }
    }
)

# ── App-registratie aanmaken of bijwerken ────────────────────────────────────
Write-Step "App-registratie '$DisplayName'"
$escapedName = $DisplayName.Replace("'", "''")
$existing = @(Get-MgApplication -Filter "displayName eq '$escapedName'" -All)
if ($existing.Count -gt 1) {
    throw "Er zijn $($existing.Count) app-registraties met de naam '$DisplayName'. Ruim dubbele op of gebruik -DisplayName."
}

if ($existing.Count -eq 1) {
    $app = $existing[0]
    Update-MgApplication -ApplicationId $app.Id -BodyParameter @{
        requiredResourceAccess = $requiredResourceAccess
        notes                  = $Notes
    }
    Write-Ok "Bestaande registratie bijgewerkt (ClientId $($app.AppId))"
}
else {
    $app = New-MgApplication -BodyParameter @{
        displayName            = $DisplayName
        signInAudience         = 'AzureADMyOrg'
        notes                  = $Notes
        tags                   = @('PurviewPdfViewer')
        requiredResourceAccess = $requiredResourceAccess
    }
    Write-Ok "Aangemaakt (ClientId $($app.AppId))"
}

# Redirect-URI's: Windows-broker (WAM) bevat de ClientId, daarom na het aanmaken.
$redirectUris = @(
    "ms-appx-web://microsoft.aad.brokerplugin/$($app.AppId)",
    'http://localhost'
)
$current = @()
if ($app.PublicClient -and $app.PublicClient.RedirectUris) { $current = @($app.PublicClient.RedirectUris) }
$merged = @($current + $redirectUris | Select-Object -Unique)
Update-MgApplication -ApplicationId $app.Id -BodyParameter @{
    publicClient = @{ redirectUris = $merged }
}
Write-Ok 'Redirect-URI''s ingesteld (Windows-broker + http://localhost)'

# ── Enterprise Application (service principal) ──────────────────────────────
Write-Step 'Enterprise Application'
$sp = $null
for ($i = 0; $i -lt 6 -and -not $sp; $i++) {
    $sp = Get-MgServicePrincipal -Filter "appId eq '$($app.AppId)'" | Select-Object -First 1
    if (-not $sp) {
        try {
            $sp = New-MgServicePrincipal -BodyParameter @{ appId = $app.AppId; tags = @('WindowsAzureActiveDirectoryIntegratedApp') }
        }
        catch {
            # Net aangemaakte app is soms nog niet gerepliceerd; even wachten en opnieuw.
            Start-Sleep -Seconds 5
        }
    }
}
if (-not $sp) { throw 'Kon de Enterprise Application niet aanmaken. Voer het script over een minuut opnieuw uit.' }
Write-Ok "Service principal $($sp.Id)"

# ── Admin consent (tenant-breed, delegated) ──────────────────────────────────
Write-Step 'Admin consent verlenen voor de hele organisatie'
$existingGrants = @(Get-MgOauth2PermissionGrant -Filter "clientId eq '$($sp.Id)'" -All)
foreach ($r in $resolved) {
    $resourceId = $r.ResourceSp.Id
    $grant = $existingGrants |
             Where-Object { $_.ResourceId -eq $resourceId -and $_.ConsentType -eq 'AllPrincipals' } |
             Select-Object -First 1

    if ($grant) {
        $scopesNow = @($grant.Scope -split ' ' | Where-Object { $_ })
        if ($scopesNow -notcontains $r.ScopeName) {
            $newScope = (@($scopesNow) + $r.ScopeName) -join ' '
            Update-MgOauth2PermissionGrant -OAuth2PermissionGrantId $grant.Id -BodyParameter @{ scope = $newScope }
        }
    }
    else {
        New-MgOauth2PermissionGrant -BodyParameter @{
            clientId    = $sp.Id
            consentType = 'AllPrincipals'
            resourceId  = $resourceId
            scope       = $r.ScopeName
        } | Out-Null
    }
    Write-Ok ("{0,-48} {1}" -f $r.ResourceSp.DisplayName, $r.ScopeName)
}

# ── Optioneel: alleen toegewezen groep ───────────────────────────────────────
if ($AllowedGroupId) {
    Write-Step 'Toegang beperken tot groep'
    Update-MgServicePrincipal -ServicePrincipalId $sp.Id -BodyParameter @{ appRoleAssignmentRequired = $true }

    $already = Get-MgServicePrincipalAppRoleAssignedTo -ServicePrincipalId $sp.Id -All |
               Where-Object { $_.PrincipalId -eq $AllowedGroupId }
    if (-not $already) {
        New-MgServicePrincipalAppRoleAssignedTo -ServicePrincipalId $sp.Id -BodyParameter @{
            principalId = $AllowedGroupId
            resourceId  = $sp.Id
            appRoleId   = [guid]::Empty.ToString()   # standaardtoegang
        } | Out-Null
    }
    Write-Ok "Alleen leden van groep $AllowedGroupId kunnen de app gebruiken"
}

# ── Resultaat ────────────────────────────────────────────────────────────────
$result = [ordered]@{
    TenantId       = $context.TenantId
    TenantInput    = $TenantId
    DisplayName    = $DisplayName
    ClientId       = $app.AppId
    ObjectId       = $app.Id
    ServicePrincipalId = $sp.Id
    AllowedGroupId = $AllowedGroupId
    CreatedBy      = $context.Account
    CreatedAt      = (Get-Date).ToString('s')
}
$safeName   = ($TenantId -replace '[^\w\.-]', '_')
$resultFile = Join-Path $PSScriptRoot "$safeName-PurviewPdfViewer.json"
$result | ConvertTo-Json | Set-Content -Path $resultFile -Encoding UTF8

Write-Host "`nKlaar." -ForegroundColor Green
Write-Host "  ClientId : $($app.AppId)"
Write-Host "  TenantId : $($context.TenantId)"
Write-Host "  Opgeslagen in: $resultFile"
Write-Host "`nIntune-pakket bouwen voor deze klant:" -ForegroundColor Cyan
Write-Host "  cd ..\intune"
Write-Host "  .\Build-IntunePackage.ps1 -Version 1.0.0 -ClientId $($app.AppId) -TenantId $($context.TenantId)"

Disconnect-MgGraph | Out-Null
