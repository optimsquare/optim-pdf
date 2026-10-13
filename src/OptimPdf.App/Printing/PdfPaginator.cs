using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OptimPdf.Core.Rendering;

namespace OptimPdf.App.Printing;

/// <summary>
/// Renders PDF pages on demand for printing. Each page is scaled to fit the printable area and
/// rotated when its orientation differs from the paper's. Pages are rasterized at <see cref="Dpi"/>.
/// </summary>
public sealed class PdfPaginator(PdfiumDocument document, IReadOnlyList<int> pages, Size paperSize, int dpi = 300) : DocumentPaginator
{
    public int Dpi { get; } = dpi;

    public override bool IsPageCountValid => true;
    public override int PageCount => pages.Count;
    public override Size PageSize { get => paperSize; set => paperSize = value; }
    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        int index = pages[pageNumber];
        var size = document.GetPageSize(index);
        bool paperLandscape = paperSize.Width > paperSize.Height;
        bool pageLandscape = size.Width > size.Height;
        int rotation = paperLandscape == pageLandscape ? 0 : 1;

        double pw = rotation == 0 ? size.Width : size.Height;   // points, as placed on paper
        double ph = rotation == 0 ? size.Height : size.Width;
        double fit = Math.Min(paperSize.Width / (pw * 96 / 72), paperSize.Height / (ph * 96 / 72));
        fit = Math.Min(fit, 1.0); // Never enlarge beyond actual size.
        double w = pw * 96 / 72 * fit, h = ph * 96 / 72 * fit;

        var rendered = document.Render(index, Dpi / 72.0 * fit, rotation);
        var bitmap = BitmapSource.Create(rendered.PixelWidth, rendered.PixelHeight, Dpi, Dpi, PixelFormats.Pbgra32, null, rendered.Pixels, rendered.Stride);
        bitmap.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(bitmap, new Rect((paperSize.Width - w) / 2, (paperSize.Height - h) / 2, w, h));
        return new DocumentPage(visual, paperSize, new Rect(paperSize), new Rect(paperSize));
    }
}
