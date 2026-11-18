using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace PurviewPdfViewer.Pdf;

/// <summary>
/// Rendert PDF-pagina's met de ingebouwde PDF-engine van Windows (Windows.Data.Pdf).
/// De (ontsleutelde) inhoud staat alleen in het geheugen; er komt geen leesbare kopie op schijf.
/// Pagina's worden pas gerenderd als ze in beeld komen en weer vrijgegeven als ze uit beeld gaan.
/// </summary>
public sealed class PdfDocumentView : IDisposable
{
    private const double RenderScale = 2.0; // scherp op hoge DPI en bij inzoomen
    private readonly PdfDocument _document;
    private readonly InMemoryRandomAccessStream _source;
    private readonly SemaphoreSlim _renderLock = new(1, 1);

    private PdfDocumentView(PdfDocument document, InMemoryRandomAccessStream source)
    {
        _document = document;
        _source = source;
        for (uint i = 0; i < document.PageCount; i++)
        {
            using var page = document.GetPage(i);
            Pages.Add(new PageViewModel(this, i, page.Size.Width, page.Size.Height));
        }
    }

    public ObservableCollection<PageViewModel> Pages { get; } = new();
    public int PageCount => Pages.Count;

    public static async Task<PdfDocumentView> LoadAsync(byte[] pdfBytes)
    {
        var source = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(source.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pdfBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        source.Seek(0);

        var document = await PdfDocument.LoadFromStreamAsync(source);
        return new PdfDocumentView(document, source);
    }

    public void SetZoom(double zoom)
    {
        foreach (var page in Pages) page.Zoom = zoom;
    }

    /// <summary>Rendert één pagina naar een bevroren bitmap (bruikbaar op elke thread).</summary>
    public async Task<BitmapSource> RenderPageAsync(uint index, double scale)
    {
        await _renderLock.WaitAsync();
        try
        {
            using var page = _document.GetPage(index);
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale))
            };

            using var output = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(output, options);

            using var managed = new MemoryStream();
            output.Seek(0);
            var rendered = output.AsStream();
            rendered.Position = 0;
            await rendered.CopyToAsync(managed);
            managed.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = managed;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            _renderLock.Release();
        }
    }

    public void Dispose()
    {
        foreach (var page in Pages) page.Release();
        _source.Dispose();
    }

    public sealed class PageViewModel : INotifyPropertyChanged
    {
        private readonly PdfDocumentView _owner;
        private ImageSource? _image;
        private double _zoom = 1.0;
        private bool _rendering;

        internal PageViewModel(PdfDocumentView owner, uint index, double width, double height)
        {
            _owner = owner;
            Index = index;
            BaseWidth = width;
            BaseHeight = height;
        }

        public uint Index { get; }
        public int Number => (int)Index + 1;
        public double BaseWidth { get; }
        public double BaseHeight { get; }

        public double Zoom
        {
            get => _zoom;
            set
            {
                if (Math.Abs(_zoom - value) < 0.0001) return;
                _zoom = value;
                OnPropertyChanged(nameof(DisplayWidth));
                OnPropertyChanged(nameof(DisplayHeight));
            }
        }

        public double DisplayWidth => BaseWidth * _zoom;
        public double DisplayHeight => BaseHeight * _zoom;

        public ImageSource? Image
        {
            get => _image;
            private set { _image = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLoading)); }
        }

        public bool IsLoading => _image == null;

        public async void EnsureRendered()
        {
            if (_image != null || _rendering) return;
            _rendering = true;
            try
            {
                Image = await _owner.RenderPageAsync(Index, RenderScale);
            }
            catch
            {
                // Pagina kon niet gerenderd worden; placeholder blijft staan.
            }
            finally
            {
                _rendering = false;
            }
        }

        public void Release() => Image = null;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
