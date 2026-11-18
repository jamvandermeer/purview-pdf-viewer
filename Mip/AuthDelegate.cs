using System.Windows.Threading;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.InformationProtection;

namespace PurviewPdfViewer.Mip;

/// <summary>
/// Levert OAuth2-tokens aan de MIP SDK. De SDK roept AcquireToken aan (op een achtergrondthread)
/// voor de Rights Management-service en de MIP Sync-service.
/// Aanmelden loopt via de Windows-broker (WAM): single sign-on met het Windows-account en
/// ondersteuning voor Conditional Access (compliant device, MFA, enz.).
/// </summary>
internal sealed class AuthDelegate : IAuthDelegate
{
    private const string GraphScope = "https://graph.microsoft.com/User.Read";

    private readonly IPublicClientApplication _app;
    private readonly Dispatcher _uiDispatcher;

    public AuthDelegate(string clientId, string tenantId, Func<IntPtr> parentWindow, Dispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
        _app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, string.IsNullOrWhiteSpace(tenantId) ? "organizations" : tenantId)
            .WithRedirectUri("http://localhost") // terugval als de broker niet beschikbaar is
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "Purview PDF Viewer"
            })
            .WithParentActivityOrWindow(parentWindow)
            .Build();
    }

    /// <summary>Meldt de gebruiker aan (stil indien mogelijk) en geeft de UPN terug.</summary>
    public async Task<string> SignInAsync()
    {
        var result = await AcquireAsync(new[] { GraphScope }, preferredUser: null, authority: null, claims: null);
        return result.Account.Username;
    }

    /// <summary>Verwijdert de accounts die deze app kent (WAM-sessie van Windows blijft bestaan).</summary>
    public async Task SignOutAsync()
    {
        foreach (var account in await _app.GetAccountsAsync())
        {
            await _app.RemoveAsync(account);
        }
    }

    /// <summary>Aanroep door de MIP SDK. Synchroon contract; wordt nooit op de UI-thread aangeroepen.</summary>
    public string AcquireToken(Identity identity, string authority, string resource, string claims)
    {
        var scope = resource.EndsWith('/') ? resource + ".default" : resource + "/.default";
        return AcquireAsync(new[] { scope }, identity?.Email, authority, claims)
            .GetAwaiter().GetResult()
            .AccessToken;
    }

    private async Task<AuthenticationResult> AcquireAsync(string[] scopes, string? preferredUser, string? authority, string? claims)
    {
        var accounts = (await _app.GetAccountsAsync().ConfigureAwait(false)).ToList();
        IAccount account =
            accounts.FirstOrDefault(a => preferredUser != null &&
                                         string.Equals(a.Username, preferredUser, StringComparison.OrdinalIgnoreCase))
            ?? accounts.FirstOrDefault()
            ?? PublicClientApplication.OperatingSystemAccount;

        var tenantAuthority = ToTenantAuthority(authority);

        try
        {
            var silent = _app.AcquireTokenSilent(scopes, account);
            if (!string.IsNullOrEmpty(claims)) silent = silent.WithClaims(claims);
            if (tenantAuthority != null) silent = silent.WithTenantIdFromAuthority(tenantAuthority);
            return await silent.ExecuteAsync().ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            // Interactief aanmelden moet vanaf de UI-thread starten (vensterhandle van het hoofdvenster).
            Task<AuthenticationResult> interactive = _uiDispatcher.Invoke(() =>
            {
                var builder = _app.AcquireTokenInteractive(scopes)
                    .WithPrompt(Prompt.SelectAccount);
                if (account != PublicClientApplication.OperatingSystemAccount) builder = builder.WithAccount(account);
                if (!string.IsNullOrEmpty(claims)) builder = builder.WithClaims(claims);
                if (tenantAuthority != null) builder = builder.WithTenantIdFromAuthority(tenantAuthority);
                return builder.ExecuteAsync();
            });
            return await interactive.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// De SDK geeft een authority mee uit de 401-challenge. Bevat die een specifieke tenant
    /// (bijv. bij een document van een andere organisatie), dan vragen we het token voor die tenant aan.
    /// </summary>
    private static Uri? ToTenantAuthority(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority)) return null;
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri)) return null;

        var tenant = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault() ?? "";
        if (tenant.Length == 0 ||
            tenant.Equals("common", StringComparison.OrdinalIgnoreCase) ||
            tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return new Uri($"https://login.microsoftonline.com/{tenant}");
    }
}

/// <summary>Toestemming voor het verbinden met de Microsoft-services van de tenant.</summary>
internal sealed class ConsentDelegate : IConsentDelegate
{
    public Consent GetUserConsent(string url) => Consent.Accept;
}
