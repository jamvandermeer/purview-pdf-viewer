# Purview PDF Viewer uitrollen via Intune

## Inhoud van deze map

```
deploy\intune\
├─ Build-IntunePackage.ps1               bouwt het complete pakket
├─ Detect-PurviewPdfViewer.template.ps1  sjabloon voor de detectieregel
└─ Source\
   ├─ install.ps1                        installatie (systeemcontext)
   └─ uninstall.ps1                      verwijdering
```

## 1. Pakket bouwen

Open PowerShell in deze map en voer uit:

```powershell
.\Build-IntunePackage.ps1 -Version 1.0.0 `
    -ClientId <Application (client) ID> `
    -TenantId <tenantdomein, bijv. contoso.nl>
```

Heb je een code-signing-certificaat, voeg dan `-CertThumbprint <thumbprint>` toe.

Het resultaat komt in `.\out\`:
- **`PurviewPdfViewer-1.0.0.intunewin`**: het bestand dat je in Intune uploadt.
- **`Detect-PurviewPdfViewer.ps1`**: het detectiescript voor deze versie.

Het script publiceert de app **self-contained**, zodat medewerkers geen .NET hoeven te installeren. Het pakket is daardoor ongeveer 150–200 MB. De ClientId en TenantId zitten in het pakket. Per klant bouw je dus een eigen pakket met hun gegevens.

De eerste keer downloadt het script `IntuneWinAppUtil.exe` van Microsofts GitHub naar `.\tools\`. Is GitHub bij jullie geblokkeerd, download het bestand dan handmatig van [Microsoft-Win32-Content-Prep-Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool) en zet het in `.\tools\`.

## 2. App aanmaken in Intune

Ga naar **Intune admin center → Apps → Windows → Toevoegen → Windows-app (Win32)**.

| Tabblad | Instelling |
|---|---|
| App-informatie | Upload `PurviewPdfViewer-<versie>.intunewin`. Naam: *Purview PDF Viewer*. Uitgever: *Adoptify Technology B.V.* Versie: `<versie>` |
| Programma | **Installatieopdracht:** `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\install.ps1` |
| | **Verwijderopdracht:** `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\uninstall.ps1` |
| | Installatiegedrag: **Systeem** · Herstartgedrag: **Geen specifieke actie** |
| Vereisten | Architectuur: **x64** · Minimaal besturingssysteem: **Windows 10 2004** |
| Detectieregels | **Aangepast detectiescript gebruiken** → `out\Detect-PurviewPdfViewer.ps1`. Script als 32-bits proces uitvoeren: **Nee**. |
| Toewijzingen | Eerst een pilotgroep als **Vereist**, of **Beschikbaar** via de Bedrijfsportal |

Installatie- en verwijderlogs staan op het apparaat in
`C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\PurviewPdfViewer-*.log`.

## Wat de installatie doet

- **Programmabestanden:** de app komt in `C:\Program Files\Purview PDF Viewer`.
- **Startmenu:** er komt een snelkoppeling voor alle gebruikers.
- **Openen met:** de app verschijnt bij *Openen met* voor PDF's en is kiesbaar als standaard-app.
- **Apps en onderdelen:** de app staat in het overzicht en is daar ook te verwijderen.
- **Lopende app:** een draaiende app wordt eerst afgesloten, zodat een update niet vastloopt.

## 3. Optioneel: standaard PDF-app maken

Windows staat niet toe dat een installer zelf de standaard-app wijzigt. Dat regel je centraal:

1. Exporteer op een testapparaat waar je de app als standaard hebt gekozen de koppelingen:
   ```powershell
   Dism /Online /Export-DefaultAppAssociations:C:\Temp\DefaultApps.xml
   ```
2. Houd in die XML alleen de regel voor `.pdf` over. Die ziet er zo uit:
   ```xml
   <Association Identifier=".pdf" ProgId="PurviewPdfViewer.Document" ApplicationName="Purview PDF Viewer" />
   ```
3. Zet de XML in Base64 en rol hem uit met een Intune-configuratieprofiel (Settings catalog → *Application Defaults → Default Associations Configuration*).

Let op: deze instelling geldt voor nieuwe gebruikersprofielen en voor de volgende aanmelding. Bestaande eigen keuzes van gebruikers kunnen voorrang houden.

## 4. Updates

1. Bouw opnieuw met een hoger versienummer, bijvoorbeeld `-Version 1.0.1`.
2. Maak in Intune een nieuwe Win32-app aan met het nieuwe pakket en detectiescript.
3. Stel onder **Vervanging (supersedence)** de oude versie in, met *App verwijderen* op **Nee**. Het installatiescript overschrijft de oude versie netjes.

## Checklist voor de eerste uitrol

- [ ] De app-registratie heeft admin consent.
- [ ] Getest op een pilotapparaat: aanmelden, labels laden, label toepassen, opslaan.
- [ ] Ondertekend, of een WDAC/AppLocker-uitzondering geregeld.
- [ ] Conditional Access: de app valt onder je policies; test op een compliant apparaat.
- [ ] Gebruikers hebben een licentie voor handmatig labelen (E3/E5, Business Premium).
