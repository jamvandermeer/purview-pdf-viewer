using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.InformationProtection;

namespace PurviewPdfViewer;

/// <summary>
/// Machtigingenvenster voor labels met door de gebruiker gedefinieerde machtigingen
/// (zelfde rollen als in Word). Elke rol wordt vertaald naar Purview-gebruiksrechten.
/// </summary>
public partial class PermissionsDialog : Window
{
    // Gebruiksrechten (policy-codering) volgens Microsoft Purview / Azure Rights Management.
    private static readonly string[] ViewerRights =
        { "VIEW", "VIEWRIGHTSDATA", "OBJMODEL" };
    private static readonly string[] RestrictedEditorRights =
        { "VIEW", "VIEWRIGHTSDATA", "OBJMODEL", "EDIT", "DOCEDIT" };
    private static readonly string[] EditorRights =
        { "VIEW", "VIEWRIGHTSDATA", "OBJMODEL", "EDIT", "DOCEDIT", "PRINT", "EXTRACT" };
    private static readonly string[] OwnerRights =
        { "VIEW", "VIEWRIGHTSDATA", "OBJMODEL", "EDIT", "DOCEDIT", "PRINT", "EXTRACT", "EXPORT", "EDITRIGHTSDATA", "OWNER" };

    private static readonly Regex EmailOrDomain = new(
        @"^([^@\s;,]+@)?[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$", RegexOptions.Compiled);

    private readonly string _currentUser;

    public PermissionsDialog(string labelName, string currentUser)
    {
        InitializeComponent();
        _currentUser = currentUser;
        LabelText.Text = $"Label: {labelName}";
        OwnerBox.Text = currentUser;
        ExpireDate.SelectedDate = DateTime.Today.AddDays(30);
        ExpireDate.DisplayDateStart = DateTime.Today.AddDays(1);
        Loaded += (_, _) => ViewerBox.Focus();
    }

    /// <summary>Resultaat: per groep gebruikers de bijbehorende rechten.</summary>
    public List<UserRights> Permissions { get; } = new();
    public DateTime? ValidUntil { get; private set; }
    public bool AllowOfflineAccess { get; private set; } = true;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Permissions.Clear();

        if (!TryParse(ViewerBox, "Viewer", out var viewers) ||
            !TryParse(RestrictedEditorBox, "Beperkte editor", out var restricted) ||
            !TryParse(EditorBox, "Editor", out var editors) ||
            !TryParse(OwnerBox, "Eigenaar", out var owners))
        {
            return;
        }

        // De huidige gebruiker is altijd eigenaar; anders kan hij zijn eigen document niet meer beheren.
        if (!owners.Contains(_currentUser, StringComparer.OrdinalIgnoreCase)) owners.Insert(0, _currentUser);

        // Iemand die in meerdere rollen staat, krijgt alleen de hoogste rol.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> Unique(List<string> list) => list.Where(seen.Add).ToList();
        var ownerList = Unique(owners);
        var editorList = Unique(editors);
        var restrictedList = Unique(restricted);
        var viewerList = Unique(viewers);

        if (viewerList.Count + restrictedList.Count + editorList.Count == 0 && ownerList.Count == 1)
        {
            var answer = MessageBox.Show(this,
                "U heeft niemand anders toegang gegeven. Alleen u kunt het document dan openen. Doorgaan?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        void Add(List<string> users, string[] rights)
        {
            if (users.Count > 0) Permissions.Add(new UserRights(users, rights.ToList()));
        }
        Add(ownerList, OwnerRights);
        Add(editorList, EditorRights);
        Add(restrictedList, RestrictedEditorRights);
        Add(viewerList, ViewerRights);

        if (ExpireCheck.IsChecked == true)
        {
            if (ExpireDate.SelectedDate is not { } date || date.Date <= DateTime.Today)
            {
                MessageBox.Show(this, "Kies een vervaldatum in de toekomst.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ValidUntil = date.Date.AddDays(1).AddSeconds(-1); // tot en met de gekozen dag
        }

        AllowOfflineAccess = OfflineCheck.IsChecked == true;
        DialogResult = true;
    }

    private bool TryParse(System.Windows.Controls.TextBox box, string role, out List<string> result)
    {
        result = box.Text
            .Split(new[] { ';', ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        var invalid = result.Where(x => !EmailOrDomain.IsMatch(x)).ToList();
        if (invalid.Count == 0) return true;

        MessageBox.Show(this,
            $"Ongeldige invoer bij {role}: {string.Join(", ", invalid)}\n\nGebruik e-mailadressen of domeinnamen, gescheiden door een puntkomma.",
            Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        box.Focus();
        box.SelectAll();
        return false;
    }
}
