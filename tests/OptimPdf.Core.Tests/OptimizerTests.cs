using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.IO;
using OptimPdf.Core.Optimization;
using OptimPdf.Core.Rendering;
using PdfSharp.Pdf;

namespace OptimPdf.Core.Tests;

public class OptimizerTests
{
    static byte[] PdfWithImage(byte[] image, int pages = 1) => DocumentSession.Transform(TestPdf.Blank(pages), null, doc =>
    {
        for (int i = 0; i < pages; i++) ContentEditor.AddImage(doc, i, 36, 756, 540, 360, image);
    });

    static (OptimizeReport Report, byte[] Data) Optimize(byte[] data, OptimizeOptions options)
    {
        OptimizeReport? report = null;
        var result = DocumentSession.Transform(data, null, doc => report = PdfOptimizer.Optimize(doc, options));
        return (report!, result);
    }

    static int ImageCount(byte[] data) => DocumentSession.FromBytes(data).Query(d =>
        d.Internals.GetAllObjects().OfType<PdfDictionary>().Count(o => o.Elements.GetName("/Subtype") == "/Image"));

    [Fact]
    public void Recompresses_and_downsamples_large_lossless_photos()
    {
        var original = PdfWithImage(TestImages.PhotoBytes(2400, 1600, ImageFileFormat.Png));
        var (report, optimized) = Optimize(original, OptimizeOptions.Balanced);

        Assert.Equal(1, report.ImagesRecompressed);
        Assert.Equal(1, report.ImagesDownsampled);
        Assert.True(optimized.Length < original.Length / 4, $"{original.Length} -> {optimized.Length}");

        // The page still looks the same.
        using var a = PdfiumDocument.Open(original);
        using var b = PdfiumDocument.Open(optimized);
        var area = new PdfRect(60, 420, 550, 740);
        var ca = TestPdf.AverageColor(a, 0, area);
        var cb = TestPdf.AverageColor(b, 0, area);
        Assert.InRange(Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B), 0, 15);
    }

    [Fact]
    public void Recompresses_high_quality_jpeg_only_when_it_helps()
    {
        var original = PdfWithImage(TestImages.PhotoBytes(1200, 800, ImageFileFormat.Jpeg, 100));
        var (report, optimized) = Optimize(original, OptimizeOptions.Smallest);
        Assert.Equal(1, report.ImagesRecompressed);
        Assert.True(optimized.Length < original.Length * 0.6);

        // Already-small JPEG at low quality is left alone.
        var small = PdfWithImage(TestImages.PhotoBytes(300, 200, ImageFileFormat.Jpeg, 40));
        var (report2, _) = Optimize(small, OptimizeOptions.HighQuality);
        Assert.Equal(0, report2.ImagesRecompressed);
    }

    [Fact]
    public void Lossless_preset_never_touches_images()
    {
        var original = PdfWithImage(TestImages.PhotoBytes(800, 600, ImageFileFormat.Png));
        var (report, optimized) = Optimize(original, OptimizeOptions.Lossless);
        Assert.Equal(0, report.ImagesRecompressed);
        using var a = PdfiumDocument.Open(original);
        using var b = PdfiumDocument.Open(optimized);
        Assert.Equal(a.Render(0, 0.5).Pixels, b.Render(0, 0.5).Pixels); // Pixel-identical.
    }

    [Fact]
    public void Merges_identical_images()
    {
        // Three separate files with the same picture, combined: each brings its own copy of the image.
        var picture = TestImages.PhotoBytes(400, 300, ImageFileFormat.Jpeg, 85);
        var parts = Enumerable.Range(0, 3).Select(_ => Core.Pages.PageOperations.OpenForImport(PdfWithImage(picture))).ToList();
        var original = DocumentSession.Serialize(Core.Pages.PageOperations.Merge(parts));
        Assert.Equal(3, ImageCount(original));
        var (report, optimized) = Optimize(original, OptimizeOptions.Lossless);
        Assert.Equal(2, report.DuplicatesMerged);
        Assert.Equal(1, ImageCount(optimized));
        Assert.True(optimized.Length < original.Length / 2);
        using var r = PdfiumDocument.Open(optimized);
        for (int i = 0; i < 3; i++) Assert.True(TestPdf.AverageColor(r, i, new PdfRect(100, 500, 500, 700)).R < 240);
    }

    [Fact]
    public void Compresses_uncompressed_content_and_removes_thumbnails()
    {
        var data = DocumentSession.Transform(TestPdf.Create(5), null, doc =>
        {
            doc.Options.CompressContentStreams = false;
            var thumb = new PdfDictionary(doc);
            thumb.CreateStream(new byte[5000]);
            doc.Internals.AddObject(thumb);
            doc.Pages[0].Elements["/Thumb"] = thumb.Reference;
        });
        var (_, optimized) = Optimize(data, OptimizeOptions.Lossless);
        Assert.True(optimized.Length < data.Length);
        Assert.False(DocumentSession.FromBytes(optimized).Query(d => d.Pages[0].Elements.ContainsKey("/Thumb")));
        Assert.Contains(TestPdf.Title(5), TestPdf.PageTitles(optimized));
    }
}

public class RecoveryTests
{
    [Fact]
    public void Saves_lists_and_deletes_snapshots()
    {
        var store = new RecoveryStore(TestPdf.TempDir());
        var data = TestPdf.Create(2);
        store.Save("a1", data, @"C:\docs\report.pdf", "report.pdf", encrypted: false);
        Thread.Sleep(10);
        store.Save("b2", TestPdf.Create(1), null, "Untitled.pdf", encrypted: true);

        var entries = store.List();
        Assert.Equal(["b2", "a1"], entries.Select(e => e.Id));
        Assert.Equal(@"C:\docs\report.pdf", entries[1].OriginalPath);
        Assert.True(entries[0].Encrypted);
        Assert.Equal(data, File.ReadAllBytes(entries[1].DataPath));

        // Overwriting a snapshot replaces it.
        store.Save("a1", TestPdf.Create(3), @"C:\docs\report.pdf", "report.pdf", false);
        Assert.Equal(3, PdfiumDocument.Open(File.ReadAllBytes(store.List().Single(e => e.Id == "a1").DataPath)).PageCount);

        store.Delete("a1");
        Assert.Equal(["b2"], store.List().Select(e => e.Id));
    }

    [Fact]
    public void Snapshots_of_running_sessions_are_not_offered_for_recovery()
    {
        var store = new RecoveryStore(TestPdf.TempDir());
        store.Save("live", TestPdf.Create(1), null, "live.pdf", false); // Written by this (running) process.
        var entry = store.List().Single();
        Assert.Equal(Environment.ProcessId, entry.OwnerProcessId);
        Assert.True(entry.OwnerIsRunning);
        Assert.Empty(store.ListOrphaned());
        Assert.False((entry with { OwnerProcessId = 0 }).OwnerIsRunning);
    }

    [Fact]
    public void Ignores_orphans_and_missing_directory()
    {
        var dir = TestPdf.TempDir();
        Assert.Empty(new RecoveryStore(Path.Combine(dir, "nope")).List());
        var store = new RecoveryStore(dir);
        store.Save("x", TestPdf.Create(1), null, "x.pdf", false);
        File.Delete(Path.Combine(dir, "x.pdf"));
        Assert.Empty(store.List());
        Assert.False(File.Exists(Path.Combine(dir, "x.json")));
    }
}
