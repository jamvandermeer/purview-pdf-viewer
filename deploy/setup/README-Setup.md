# Ondertekende setup.exe

Met `Build-Setup.ps1` maak je een ondertekende `PurviewPdfViewer-Setup-<versie>.exe`. Die setup.exe bevat het MSI en heeft een installatievenster. Windows vraagt zelf om beheerdersrechten, dus een gebruiker hoeft alleen te dubbelklikken.

## Bouwen

Ga eerst naar de juiste map en sta scripts toe in dit venster:
```powershell
cd deploy\setup
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
```

**Interne setup.** De configuratie voor uw organisatie zit erin, dus dubbelklikken is genoeg:
```powershell
.\Build-Setup.ps1 -Version 1.0.4 -AcceptWixOsmfEula `
    -ArtifactSigningMetadata ..\signing\metadata.json `
    -ClientId <client-id> -TenantId contoso.nl
```

**Neutrale setup voor klanten.** Er zit geen configuratie in; de klant geeft `CLIENTID` mee:
```powershell
.\Build-Setup.ps1 -Version 1.0.4 -AcceptWixOsmfEula -ArtifactSigningMetadata ..\signing\metadata.json
```

Met een certificaat gebruik je `-CertThumbprint <thumbprint>` in plaats van `-ArtifactSigningMetadata`. Om te testen zonder handtekening gebruik je `-SkipSigning`.

Het resultaat komt in `out\PurviewPdfViewer-Setup-<versie>.exe`. Het script bouwt en ondertekent eerst het MSI, en daarna de setup.exe.

### Waarom de setup.exe twee keer wordt ondertekend

Een setup.exe van WiX bestaat uit een *engine* met de pakketten eraan vastgeplakt. WiX maakt de engine eerst los en laat die ondertekenen. Daarna plakt WiX de pakketten er weer aan en wordt de complete setup.exe ondertekend. Zonder die eerste stap zou de handtekening ongeldig worden zodra de setup zichzelf uitpakt. Het script regelt dit automatisch via `..\signing\Sign-Files.ps1`.

## Gebruik

| Situatie | Opdracht |
|---|---|
| Installeren met venster | dubbelklikken |
| Stil installeren | `PurviewPdfViewer-Setup-1.0.4.exe /quiet` (voeg `CLIENTID=<id> TENANTID=<tenant>` toe als die niet in de setup zitten) |
| Stil, met log | `… /quiet /log "%TEMP%\PurviewPdfViewer-setup.log"` |
| Verwijderen | `PurviewPdfViewer-Setup-1.0.4.exe /uninstall /quiet`, of via Apps en onderdelen |
| Upgrade | de nieuwe setup.exe uitvoeren; de configuratie blijft behouden |

Een `CLIENTID` of `TENANTID` op de opdrachtregel gaat altijd voor op de waarde die in de setup zit.

## Setup.exe of MSI?

| | setup.exe | MSI |
|---|---|---|
| Handmatig installeren door gebruikers | ✅ venster en automatische UAC-melding | werkt, maar is technischer |
| Intune, SCCM of GPO | werkt als Win32-app | ✅ standaard; Intune leest de ProductCode zelf uit |
| Configuratie meebakken | ✅ `-ClientId` bij het bouwen | via de opdrachtregel of het register |

Gebruik de **setup.exe** voor handmatige installaties en downloads, en het **MSI** voor centrale uitrol. Beide zijn ondertekend en installeren precies hetzelfde.
