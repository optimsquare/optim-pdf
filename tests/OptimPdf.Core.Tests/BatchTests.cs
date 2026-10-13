using System.Drawing;
using OptimPdf.Core.Batch;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Optimization;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Security;

namespace OptimPdf.Core.Tests;

public class BatchTests
{
    static (string Dir, List<string> Files) Inputs(int count)
    {
        var dir = TestPdf.TempDir();
        var files = new List<string>();
        for (int i = 1; i <= count; i++)
        {
            var path = Path.Combine(dir, $"report {i}.pdf");
            File.WriteAllBytes(path, TestPdf.Create(i, title: $"Report number {i}"));
            files.Add(path);
        }
        return (dir, files);
    }

    [Fact]
    public async Task Compress_writes_new_files_and_leaves_inputs_untouched()
    {
        var (dir, files) = Inputs(3);
        var before = files.Select(File.ReadAllBytes).ToList();
        var output = Path.Combine(dir, "out");

        var results = await new BatchProcessor(new CompressOperation(OptimizeOptions.Balanced), output, "{name} small").RunAsync(files);

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(["report 1 small.pdf", "report 2 small.pdf", "report 3 small.pdf"], results.Select(r => Path.GetFileName(r.Outputs.Single())));
        for (int i = 0; i < files.Count; i++) Assert.Equal(before[i], File.ReadAllBytes(files[i]));
        Assert.Equal(TestPdf.Title(3), TestPdf.PageTitles(File.ReadAllBytes(results[2].Outputs[0]))[2]);
    }

    [Fact]
    public async Task Rename_template_and_no_overwrite()
    {
        var (dir, files) = Inputs(2);
        var output = Path.Combine(dir, "renamed");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "002 - Report number 2.pdf"), "keep me");

        var results = await new BatchProcessor(new RenameOperation(), output, "{n:000} - {title}").RunAsync(files);

        Assert.Equal("001 - Report number 1.pdf", Path.GetFileName(results[0].Outputs[0]));
        Assert.Equal("002 - Report number 2 (2).pdf", Path.GetFileName(results[1].Outputs[0]));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(output, "002 - Report number 2.pdf")));
    }

    [Fact]
    public async Task Merge_combines_inputs_in_order()
    {
        var (dir, files) = Inputs(3); // 1 + 2 + 3 pages
        var results = await new BatchProcessor(new MergeOperation("All reports"), Path.Combine(dir, "m")).RunAsync(files);
        Assert.True(Assert.Single(results).Success, results[0].Message);
        var merged = File.ReadAllBytes(results[0].Outputs.Single());
        var titles = TestPdf.PageTitles(merged);
        Assert.Equal(6, titles.Count);
        Assert.Equal(TestPdf.Title(1), titles[0]);
        Assert.Equal(TestPdf.Title(3), titles[5]);
    }

    [Fact]
    public async Task Protect_and_export_images()
    {
        var (dir, files) = Inputs(1);
        var protect = await new BatchProcessor(new ProtectOperation(new ProtectionOptions("pw", "owner", new PdfPermissions(), EncryptionStrength.Aes256)),
            Path.Combine(dir, "p")).RunAsync(files);
        var data = File.ReadAllBytes(protect[0].Outputs[0]);
        Assert.Throws<PdfPasswordRequiredException>(() => PdfiumDocument.Open(data));
        using (var opened = PdfiumDocument.Open(data, "pw")) Assert.Equal(6, opened.SecurityRevision);

        var export = await new BatchProcessor(new ExportImagesOperation(ImageFileFormat.Png, 72), Path.Combine(dir, "img")).RunAsync(files);
        var png = Assert.Single(export[0].Outputs);
        Assert.EndsWith("report 1-001.png", png);
        using var bmp = new Bitmap(png);
        Assert.Equal(612, bmp.Width);
    }

    [Fact]
    public async Task Bad_files_are_reported_and_the_batch_continues()
    {
        var (dir, files) = Inputs(2);
        var broken = Path.Combine(dir, "broken.pdf");
        File.WriteAllText(broken, "not a pdf");
        var locked = Path.Combine(dir, "locked.pdf");
        File.WriteAllBytes(locked, DocumentSession.Transform(TestPdf.Create(1), null, _ => { },
            new ProtectionOptions("open", "owner", new PdfPermissions(), EncryptionStrength.Aes256)));

        var results = await new BatchProcessor(new RemoveHiddenInfoOperation(new SanitizeOptions()), Path.Combine(dir, "o"))
            .RunAsync([files[0], broken, locked, files[1]]);

        Assert.True(results[0].Success);
        Assert.False(results[1].Success);
        Assert.False(results[2].Success);
        Assert.Contains("password", results[2].Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(results[3].Success);
    }

    [Fact]
    public async Task Images_convert_to_pdf()
    {
        var dir = TestPdf.TempDir();
        var png = Path.Combine(dir, "photo.png");
        File.WriteAllBytes(png, TestImages.Solid(96, 96, Color.Red));
        var results = await new BatchProcessor(new ConvertToPdfOperation(), Path.Combine(dir, "pdf")).RunAsync([png]);
        using var doc = PdfiumDocument.Open(File.ReadAllBytes(results[0].Outputs[0]));
        Assert.Equal(72, doc.GetPageSize(0).Width, 1);
    }

    [Fact]
    public void Template_fields_are_filled_and_sanitized()
    {
        Assert.Equal("007_a_b", BatchProcessor.Fill("{n:000}_{title}", @"C:\x\in.pdf", 7, "a/b"));
        Assert.Equal("in", BatchProcessor.Fill("{title}", @"C:\x\in.pdf", 1, null));
        Assert.Matches(@"^in-\d{4}-\d{2}-\d{2}$", BatchProcessor.Fill("{name}-{date}", @"C:\x\in.pdf", 1, null));
    }
}
