# Ondertekend MSI-installatiepakket

Met `Build-Msi.ps1` maak je één ondertekend MSI voor Purview PDF Viewer. Dat ene MSI gebruik je bij elke organisatie: de tenantgegevens geef je pas bij installatie mee.

```
msiexec /i PurviewPdfViewer-1.0.0.msi CLIENTID=<client-id> TENANTID=<tenant> /qn
```

## Waarom een MSI en geen setup.exe

- **Geen extra prerequisites nodig.** De app is self-contained, dus een setup.exe (bootstrapper) voegt niets toe.
- **Standaardformaat voor beheer.** Intune, Configuration Manager, GPO en elke beheertool begrijpen een MSI.
- **Geen PowerShell bij installatie.** Er draait geen installatiescript, dus een `AllSigned`-beleid voor PowerShell speelt niet.
- **Upgrades, reparatie en verwijderen werken vanzelf.** Er komen geen detectiescripts aan te pas.

Dubbelklikken op het MSI werkt gewoon, net als bij een setup.exe. Zonder `CLIENTID` installeert de app ook. Hij toont dan dat hij nog geconfigureerd moet worden.

## 1. Code signing regelen (eenmalig)

| Optie | Kosten (indicatie) | Opmerkingen |
|---|---|---|
| **Azure Artifact Signing** (voorheen Trusted Signing), aanbevolen | ca. $9,99 per maand (Basic) | Beschikbaar voor organisaties in de EU. Microsoft valideert eerst de organisatie (een paar werkdagen). Geen USB-token nodig: de sleutel staat in Azure, en het werkt ook in een build-pipeline. |
| OV-certificaat (DigiCert, Sectigo, GlobalSign) | ca. $150–300 per jaar | De sleutel moet op een hardwaretoken of HSM staan. |
| EV-certificaat | $400+ per jaar | Geeft sinds 2024 geen voordeel meer bij SmartScreen, dus niet nodig. |

### Azure Artifact Signing instellen

1. Maak in Azure een resource **Artifact Signing account** aan, bijvoorbeeld in regio West Europe.
2. Start een **Identity validation** voor *Public trust* op Adoptify Technology B.V. Wacht tot de validatie is goedgekeurd.
3. Maak een **Certificate profile** aan van het type *Public Trust*.
4. Geef je eigen account de rol **Artifact Signing Certificate Profile Signer** op het account.
5. Kopieer `..\signing\metadata.example.json` naar `..\signing\metadata.json` en vul de accountnaam en de profielnaam in. Gebruik als endpoint het adres van jouw regio, bijvoorbeeld `https://weu.codesigning.azure.net` voor West Europe.
6. Meld je aan met `az login`. Je kunt ook aangemeld zijn in Visual Studio.

### Windows SDK (signtool)

```powershell
winget install Microsoft.WindowsSDK.10.0.26100
```

Het script zoekt `signtool.exe` zelf op. De Artifact Signing-client downloadt het de eerste keer automatisch van NuGet.

## 2. MSI bouwen

```powershell
cd deploy\msi
.\Build-Msi.ps1 -Version 1.0.0 -AcceptWixOsmfEula -ArtifactSigningMetadata ..\signing\metadata.json
```

Met een certificaat gebruik je `-CertThumbprint <thumbprint>` in plaats van `-ArtifactSigningMetadata`. Voor een testbuild zonder ondertekening gebruik je `-SkipSigning`.

Het script doet het volgende:
1. Het publiceert de app self-contained.
2. Het ondertekent `PurviewPdfViewer.exe` en `PurviewPdfViewer.dll`. De Microsoft-DLL's zijn al door Microsoft ondertekend.
3. Het bouwt het MSI met WiX Toolset v7. Die komt via NuGet binnen, dus je hoeft niets te installeren.
4. Het ondertekent het MSI en controleert de handtekening met `signtool verify /pa`.

Het resultaat staat in `out\PurviewPdfViewer-<versie>.msi`. Het script toont ook de ProductCode.

**Bij elke nieuwe release verhoog je het versienummer**, bijvoorbeeld van 1.0.0 naar 1.0.1. Het MSI vervangt dan automatisch de oude versie. Let op: Windows Installer kijkt alleen naar de eerste drie delen van het versienummer.

### WiX-licentie (OSMF)

WiX Toolset v7 is open source, maar valt onder de **Open Source Maintenance Fee EULA**. Organisaties met meer dan US$10.000 jaaromzet die WiX gebruiken, betalen een kleine onderhoudsbijdrage via GitHub Sponsors. Daar valt Adoptify onder. Lees eerst https://wixtoolset.org/osmf/ en geef daarna `-AcceptWixOsmfEula` mee. Het script geeft die acceptatie door aan WiX met `AcceptEula=wix7`.

## 3. Installeren

| Situatie | Opdracht |
|---|---|
| Stil installeren | `msiexec /i PurviewPdfViewer-1.0.0.msi CLIENTID=<id> TENANTID=<tenant> /qn` |
| Met logbestand | voeg toe: `/l*v "%TEMP%\PurviewPdfViewer.log"` |
| Upgrade | `msiexec /i PurviewPdfViewer-1.0.1.msi /qn`. CLIENTID en TENANTID blijven bewaard. |
| Verwijderen | `msiexec /x {ProductCode} /qn` of via Apps en onderdelen |

`CLIENTID` en `TENANTID` komen in `HKLM\SOFTWARE\PurviewPdfViewer`. Die waarden blijven staan bij upgrades, reparatie en verwijderen, zodat een herinstallatie zonder `CLIENTID` gewoon werkt. Helemaal opruimen gaat met `Remove-Item HKLM:\SOFTWARE\PurviewPdfViewer -Recurse`. Wil je centraal kunnen overschrijven, bijvoorbeeld via Intune of GPO, zet de waarden `ClientId` en `TenantId` dan in `HKLM\SOFTWARE\Policies\PurviewPdfViewer`. Die gaan altijd voor.

## 4. Uitrollen via Intune

De aanbevolen manier is het MSI als **Win32-app** uitrollen. Dat geeft de meeste controle en ondersteunt supersedence.

1. Verpak het MSI:
   ```powershell
   IntuneWinAppUtil.exe -c .\out -s PurviewPdfViewer-1.0.0.msi -o .\intune
   ```
2. Ga naar **Intune → Apps → Windows → Toevoegen → Windows-app (Win32)**.
   - **Installatieopdracht:** `msiexec /i "PurviewPdfViewer-1.0.0.msi" CLIENTID=<id> TENANTID=<tenant> /qn`
   - **Verwijderopdracht:** wordt automatisch ingevuld (`msiexec /x {ProductCode} /qn`).
   - **Installatiegedrag:** Systeem.
   - **Detectieregel:** *MSI*. De ProductCode wordt automatisch ingevuld; vink de versiecontrole aan.
   - **Vereisten:** x64, Windows 10 2004 of nieuwer.

Je kunt het MSI ook als **Line-of-business-app** uitrollen. Dat is eenvoudiger: zet de CLIENTID en TENANTID bij *Opdrachtregelargumenten*. Supersedence en vereisten worden dan niet ondersteund.

Deze route vervangt het PowerShell-pakket in `..\intune`. Gebruik niet allebei tegelijk.

## Wat "trusted" in de praktijk oplevert

- **UAC-melding:** toont *Geverifieerde uitgever: Adoptify Technology B.V.* in plaats van *Onbekend*.
- **AppLocker en WDAC:** klanten kunnen de app toestaan op basis van uitgever, zonder losse hash-regels per versie.
- **Integriteit:** een gewijzigd bestand verliest zijn handtekening, dus manipulatie valt op.
- **SmartScreen:** speelt alleen bij handmatig gedownloade bestanden. Bij uitrol via Intune of SCCM verschijnt er geen SmartScreen-melding. Bij handmatige downloads bouwt de reputatie zich op met het aantal installaties. Zie je in het begin toch een melding, dan verdwijnt die naarmate er meer installaties zijn.
