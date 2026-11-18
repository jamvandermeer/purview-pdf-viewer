using System.IO;
using System.Windows.Threading;
using Microsoft.InformationProtection;
using Microsoft.InformationProtection.File;

namespace PurviewPdfViewer.Mip;

/// <summary>
/// Eén MIP-context, -profiel en -engine voor de aangemelde gebruiker.
/// Alle SDK-aanroepen lopen via Task.Run zodat de UI-thread nooit blokkeert.
/// </summary>
public sealed class MipService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly AuthDelegate _auth;
    private MipContext? _context;
    private IFileProfile? _profile;
    private IFileEngine? _engine;

    public MipService(AppSettings settings, Func<IntPtr> parentWindow, Dispatcher uiDispatcher)
    {
        _settings = settings;
        _auth = new AuthDelegate(settings.ClientId, settings.TenantId, parentWindow, uiDispatcher);
    }

    public string? UserEmail { get; private set; }
    public bool IsReady => _engine != null;

    public IFileEngine Engine => _engine ?? throw new InvalidOperationException("Niet aangemeld.");

    /// <summary>Labels uit het labelbeleid van de gebruiker (hiërarchisch: hoofdlabels met sublabels).</summary>
    public IReadOnlyList<Label> Labels => Engine.SensitivityLabels.ToList();

    /// <summary>Verplicht labelen volgens het labelbeleid.</summary>
    public bool IsLabelingRequired => Engine.IsLabelingRequired;

    /// <summary>Standaardlabel uit het beleid, of null.</summary>
    public Label? DefaultLabel
    {
        get
        {
            try { return Engine.DefaultSensitivityLabel; }
            catch { return null; }
        }
    }

    public string? MoreInfoUrl
    {
        get
        {
            try { return string.IsNullOrWhiteSpace(Engine.MoreInfoUrl) ? null : Engine.MoreInfoUrl; }
            catch { return null; }
        }
    }

    public Task InitializeAsync() => Task.Run(async () =>
    {
        var email = await _auth.SignInAsync().ConfigureAwait(false);

        if (_context == null)
        {
            MIP.Initialize(MipComponent.File);

            var appInfo = new ApplicationInfo
            {
                ApplicationId = _settings.ClientId,
                ApplicationName = "Purview PDF Viewer",
                ApplicationVersion = "1.0.0"
            };

            var dataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PurviewPdfViewer", "mip_data");
            Directory.CreateDirectory(dataPath);

            var config = new MipConfiguration(appInfo, dataPath, LogLevel.Warning, false, CacheStorageType.OnDiskEncrypted);
            _context = MIP.CreateMipContext(config);

            var profileSettings = new FileProfileSettings(_context, CacheStorageType.OnDiskEncrypted, new ConsentDelegate());
            _profile = await MIP.LoadFileProfileAsync(profileSettings).ConfigureAwait(false);
        }

        _engine = await AddEngineAsync(email).ConfigureAwait(false);
        UserEmail = email;

        // Labelbeleid uit de cache ouder dan een uur? Dan vers ophalen, zodat nieuwe of
        // gewijzigde labels uit Purview zichtbaar worden.
        if (IsPolicyStale(_engine, PolicyMaxAge))
        {
            await RefreshEngineAsync().ConfigureAwait(false);
        }
    });

    private static readonly TimeSpan PolicyMaxAge = TimeSpan.FromHours(1);

    /// <summary>Tijdstip (lokale tijd) waarop het labelbeleid het laatst is opgehaald.</summary>
    public DateTime? LastPolicyFetchTime
    {
        get
        {
            try { return _engine?.LastPolicyFetchTime.ToLocalTime(); }
            catch { return null; }
        }
    }

    /// <summary>Haalt het labelbeleid opnieuw op bij Purview (negeert de lokale cache).</summary>
    public Task RefreshPolicyAsync() => Task.Run(() => RefreshEngineAsync());

    private async Task RefreshEngineAsync()
    {
        if (_profile == null || UserEmail == null) return;
        var email = UserEmail;

        if (_engine != null)
        {
            var id = _engine.Settings.EngineId;
            _engine = null;
            try { await _profile.UnloadEngineAsync(id).ConfigureAwait(false); } catch { /* niet geladen */ }
            try { await _profile.DeleteEngineAsync(id).ConfigureAwait(false); } catch { /* geen cache */ }
        }

        _engine = await AddEngineAsync(email).ConfigureAwait(false);
    }

    private async Task<IFileEngine> AddEngineAsync(string email)
    {
        var engineSettings = new FileEngineSettings(email, _auth, "", _settings.Locale)
        {
            Identity = new Identity(email)
        };
        return await _profile!.AddEngineAsync(engineSettings).ConfigureAwait(false);
    }

    private static bool IsPolicyStale(IFileEngine engine, TimeSpan maxAge)
    {
        try
        {
            var fetched = engine.LastPolicyFetchTime;
            if (fetched == default) return false;
            var utc = fetched.Kind == DateTimeKind.Local ? fetched.ToUniversalTime() : fetched;
            return DateTime.UtcNow - utc > maxAge;
        }
        catch
        {
            return false;
        }
    }

    public Task SignOutAsync() => Task.Run(async () =>
    {
        if (_engine != null && _profile != null)
        {
            var id = _engine.Settings.EngineId;
            _engine = null;
            await _profile.UnloadEngineAsync(id).ConfigureAwait(false);
        }
        UserEmail = null;
        await _auth.SignOutAsync().ConfigureAwait(false);
    });

    /// <summary>Opent een PDF: leest label en beveiliging en levert (zo nodig ontsleutelde) bytes om te tonen.</summary>
    public Task<PdfSession> OpenAsync(string path) => Task.Run(() => PdfSession.OpenAsync(Engine, path));

    public void Dispose()
    {
        try
        {
            if (_engine != null && _profile != null)
            {
                _profile.UnloadEngineAsync(_engine.Settings.EngineId).GetAwaiter().GetResult();
            }
        }
        catch { /* afsluiten: negeren */ }
        _engine = null;
        _profile = null;
        _context?.ShutDown();
        _context = null;
    }
}
