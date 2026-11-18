using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace PurviewPdfViewer;

/// <summary>
/// Instellingen van de app. Bronnen, van laag naar hoog in prioriteit:
///   1. appsettings.json naast de .exe
///   2. HKLM\SOFTWARE\PurviewPdfViewer           (geschreven door de MSI: CLIENTID / TENANTID)
///   3. HKLM\SOFTWARE\Policies\PurviewPdfViewer  (centraal beheer via Intune/GPO; overschrijft alles)
/// Lege waarden in een hogere bron overschrijven niets.
/// </summary>
public sealed class AppSettings
{
    private const string MachineKey = @"SOFTWARE\PurviewPdfViewer";
    private const string PolicyKey = @"SOFTWARE\Policies\PurviewPdfViewer";

    /// <summary>Application (client) ID van de Entra app-registratie.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>"organizations" (multi-tenant) of een tenant-ID / domein (single-tenant).</summary>
    public string TenantId { get; set; } = "organizations";

    /// <summary>Taal waarin labelnamen worden opgehaald (als de tenant vertalingen heeft).</summary>
    public string Locale { get; set; } = "nl-NL";

    public static AppSettings Load()
    {
        var settings = LoadJson() ?? new AppSettings();
        ApplyRegistry(settings, MachineKey);
        ApplyRegistry(settings, PolicyKey);
        if (string.IsNullOrWhiteSpace(settings.TenantId)) settings.TenantId = "organizations";
        if (string.IsNullOrWhiteSpace(settings.Locale)) settings.Locale = "nl-NL";
        return settings;
    }

    private static AppSettings? LoadJson()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch
        {
            return null; // Kapotte JSON mag de app niet laten crashen; register/policy kan nog aanvullen.
        }
    }

    private static void ApplyRegistry(AppSettings settings, string subKey)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(subKey);
            if (key == null) return;

            if (key.GetValue("ClientId") is string clientId && !string.IsNullOrWhiteSpace(clientId))
                settings.ClientId = clientId.Trim();
            if (key.GetValue("TenantId") is string tenantId && !string.IsNullOrWhiteSpace(tenantId))
                settings.TenantId = tenantId.Trim();
            if (key.GetValue("Locale") is string locale && !string.IsNullOrWhiteSpace(locale))
                settings.Locale = locale.Trim();
        }
        catch
        {
            // Geen leesrechten of geen register: negeren.
        }
    }
}
