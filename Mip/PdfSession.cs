using System.IO;
using Microsoft.InformationProtection;
using Microsoft.InformationProtection.File;
using Microsoft.InformationProtection.Protection;

namespace PurviewPdfViewer.Mip;

/// <summary>
/// Een geopende PDF met zijn MIP-filehandler. Labelwijzigingen worden op de handler klaargezet
/// en pas bij Opslaan (CommitAsync) naar schijf geschreven — net als in Acrobat.
/// Het bestand zelf blijft niet geopend/vergrendeld: de handler werkt op een kopie in het geheugen.
/// </summary>
public sealed class PdfSession : IDisposable
{
    // Rechtennamen uit Purview (usage rights). AccessCheck accepteert deze strings.
    public const string RightView = "VIEW";
    public const string RightPrint = "PRINT";
    public const string RightExtract = "EXTRACT";
    public const string RightExport = "EXPORT";
    public const string RightEdit = "EDIT";
    public const string RightOwner = "OWNER";

    private readonly IFileEngine _engine;
    private IFileHandler _handler;
    private MemoryStream _handlerStream;

    private PdfSession(IFileEngine engine, string path, IFileHandler handler, MemoryStream handlerStream, byte[] renderBytes)
    {
        _engine = engine;
        FilePath = path;
        _handler = handler;
        _handlerStream = handlerStream;
        RenderBytes = renderBytes;
        RefreshFromHandler();
    }

    public string FilePath { get; private set; }
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Leesbare PDF-inhoud (ontsleuteld indien beveiligd). Staat alleen in het geheugen.</summary>
    public byte[] RenderBytes { get; }

    /// <summary>Het label zoals het nu (inclusief niet-opgeslagen wijziging) op het document staat.</summary>
    public Label? CurrentLabel { get; private set; }
    public bool IsLabelProtecting { get; private set; }

    public bool IsProtected { get; private set; }
    public string? Owner { get; private set; }
    public bool IsOwner { get; private set; }
    public IReadOnlyList<string> Rights { get; private set; } = Array.Empty<string>();

    public bool HasUnsavedChanges { get; private set; }

    public bool CanPrint => !IsProtected || HasRight(RightPrint);
    /// <summary>Kopiëren / schermafbeeldingen toegestaan?</summary>
    public bool CanExtract => !IsProtected || HasRight(RightExtract);
    /// <summary>
    /// Label wijzigen op een versleuteld document betekent opnieuw beveiligen; daarvoor zijn
    /// eigenaar- of exportrechten nodig. Onbeveiligde documenten mag iedereen labelen.
    /// </summary>
    public bool CanChangeLabel => !IsProtected || HasRight(RightOwner) || HasRight(RightExport) || HasRight(RightExtract);

    public bool HasRight(string right)
    {
        if (!IsProtected) return true;
        try { return _handler.Protection?.AccessCheck(right) ?? true; }
        catch { return false; }
    }

    internal static async Task<PdfSession> OpenAsync(IFileEngine engine, string path)
    {
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);

        var handlerStream = ToReadWriteStream(bytes);
        var handler = await engine.CreateFileHandlerAsync(handlerStream, path, true).ConfigureAwait(false);

        byte[] renderBytes = bytes;
        var protection = handler.Protection;
        if (protection != null)
        {
            if (!protection.AccessCheck(RightView))
            {
                handler.Dispose();
                throw new UnauthorizedAccessException("Je hebt geen rechten om dit document te bekijken.");
            }

            // Ontsleutelen met een aparte handler, zodat de label-handler onaangeroerd blijft.
            using var decryptSource = ToReadWriteStream(bytes);
            using var decryptHandler = await engine.CreateFileHandlerAsync(decryptSource, path, false).ConfigureAwait(false);
            using var decrypted = await decryptHandler.GetDecryptedTemporaryStreamAsync().ConfigureAwait(false);
            using var buffer = new MemoryStream();
            if (decrypted.CanSeek) decrypted.Position = 0;
            await decrypted.CopyToAsync(buffer).ConfigureAwait(false);
            renderBytes = buffer.ToArray();
        }

        return new PdfSession(engine, path, handler, handlerStream, renderBytes);
    }

    /// <summary>Zet een label klaar. Gooit JustificationRequiredException als een reden nodig is.</summary>
    public Task ApplyLabelAsync(Label label, string? justification) => Task.Run(() =>
    {
        var options = CreateOptions(justification);
        _handler.SetLabel(label, options, new ProtectionSettings());
        HasUnsavedChanges = _handler.IsModified();
        CurrentLabel = label;
        IsLabelProtecting = label.HasRightsManagementPolicy;
    });

    /// <summary>
    /// Zet een label met door de gebruiker gedefinieerde machtigingen klaar: eerst de ad-hocbeveiliging
    /// (wie mag wat), daarna het label. Gooit JustificationRequiredException als een reden nodig is.
    /// </summary>
    public Task ApplyLabelWithPermissionsAsync(Label label, List<UserRights> permissions, DateTime? validUntil,
                                               bool allowOfflineAccess, string? justification) => Task.Run(() =>
    {
        var descriptor = new ProtectionDescriptor(permissions)
        {
            AllowOfflineAccess = allowOfflineAccess
        };
        if (validUntil.HasValue) descriptor.ContentValidUntil = validUntil.Value.ToUniversalTime();

        _handler.SetProtection(descriptor, new ProtectionSettings());
        _handler.SetLabel(label, CreateOptions(justification), new ProtectionSettings());
        HasUnsavedChanges = _handler.IsModified();
        CurrentLabel = label;
        IsLabelProtecting = true;
    });

    /// <summary>Verwijdert het label (en daarmee eventuele beveiliging uit het label).</summary>
    public Task RemoveLabelAsync(string? justification) => Task.Run(() =>
    {
        var options = CreateOptions(justification);
        _handler.DeleteLabel(options);
        HasUnsavedChanges = _handler.IsModified();
        CurrentLabel = null;
        IsLabelProtecting = false;
    });

    /// <summary>
    /// Schrijft de wijzigingen weg. Eerst naar een tijdelijk bestand in dezelfde map,
    /// daarna wordt het doelbestand vervangen. Bij succes wordt een audit-event verstuurd.
    /// </summary>
    public Task SaveAsync(string targetPath) => Task.Run(async () =>
    {
        var samePath = string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase);

        if (!_handler.IsModified())
        {
            // Geen labelwijziging: bij "Opslaan als" het bestand ongewijzigd kopiëren (beveiliging blijft intact).
            if (samePath) { HasUnsavedChanges = false; return; }
            File.Copy(FilePath, targetPath, overwrite: true);
        }
        else
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
            var tmp = Path.Combine(dir, $"~{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                var written = await _handler.CommitAsync(tmp).ConfigureAwait(false);
                if (!written) throw new IOException("De SDK heeft geen wijzigingen geschreven.");

                File.Move(tmp, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }

            try { _handler.NotifyCommitSuccessful(targetPath); } catch { /* audit is best-effort */ }
        }

        // Handler opnieuw opbouwen op de opgeslagen versie (label/beveiliging zoals nu op schijf).
        var bytes = await File.ReadAllBytesAsync(targetPath).ConfigureAwait(false);
        var newStream = ToReadWriteStream(bytes);
        var newHandler = await _engine.CreateFileHandlerAsync(newStream, targetPath, true).ConfigureAwait(false);

        _handler.Dispose();
        _handlerStream.Dispose();
        _handler = newHandler;
        _handlerStream = newStream;
        FilePath = targetPath;
        HasUnsavedChanges = false;
        RefreshFromHandler();
    });

    private static LabelingOptions CreateOptions(string? justification)
    {
        var options = new LabelingOptions { AssignmentMethod = AssignmentMethod.Standard };
        if (!string.IsNullOrWhiteSpace(justification))
        {
            options.IsDowngradeJustified = true;
            options.JustificationMessage = justification;
        }
        return options;
    }

    private void RefreshFromHandler()
    {
        var contentLabel = _handler.Label;
        CurrentLabel = contentLabel?.Label;
        IsLabelProtecting = contentLabel?.IsProtectionAppliedFromLabel ?? false;

        IProtectionHandler? protection = _handler.Protection;
        IsProtected = protection != null;
        if (protection != null)
        {
            Owner = protection.Owner;
            IsOwner = protection.IsIssuedToOwner;
            Rights = protection.Rights?.ToList() ?? new List<string>();
        }
        else
        {
            Owner = null;
            IsOwner = false;
            Rights = Array.Empty<string>();
        }
    }

    private static MemoryStream ToReadWriteStream(byte[] bytes)
    {
        // De SDK verwacht een lees/schrijf-stream.
        var ms = new MemoryStream();
        ms.Write(bytes, 0, bytes.Length);
        ms.Position = 0;
        return ms;
    }

    public void Dispose()
    {
        _handler.Dispose();
        _handlerStream.Dispose();
    }
}
