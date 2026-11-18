using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using PurviewPdfViewer.Mip;
using PurviewPdfViewer.Pdf;
using MipEx = Microsoft.InformationProtection.Exceptions;
using MipLabel = Microsoft.InformationProtection.Label;

namespace PurviewPdfViewer;

public partial class MainWindow : Window
{
    private static readonly double[] ZoomSteps = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0 };
    private static readonly Color NoLabelColor = Color.FromRgb(0x8A, 0x88, 0x86);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly string? _startupFile;
    private MipService? _mip;
    private PdfSession? _session;
    private PdfDocumentView? _view;
    private ScrollViewer? _scroll;
    private double _zoom = 1.0;
    private bool _closeConfirmed;

    public MainWindow(string? startupFile = null)
    {
        InitializeComponent();
        _startupFile = startupFile;
        WindowPlacement.Apply(this);

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, async (_, _) => await OpenWithDialogAsync(),
            (_, e) => e.CanExecute = _mip?.IsReady == true));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, async (_, _) => await SaveAsync(null),
            (_, e) => e.CanExecute = _session?.HasUnsavedChanges == true));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.SaveAs, async (_, _) => await SaveAsWithDialogAsync(),
            (_, e) => e.CanExecute = _session != null));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Print, async (_, _) => await PrintAsync(),
            (_, e) => e.CanExecute = _session?.CanPrint == true && _view != null));
        CommandBindings.Add(new CommandBinding(NavigationCommands.IncreaseZoom, (_, _) => StepZoom(+1),
            (_, e) => e.CanExecute = _view != null));
        CommandBindings.Add(new CommandBinding(NavigationCommands.DecreaseZoom, (_, _) => StepZoom(-1),
            (_, e) => e.CanExecute = _view != null));

        Loaded += OnLoaded;
        Closing += OnClosing;
        Drop += OnDrop;
        DragOver += OnDragOver;
        PagesView.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    // ───────────────────────────── Opstarten & aanmelden ─────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PagesView.ApplyTemplate();
        _scroll = PagesView.Template.FindName("PART_Scroll", PagesView) as ScrollViewer;
        if (_scroll != null) _scroll.ScrollChanged += (_, _) => UpdatePageInfo();

        if (string.IsNullOrWhiteSpace(_settings.ClientId))
        {
            EmptyTitle.Text = "Nog niet geconfigureerd";
            EmptySubtitle.Text = "Er is geen ClientId ingesteld. Installeer de app met CLIENTID=… (MSI), stel HKLM\\SOFTWARE\\Policies\\PurviewPdfViewer in, of vul appsettings.json in. Neem contact op met je IT-beheerder.";
            EmptyOpenButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "Geen ClientId geconfigureerd";
            return;
        }

        await SignInAsync();

        if (_startupFile != null && _mip?.IsReady == true)
        {
            await OpenFileAsync(_startupFile);
        }
    }

    private async Task SignInAsync()
    {
        // Vensterhandle hier (op de UI-thread) vastleggen; MSAL mag hem vanaf elke thread opvragen.
        var hwnd = new WindowInteropHelper(this).Handle;
        _mip ??= new MipService(_settings, () => hwnd, Dispatcher);
        StatusText.Text = "Aanmelden en labelbeleid ophalen…";
        try
        {
            await _mip.InitializeAsync();
            AccountText.Text = _mip.UserEmail;
            var count = CountLabels(_mip.Labels);
            StatusText.Text = $"Aangemeld als {_mip.UserEmail} · {count} label(s) beschikbaar";
        }
        catch (Exception ex)
        {
            AccountText.Text = "Niet aangemeld";
            StatusText.Text = "Aanmelden mislukt";
            ShowError("Aanmelden of ophalen van het labelbeleid is mislukt.", ex);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };

        if (_mip?.IsReady == true)
        {
            menu.Items.Add(new MenuItem { Header = _mip.UserEmail, IsEnabled = false });
            if (_mip.LastPolicyFetchTime is { } fetched)
            {
                menu.Items.Add(new MenuItem { Header = $"Labelbeleid opgehaald: {fetched:d MMM HH:mm}", IsEnabled = false });
            }
            menu.Items.Add(new Separator());
            var refreshItem = new MenuItem { Header = "Labels vernieuwen" };
            refreshItem.Click += async (_, _) => await RefreshLabelsAsync();
            menu.Items.Add(refreshItem);
            var diagItem = new MenuItem { Header = "Labeldiagnose kopiëren" };
            diagItem.Click += (_, _) => CopyLabelDiagnostics();
            menu.Items.Add(diagItem);
            var signOut = new MenuItem { Header = "Afmelden" };
            signOut.Click += async (_, _) =>
            {
                if (!await ConfirmDiscardChangesAsync()) return;
                CloseDocument();
                await _mip.SignOutAsync();
                AccountText.Text = "Niet aangemeld";
                StatusText.Text = "Afgemeld";
                CommandManager.InvalidateRequerySuggested();
            };
            menu.Items.Add(signOut);
        }
        else if (!string.IsNullOrWhiteSpace(_settings.ClientId))
        {
            var signIn = new MenuItem { Header = "Aanmelden" };
            signIn.Click += async (_, _) => await SignInAsync();
            menu.Items.Add(signIn);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Haalt het labelbeleid opnieuw op bij Purview. Een geopend document wordt daarna opnieuw
    /// geladen (het label en de rechten zijn gekoppeld aan de oude engine).
    /// </summary>
    private async Task RefreshLabelsAsync()
    {
        if (_mip?.IsReady != true) return;
        if (!await ConfirmDiscardChangesAsync()) return;

        var reopen = _session?.FilePath;
        CloseDocument();

        StatusText.Text = "Labelbeleid vernieuwen…";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await _mip.RefreshPolicyAsync();
            StatusText.Text = $"Labelbeleid vernieuwd · {CountLabels(_mip.Labels)} label(s) beschikbaar";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Vernieuwen mislukt";
            ShowError("Het labelbeleid kon niet worden vernieuwd.", ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            CommandManager.InvalidateRequerySuggested();
        }

        if (reopen != null && File.Exists(reopen)) await OpenFileAsync(reopen);
    }

    /// <summary>
    /// Kopieert naar het klembord welke labels de MIP SDK aan de app doorgeeft, met de eigenschappen
    /// die bepalen of een label in het menu verschijnt. Bedoeld voor probleemoplossing.
    /// </summary>
    private void CopyLabelDiagnostics()
    {
        if (_mip?.IsReady != true) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Purview PDF Viewer {typeof(MainWindow).Assembly.GetName().Version}");
        sb.AppendLine($"Gebruiker: {_mip.UserEmail}");
        sb.AppendLine($"Labelbeleid opgehaald: {_mip.LastPolicyFetchTime:yyyy-MM-dd HH:mm}");
        try { sb.AppendLine($"Verplicht labelen: {_mip.IsLabelingRequired}"); } catch { }
        sb.AppendLine();

        void Dump(MipLabel label, int depth)
        {
            string formats;
            try { formats = label.ContentFormats == null ? "" : string.Join(",", label.ContentFormats); }
            catch { formats = "?"; }
            sb.AppendLine($"{new string(' ', depth * 2)}- {label.Name} | actief={label.IsActive} | sublabels={label.Children.Count} " +
                          $"| versleuteling={label.HasRightsManagementPolicy} | adhoc={label.HasAdhocProtection} " +
                          $"| formaten={formats} | zichtbaar={IsVisible(label)} | id={label.Id}");
            foreach (var child in label.Children) Dump(child, depth + 1);
        }

        foreach (var label in _mip.Labels) Dump(label, 0);

        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText.Text = "Labeldiagnose gekopieerd naar het klembord";
        }
        catch (Exception ex)
        {
            ShowError("Kopiëren naar het klembord is mislukt.", ex);
        }
    }

    // ───────────────────────────── Openen ─────────────────────────────

    private async Task OpenWithDialogAsync()
    {
        if (!await ConfirmDiscardChangesAsync()) return;

        var dialog = new OpenFileDialog
        {
            Filter = "PDF-bestanden (*.pdf)|*.pdf",
            Title = "PDF openen"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await OpenFileAsync(dialog.FileName);
        }
    }

    private async Task OpenFileAsync(string path)
    {
        if (_mip?.IsReady != true)
        {
            MessageBox.Show(this, "Meld je eerst aan om PDF's met gevoeligheidslabels te openen.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusText.Text = $"Openen: {System.IO.Path.GetFileName(path)}…";
        Mouse.OverrideCursor = Cursors.Wait;
        PdfSession? session = null;
        try
        {
            session = await _mip.OpenAsync(path);
            var view = await PdfDocumentView.LoadAsync(session.RenderBytes);

            CloseDocument();
            _session = session;
            _view = view;

            PagesView.ItemsSource = view.Pages;
            EmptyState.Visibility = Visibility.Collapsed;
            Title = $"{session.FileName} – Purview PDF Viewer";

            FitWidth();
            _scroll?.ScrollToTop();
            UpdateLabelUi();

            StatusText.Text = session.IsProtected
                ? $"{session.FileName} geopend (versleuteld met Purview)"
                : $"{session.FileName} geopend";
            ApplyRightsEnforcement();
        }
        catch (UnauthorizedAccessException ex)
        {
            session?.Dispose();
            StatusText.Text = "Geen toegang";
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is MipEx.NoPermissionsException or MipEx.AccessDeniedException)
        {
            session?.Dispose();
            StatusText.Text = "Geen toegang";
            MessageBox.Show(this,
                "Je hebt geen rechten om dit beveiligde document te openen.\n\nNeem contact op met de eigenaar van het document.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            StatusText.Text = "Openen mislukt";
            ShowError("Het document kon niet worden geopend.", ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void CloseDocument()
    {
        PagesView.ItemsSource = null;
        _view?.Dispose();
        _view = null;
        _session?.Dispose();
        _session = null;

        EmptyState.Visibility = Visibility.Visible;
        Title = "Purview PDF Viewer";
        PageInfoText.Text = "";
        UpdateLabelUi();
        ApplyRightsEnforcement();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedPdf(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedPdf(e);
        if (path != null && await ConfirmDiscardChangesAsync())
        {
            await OpenFileAsync(path);
        }
    }

    private static string? GetDroppedPdf(DragEventArgs e)
        => (e.Data.GetData(DataFormats.FileDrop) as string[])?
            .FirstOrDefault(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    // ───────────────────────────── Labels ─────────────────────────────

    private void SensitivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || _mip?.IsReady != true) return;

        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };

        if (!_session.CanChangeLabel)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Je hebt geen rechten om het label van dit versleutelde document te wijzigen.",
                IsEnabled = false
            });
            menu.Items.Add(new Separator());
        }

        foreach (var label in _mip.Labels)
        {
            if (IsVisible(label)) menu.Items.Add(CreateLabelMenuItem(label));
        }

        menu.Items.Add(new Separator());

        var remove = new MenuItem
        {
            Header = "Label verwijderen",
            IsEnabled = _session.CurrentLabel != null && _session.CanChangeLabel && !_mip.IsLabelingRequired,
            ToolTip = _mip.IsLabelingRequired ? "Je organisatie vereist een label; je kunt het wel wijzigen." : null
        };
        ToolTipService.SetShowOnDisabled(remove, true);
        remove.Click += async (_, _) => await RunLabelChangeAsync(
            j => _session!.RemoveLabelAsync(j), "Label verwijderd");
        menu.Items.Add(remove);

        var refresh = new MenuItem { Header = "Labels vernieuwen" };
        refresh.Click += async (_, _) => await RefreshLabelsAsync();
        menu.Items.Add(refresh);

        if (_mip.MoreInfoUrl is { } url)
        {
            var info = new MenuItem { Header = "Meer informatie over labels" };
            info.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            menu.Items.Add(info);
        }

        menu.IsOpen = true;
    }

    private MenuItem CreateLabelMenuItem(MipLabel label)
    {
        var item = new MenuItem
        {
            Header = label.Name,
            Icon = CreateColorDot(ParseColor(label.Color) ?? NoLabelColor),
            ToolTip = string.IsNullOrWhiteSpace(label.Tooltip) ? null : label.Tooltip
        };
        ToolTipService.SetShowOnDisabled(item, true);

        // Hoofdlabel met sublabels: alleen een submenu (zoals in Office). Het hoofdlabel zelf
        // kan dan niet worden toegepast; het wordt getoond zolang er een actief sublabel is.
        if (label.Children.Count > 0)
        {
            foreach (var child in label.Children)
            {
                if (IsVisible(child)) item.Items.Add(CreateLabelMenuItem(child));
            }
            return item;
        }

        item.IsChecked = _session?.CurrentLabel?.Id == label.Id;
        item.IsEnabled = _session?.CanChangeLabel == true;

        item.Click += async (_, _) =>
        {
            if (_session?.CurrentLabel?.Id == label.Id && !label.HasAdhocProtection) return;
            if (label.HasAdhocProtection)
            {
                await ApplyLabelWithPermissionsAsync(label);
                return;
            }
            await RunLabelChangeAsync(j => _session!.ApplyLabelAsync(label, j), $"Label '{FullName(label)}' toegepast",
                onAdhocRequired: () => ApplyLabelWithPermissionsAsync(label));
        };
        return item;
    }

    /// <summary>
    /// Label met door de gebruiker gedefinieerde machtigingen: vraag (zoals Word) wie wat mag,
    /// stel de ad-hocbeveiliging in en pas daarna het label toe.
    /// </summary>
    private async Task ApplyLabelWithPermissionsAsync(MipLabel label)
    {
        if (_session == null || _mip?.UserEmail == null) return;

        var dialog = new PermissionsDialog(FullName(label), _mip.UserEmail) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            StatusText.Text = "Labelwijziging geannuleerd";
            return;
        }

        var permissions = dialog.Permissions;
        var validUntil = dialog.ValidUntil;
        var offline = dialog.AllowOfflineAccess;
        await RunLabelChangeAsync(
            j => _session!.ApplyLabelWithPermissionsAsync(label, permissions, validUntil, offline, j),
            $"Label '{FullName(label)}' met machtigingen toegepast");
    }

    /// <summary>
    /// Voert een labelwijziging uit. Vraagt om een reden als het beleid dat vereist
    /// (label verlagen of verwijderen), precies zoals Office en Acrobat dat doen.
    /// </summary>
    private async Task RunLabelChangeAsync(Func<string?, Task> change, string successMessage, Func<Task>? onAdhocRequired = null)
    {
        try
        {
            try
            {
                await change(null);
            }
            catch (Exception ex) when (ex is MipEx.JustificationRequiredException)
            {
                var dialog = new JustificationDialog { Owner = this };
                if (dialog.ShowDialog() != true)
                {
                    StatusText.Text = "Labelwijziging geannuleerd";
                    return;
                }
                await change(dialog.Justification);
            }

            StatusText.Text = $"{successMessage} — klik op Opslaan om de wijziging in het bestand te schrijven.";
        }
        catch (Exception ex) when (ex is MipEx.AdhocProtectionRequiredException)
        {
            // Het beleid vraagt alsnog om zelf ingestelde machtigingen.
            if (onAdhocRequired != null) await onAdhocRequired();
        }
        catch (Exception ex) when (ex is MipEx.NoPermissionsException or MipEx.AccessDeniedException)
        {
            MessageBox.Show(this, "Je hebt niet de rechten om het label van dit document te wijzigen. Alleen de eigenaar (of iemand met exportrechten) kan dat.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ShowError("Het label kon niet worden toegepast.", ex);
        }
        finally
        {
            UpdateLabelUi();
        }
    }

    private void UpdateLabelUi()
    {
        CommandManager.InvalidateRequerySuggested();

        if (_session == null)
        {
            LabelBar.Visibility = Visibility.Collapsed;
            SensitivityButton.IsEnabled = false;
            return;
        }

        LabelBar.Visibility = Visibility.Visible;
        SensitivityButton.IsEnabled = _mip?.IsReady == true;

        var label = _session.CurrentLabel;
        var color = ParseColor(label?.Color) ?? NoLabelColor;
        LabelAccent.Fill = new SolidColorBrush(color);
        LabelDot.Fill = new SolidColorBrush(color);
        LabelTint.Background = new SolidColorBrush(Color.FromArgb(0x1A, color.R, color.G, color.B));
        LabelNameRun.Text = label == null ? "Geen label" : FullName(label);

        // Beveiligingsinfo
        string? protectionText = null;
        if (_session.HasUnsavedChanges && _session.IsLabelProtecting != _session.IsProtected)
        {
            protectionText = _session.IsLabelProtecting
                ? "Versleuteling wordt toegepast bij opslaan"
                : "Versleuteling wordt verwijderd bij opslaan";
        }
        else if (_session.IsProtected)
        {
            var owner = _session.IsOwner ? "jij bent eigenaar" : $"eigenaar: {_session.Owner}";
            protectionText = $"Versleuteld · {owner} · jouw rechten: {DescribeRights(_session.Rights)}";
        }
        ProtectionPanel.Visibility = protectionText != null ? Visibility.Visible : Visibility.Collapsed;
        ProtectionText.Text = protectionText ?? "";

        RequiredPanel.Visibility = label == null && _mip?.IsLabelingRequired == true ? Visibility.Visible : Visibility.Collapsed;

        var dirty = _session.HasUnsavedChanges;
        UnsavedText.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SaveFromBarButton.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;

        ChangeLabelButton.Content = label == null ? "Label kiezen" : "Label wijzigen";
        ChangeLabelButton.IsEnabled = _session.CanChangeLabel;

        Title = $"{(dirty ? "• " : "")}{_session.FileName} – Purview PDF Viewer";
    }

    // ───────────────────────────── Opslaan ─────────────────────────────

    private async Task SaveAsWithDialogAsync()
    {
        if (_session == null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "PDF-bestanden (*.pdf)|*.pdf",
            FileName = _session.FileName,
            InitialDirectory = System.IO.Path.GetDirectoryName(_session.FilePath),
            Title = "Opslaan als"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await SaveAsync(dialog.FileName);
        }
    }

    private async Task<bool> SaveAsync(string? targetPath)
    {
        if (_session == null) return true;
        targetPath ??= _session.FilePath;

        StatusText.Text = "Opslaan…";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await _session.SaveAsync(targetPath);
            UpdateLabelUi();
            StatusText.Text = $"Opgeslagen: {targetPath}";
            ApplyRightsEnforcement();
            return true;
        }
        catch (IOException ex)
        {
            ShowError("Opslaan is mislukt. Is het bestand nog geopend in een ander programma (of alleen-lezen)?", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowError("Opslaan is mislukt: geen schrijfrechten op deze locatie.", ex);
        }
        catch (Exception ex) when (ex is MipEx.NoPermissionsException or MipEx.AccessDeniedException)
        {
            ShowError("Opslaan is mislukt: je hebt niet de rechten om de beveiliging van dit document te wijzigen.", ex);
        }
        catch (Exception ex)
        {
            ShowError("Opslaan is mislukt.", ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            CommandManager.InvalidateRequerySuggested();
        }
        StatusText.Text = "Opslaan mislukt";
        return false;
    }

    /// <summary>Vraagt bij een niet-opgeslagen labelwijziging of die eerst opgeslagen moet worden.</summary>
    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (_session?.HasUnsavedChanges != true) return true;
        var answer = MessageBox.Show(this,
            "Er is een labelwijziging die nog niet is opgeslagen. Wil je die eerst opslaan?",
            Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return await SaveAsync(null);
        return true;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || _session?.HasUnsavedChanges != true)
        {
            WindowPlacement.Save(this);
            _view?.Dispose();
            _session?.Dispose();
            _mip?.Dispose();
            return;
        }

        e.Cancel = true;
        var answer = MessageBox.Show(this,
            "Er is een labelwijziging die nog niet is opgeslagen. Wil je die opslaan voordat je afsluit?",
            Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        if (answer == MessageBoxResult.Yes && !await SaveAsync(null)) return;

        _closeConfirmed = true;
        Close();
    }

    // ───────────────────────────── Afdrukken ─────────────────────────────

    private async Task PrintAsync()
    {
        if (_session == null || _view == null) return;
        if (!_session.CanPrint)
        {
            MessageBox.Show(this, "Je hebt geen rechten om dit document af te drukken.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            MinPage = 1,
            MaxPage = (uint)_view.PageCount
        };
        if (dialog.ShowDialog() != true) return;

        int from = 1, to = _view.PageCount;
        if (dialog.PageRangeSelection == PageRangeSelection.UserPages)
        {
            from = Math.Max(1, dialog.PageRange.PageFrom);
            to = Math.Min(_view.PageCount, dialog.PageRange.PageTo);
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var document = new FixedDocument();
            for (int number = from; number <= to; number++)
            {
                StatusText.Text = $"Afdrukken voorbereiden… pagina {number} van {to}";
                var page = _view.Pages[number - 1];
                var bitmap = await _view.RenderPageAsync(page.Index, 3.0); // ±288 dpi

                var fixedPage = new FixedPage { Width = page.BaseWidth, Height = page.BaseHeight };
                fixedPage.Children.Add(new Image
                {
                    Source = bitmap,
                    Width = page.BaseWidth,
                    Height = page.BaseHeight,
                    Stretch = Stretch.Fill
                });
                document.Pages.Add(new PageContent { Child = fixedPage });
            }

            dialog.PrintDocument(document.DocumentPaginator, _session.FileName);
            StatusText.Text = "Naar de printer verstuurd";
        }
        catch (Exception ex)
        {
            ShowError("Afdrukken is mislukt.", ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ───────────────────────────── Weergave ─────────────────────────────

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PdfDocumentView.PageViewModel page) page.EnsureRendered();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PdfDocumentView.PageViewModel page) page.Release();
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_view == null || Keyboard.Modifiers != ModifierKeys.Control) return;
        StepZoom(e.Delta > 0 ? +1 : -1);
        e.Handled = true;
    }

    private void StepZoom(int direction)
    {
        double next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > _zoom + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < _zoom - 0.001, ZoomSteps[0]);
        SetZoom(next);
    }

    private void FitWidth_Click(object sender, RoutedEventArgs e) => FitWidth();

    private void FitWidth()
    {
        if (_view == null || _view.PageCount == 0) return;
        var widest = _view.Pages.Max(p => p.BaseWidth);
        var available = Math.Max(200, PagesView.ActualWidth - 32 - SystemParameters.VerticalScrollBarWidth - 8);
        SetZoom(Math.Clamp(available / widest, ZoomSteps[0], ZoomSteps[^1]));
    }

    private void SetZoom(double zoom)
    {
        _zoom = zoom;
        _view?.SetZoom(zoom);
        ZoomText.Text = $"{Math.Round(zoom * 100)}%";
        UpdatePageInfo();
    }

    private void UpdatePageInfo()
    {
        if (_view == null || _scroll == null || _view.PageCount == 0)
        {
            PageInfoText.Text = "";
            return;
        }

        // Pagina waarvan het midden van het venster binnen valt.
        double probe = _scroll.VerticalOffset + _scroll.ViewportHeight / 2 - 16;
        double y = 0;
        int current = _view.PageCount;
        foreach (var page in _view.Pages)
        {
            y += page.DisplayHeight + 16;
            if (probe < y) { current = page.Number; break; }
        }
        PageInfoText.Text = $"Pagina {current} van {_view.PageCount}";
    }

    // ───────────────────────────── Rechten afdwingen ─────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    private const uint WDA_NONE = 0x0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    /// <summary>
    /// Zonder kopieerrecht (EXTRACT) sluiten we het venster uit van schermafbeeldingen en schermdelen.
    /// Tekst selecteren/kopiëren is in deze viewer sowieso niet mogelijk (pagina's zijn afbeeldingen),
    /// en een onversleutelde kopie opslaan wordt nergens aangeboden.
    /// </summary>
    private void ApplyRightsEnforcement()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        bool block = _session?.IsProtected == true && !_session.CanExtract;
        SetWindowDisplayAffinity(handle, block ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        if (block)
        {
            StatusText.Text += " · schermafbeeldingen geblokkeerd (geen kopieerrecht)";
        }
    }

    // ───────────────────────────── Hulpfuncties ─────────────────────────────

    private static string FullName(MipLabel label)
        => label.Parent != null ? $"{label.Parent.Name} \\ {label.Name}" : label.Name;

    /// <summary>Een label is zichtbaar als het actief is, of als het een hoofdlabel met een zichtbaar sublabel is.</summary>
    private static bool IsVisible(MipLabel label)
        => label.Children.Count > 0 ? label.Children.Any(IsVisible) : label.IsActive;

    private static int CountLabels(IEnumerable<MipLabel> labels)
        => labels.Sum(l => l.Children.Count > 0 ? CountLabels(l.Children) : (l.IsActive ? 1 : 0));

    private static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return null; }
    }

    private static Ellipse CreateColorDot(Color color) => new()
    {
        Width = 10,
        Height = 10,
        Fill = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private static string DescribeRights(IEnumerable<string> rights)
    {
        var set = new HashSet<string>(rights, StringComparer.OrdinalIgnoreCase);
        if (set.Contains(PdfSession.RightOwner)) return "volledig beheer";

        var names = new List<string>();
        if (set.Contains(PdfSession.RightView)) names.Add("bekijken");
        if (set.Contains(PdfSession.RightEdit) || set.Contains("DOCEDIT")) names.Add("bewerken");
        if (set.Contains(PdfSession.RightPrint)) names.Add("afdrukken");
        if (set.Contains(PdfSession.RightExtract)) names.Add("kopiëren");
        if (set.Contains(PdfSession.RightExport)) names.Add("exporteren");
        return names.Count > 0 ? string.Join(", ", names) : "alleen bekijken";
    }

    private void ShowError(string message, Exception ex)
    {
        var detail = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
        MessageBox.Show(this, $"{message}\n\n{detail.GetType().Name}: {detail.Message}", Title,
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
