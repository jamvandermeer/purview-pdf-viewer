# App-registratie aanmaken in een klanttenant

`New-PurviewPdfViewerAppRegistration.ps1` richt een tenant in één keer in voor Purview PDF Viewer. Het script:
- maakt de app-registratie aan (single-tenant, public client);
- stelt de redirect-URI's in voor de Windows-broker en `http://localhost`;
- voegt de delegated rechten toe: Graph `User.Read`, Azure RMS `user_impersonation` en MIP Sync `UnifiedPolicy.User.Read`;
- maakt de Enterprise Application aan;
- verleent admin consent voor de hele organisatie;
- beperkt de toegang optioneel tot één groep.

## Vereisten

- **Rol:** Global Administrator of Cloud Application Administrator in de klanttenant.
- **Module:** Microsoft Graph PowerShell, eenmalig te installeren:
  ```powershell
  Install-Module Microsoft.Graph -Scope CurrentUser
  ```

## Gebruik

Basisgebruik:
```powershell
.\New-PurviewPdfViewerAppRegistration.ps1 -TenantId klant.nl
```

Toegang beperken tot een pilotgroep (object-ID van een Entra-groep):
```powershell
.\New-PurviewPdfViewerAppRegistration.ps1 -TenantId klant.nl -AllowedGroupId <groep-object-id> -Notes "Beheer: Adoptify"
```

Het script toont aan het eind de **ClientId**, de **TenantId** en de opdracht om het Intune-pakket voor deze klant te bouwen. Die gegevens staan ook in `<tenant>-PurviewPdfViewer.json` naast het script.

Je kunt het script veilig opnieuw draaien. Een bestaande registratie met dezelfde naam wordt bijgewerkt, er komt geen dubbele bij.

## Werkwijze per nieuwe klant

1. Draai dit script in de klanttenant.
2. Bouw het pakket met `..\intune\Build-IntunePackage.ps1` en de getoonde ClientId en TenantId.
3. Rol het pakket uit via Intune bij de klant (zie `..\intune\README-Intune.md`).

## Problemen oplossen

| Melding | Oplossing |
|---|---|
| *Insufficient privileges* | Meld je aan met een account met de rol Global Administrator of Cloud Application Administrator. De eerste keer vraagt Graph PowerShell zelf ook om consent voor de opgevraagde scopes. |
| *Service principal … niet vinden of aanmaken* | De Azure Rights Management-service of het labelbeleid is in die tenant niet actief. Controleer in Purview of er labels zijn gepubliceerd. |
| *Er zijn N app-registraties met de naam …* | Ruim de dubbele registraties op of kies een andere naam met `-DisplayName`. |
| Gebruiker krijgt *AADSTS50105* | Toegang is beperkt tot een groep (`-AllowedGroupId`) en de gebruiker is geen lid van die groep. |
