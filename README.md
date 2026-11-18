# Purview PDF Viewer

> **Disclaimer** – Deze oplossing wordt door Adoptify Technology B.V. geleverd *zoals deze is* (“as is”), zonder garantie, ondersteuning, onderhoud of updates. Adoptify aanvaardt geen aansprakelijkheid voor de werking of voor schade door het gebruik ervan. Verdere verspreiding is alleen toegestaan met voorafgaande schriftelijke toestemming van Adoptify. Zie [LICENSE.txt](LICENSE.txt) voor de volledige voorwaarden.

Een Windows-PDF-viewer met Microsoft Purview-gevoeligheidslabels: bekijken, label toekennen, wijzigen en verwijderen, versleutelde PDF's openen — vergelijkbaar met de labelfunctie in Adobe Acrobat Pro.

Gebouwd op de **Microsoft Information Protection (MIP) SDK**, dezelfde SDK die Acrobat gebruikt. Labels die je hier toepast zijn dus gewone Purview-labels: Acrobat Reader, Edge, Office en Purview (Activity explorer, DLP, eDiscovery) herkennen ze.

## Wat de app doet

| Functie | Details |
|---|---|
| PDF bekijken | Pagina's renderen met de ingebouwde PDF-engine van Windows, zoom, paginabreedte, Ctrl+scrollwiel |
| Labelbalk | Huidig label met labelkleur, versleutelingsstatus, eigenaar en jouw rechten |
| Label toekennen / wijzigen | Menu *Gevoeligheid* toont het labelbeleid van de gebruiker, inclusief sublabels en tooltips |
| Zelf machtigingen instellen | Bij labels met door de gebruiker gedefinieerde machtigingen: venster zoals in Word (Viewer, Beperkte editor, Editor, Eigenaar, vervaldatum) |
| Label verwijderen | Geblokkeerd als je organisatie verplicht labelen heeft ingesteld |
| Reden bij verlagen | Vraagt om een reden als het beleid dat eist; de reden komt in het auditlog |
| Versleutelde PDF's | Ontsleuteling alleen in het geheugen; er komt geen leesbare kopie op schijf |
| Rechten afdwingen | Afdrukken alleen met afdrukrecht; zonder kopieerrecht blokkeert de app schermafbeeldingen en schermdelen |
| Audit | Na opslaan stuurt de app een audit-event naar Purview |
| Overig | Opslaan, Opslaan als, afdrukken, slepen en neerzetten, "Openen met" |

## 1. Entra app-registratie (eenmalig per tenant)

> **Snelste route:** draai `deploy\entra\New-PurviewPdfViewerAppRegistration.ps1 -TenantId <tenant>`. Dat script voert onderstaande stappen inclusief admin consent automatisch uit (zie `deploy\entra\README-Entra.md`). Handmatig gaat het zo:

1. Ga naar **Entra admin center → App registrations → New registration**.
   - Naam: `Purview PDF Viewer`.
   - Supported account types: *Accounts in this organizational directory only*. Werk je in meerdere klanttenants, kies dan *Multitenant* en geef in elke tenant admin consent.
2. Ga naar **Authentication → Add a platform → Mobile and desktop applications** en voeg twee redirect URI's toe:
   - `ms-appx-web://microsoft.aad.brokerplugin/<CLIENT_ID>`
   - `http://localhost`
3. Ga naar **API permissions → Add a permission** en voeg deze *delegated* rechten toe:
   - **Microsoft Graph** → `User.Read`
   - **Azure Rights Management Services** (tabblad *APIs my organization uses*) → `user_impersonation`
   - **Microsoft Information Protection Sync Service** (tabblad *APIs my organization uses*) → `UnifiedPolicy.User.Read`
4. Klik op **Grant admin consent**.
5. Zet de *Application (client) ID* in `appsettings.json`:

```json
{
  "ClientId": "00000000-0000-0000-0000-000000000000",
  "TenantId": "organizations",
  "Locale": "nl-NL"
}
```

Voor TenantId gebruik je `organizations` bij een multitenant-app, of het tenant-ID of domein bij single-tenant.

Aanmelden loopt via de Windows-broker (WAM). Daardoor werken single sign-on met het Windows-account en Conditional Access (compliant device, MFA) gewoon. Staat de app achter een CA-policy voor "alle cloud-apps", dan geldt die policy ook hier.

## 2. Bouwen

Je hebt nodig:
- Windows 10 (2004) of Windows 11
- Visual Studio 2022 met de workload **.NET desktop development**, of alleen de .NET 8 SDK

```powershell
cd PurviewPdfViewer
dotnet build -c Release
# of een map om te verspreiden:
dotnet publish -c Release -r win-x64 --self-contained false -o .\publish
```

Start daarna `PurviewPdfViewer.exe`. NuGet haalt de MIP SDK (`Microsoft.InformationProtection.File`) en MSAL automatisch op. Het project gebruikt de nieuwste 1.x-versie van de MIP SDK.

Wil je de app als standaard-PDF-viewer instellen? Ga dan in Verkenner naar een PDF, kies *Openen met → Andere app kiezen* en selecteer `PurviewPdfViewer.exe`.

## 3. Testen

1. Open een PDF zonder label. Kies **Gevoeligheid → [label zonder versleuteling]** en klik **Opslaan**. Open het bestand daarna in Acrobat Reader of Edge: het label hoort zichtbaar te zijn.
2. Pas een label **met versleuteling** toe en sla op. Open het bestand als een andere gebruiker: die krijgt alleen de rechten uit het label.
3. Verlaag een label, bijvoorbeeld van *Vertrouwelijk* naar *Openbaar*. De app vraagt om een reden. Controleer daarna in Purview → **Activity explorer** het event *Label downgraded*.
4. Open een versleutelde PDF waarop je alleen leesrechten hebt. De afdrukknop hoort dan uitgeschakeld te zijn en een schermafbeelding van het venster hoort zwart te zijn.

## Uitrollen

| Route | Map | Wanneer |
|---|---|---|
| **Ondertekende setup.exe** | `deploy\setup` | Handmatige installatie met venster; configuratie kan erin worden meegebakken. |
| **Ondertekend MSI** (aanbevolen) | `deploy\msi` | Eén MSI voor alle klanten. `CLIENTID` en `TENANTID` geef je bij installatie mee. Werkt met Intune, SCCM en GPO. |
| Intune-pakket met PowerShell | `deploy\intune` | Alternatief als je geen MSI wilt. De ClientId zit dan per klant in het pakket. |
| App-registratie per klant | `deploy\entra` | Eenmalig per tenant uitvoeren. |

De app leest zijn configuratie in deze volgorde, waarbij elke volgende bron de vorige overschrijft:
1. `appsettings.json`
2. `HKLM\SOFTWARE\PurviewPdfViewer`, geschreven door het MSI
3. `HKLM\SOFTWARE\Policies\PurviewPdfViewer`, voor centraal beheer

## Bekende beperkingen (versie 1)

- **Visuele markeringen.** Of de MIP SDK zelf koptekst, voettekst en watermerk in een PDF zet, staat niet in Microsofts documentatie. Acrobat tekent die markeringen zelf. Controleer dit bij test 1 met een label dat markeringen heeft. Ontbreken ze, dan kan die functie worden toegevoegd.
- **Geen bewerkfuncties.** Tekst selecteren of zoeken, annotaties, formulieren en handtekeningen zitten er niet in. De app is een viewer met labelen.
- **Klassieke PDF-wachtwoorden** (Adobe-beveiliging, geen Purview) worden niet ondersteund.
- **Alleen Windows.**

## Projectstructuur

```
PurviewPdfViewer/
├─ appsettings.json          ClientId / tenant / taal
├─ App.xaml(.cs)             opstart, stijlen, "Openen met"-argument
├─ MainWindow.xaml(.cs)      viewer, labelbalk, labelmenu, opslaan, afdrukken, rechten
├─ JustificationDialog.*     reden bij verlagen of verwijderen van een label
├─ Mip/
│  ├─ AuthDelegate.cs        MSAL + Windows-broker → tokens voor de MIP SDK
│  ├─ MipService.cs          MIP-context, profiel, engine, labelbeleid
│  └─ PdfSession.cs          file handler: label lezen/zetten, ontsleutelen, commit
└─ Pdf/
   └─ PdfDocumentView.cs     rendering via Windows.Data.Pdf (lazy, in geheugen)
```

## Licenties

- De MIP SDK is gratis te gebruiken.
- Gebruikers hebben dezelfde Microsoft 365-licentie nodig als voor handmatig labelen in Office, bijvoorbeeld Microsoft 365 E3/E5 of Business Premium.
