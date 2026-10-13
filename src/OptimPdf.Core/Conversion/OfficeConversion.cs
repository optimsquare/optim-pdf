using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OptimPdf.Core.Conversion;

public enum OfficeApp { Word, Excel, PowerPoint }

public sealed class OfficeConversionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Office ↔ PDF conversion through the Microsoft Office installed on the PC (COM automation, run invisibly in
/// the background). Nothing is bundled: the conversion quality is Office's own, and the feature is available
/// only when the matching Office application is installed. Macros are disabled in opened files.
/// </summary>
/// <remarks>
/// Office is only quit (or stopped after a timeout) when this class started it, so a user's open Office
/// windows are never closed. Files with an open password are refused rather than prompting.
/// </remarks>
public static class OfficeConversion
{
    public static readonly IReadOnlyList<string> WordExtensions = [".docx", ".doc", ".docm", ".dotx", ".dot", ".rtf", ".odt", ".txt", ".xml", ".htm", ".html"];
    public static readonly IReadOnlyList<string> ExcelExtensions = [".xlsx", ".xls", ".xlsm", ".xlsb", ".ods", ".csv"];
    public static readonly IReadOnlyList<string> PowerPointExtensions = [".pptx", ".ppt", ".pptm", ".ppsx", ".pps", ".odp"];

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);

    static string ProgId(OfficeApp app) => app switch { OfficeApp.Word => "Word.Application", OfficeApp.Excel => "Excel.Application", _ => "PowerPoint.Application" };
    static string ProcessName(OfficeApp app) => app switch { OfficeApp.Word => "WINWORD", OfficeApp.Excel => "EXCEL", _ => "POWERPNT" };

    public static bool IsInstalled(OfficeApp app) => Type.GetTypeFromProgID(ProgId(app)) is not null;

    public static OfficeApp? AppFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (WordExtensions.Contains(ext)) return OfficeApp.Word;
        if (ExcelExtensions.Contains(ext)) return OfficeApp.Excel;
        if (PowerPointExtensions.Contains(ext)) return OfficeApp.PowerPoint;
        return null;
    }

    /// <summary>Converts a Word, Excel or PowerPoint file to PDF.</summary>
    public static Task ToPdfAsync(string input, string outputPdf, CancellationToken cancellationToken = default)
    {
        input = Path.GetFullPath(input);
        outputPdf = Path.GetFullPath(outputPdf);
        var app = AppFor(input) ?? throw new OfficeConversionException($"{Path.GetExtension(input)} files cannot be converted with Office.");
        return RunAsync(app, office =>
        {
            switch (app)
            {
                case OfficeApp.Word: WordToPdf(office, input, outputPdf); break;
                case OfficeApp.Excel: ExcelToPdf(office, input, outputPdf); break;
                default: PowerPointToPdf(office, input, outputPdf); break;
            }
        }, outputPdf, cancellationToken);
    }

    /// <summary>Converts a PDF to an editable Word document with Word's PDF reflow (Word 2013 or later).</summary>
    public static Task PdfToWordAsync(string inputPdf, string outputDocx, CancellationToken cancellationToken = default)
    {
        inputPdf = Path.GetFullPath(inputPdf);
        outputDocx = Path.GetFullPath(outputDocx);
        return RunAsync(OfficeApp.Word, word =>
        {
            // Word shows "Word will now convert your PDF…" even with alerts off; its own "don't show again"
            // setting is switched on for this conversion only and restored afterwards.
            using var _ = SuppressPdfConvertNotice((string)word.Version);
            dynamic doc = word.Documents.Open(FileName: inputPdf, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                PasswordDocument: NoPassword, Visible: false, NoEncodingDialog: true);
            try { doc.SaveAs2(FileName: outputDocx, FileFormat: 16 /* wdFormatDocumentDefault */, AddToRecentFiles: false); }
            finally { doc.Close(SaveChanges: 0); Release(doc); }
        }, outputDocx, cancellationToken);
    }

    sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }

    static IDisposable SuppressPdfConvertNotice(string version)
    {
        const string name = "DisableConvertPdfWarning";
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Office\{version}\Word\Options");
        var previous = key.GetValue(name);
        key.SetValue(name, 1, Microsoft.Win32.RegistryValueKind.DWord);
        return new Restore(() =>
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Office\{version}\Word\Options");
            if (previous is null) k.DeleteValue(name, throwOnMissingValue: false);
            else k.SetValue(name, previous, Microsoft.Win32.RegistryValueKind.DWord);
        });
    }

    // A password that no real file uses: protected files fail to open instead of showing a password prompt.
    const string NoPassword = "\u0001optim-no-password\u0001";

    static void WordToPdf(dynamic word, string input, string output)
    {
        dynamic doc = word.Documents.Open(FileName: input, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
            PasswordDocument: NoPassword, Visible: false, NoEncodingDialog: true);
        try
        {
            doc.ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17 /* wdExportFormatPDF */, OpenAfterExport: false,
                OptimizeFor: 0 /* print */, IncludeDocProps: true, CreateBookmarks: 1 /* headings */, DocStructureTags: true);
        }
        finally { doc.Close(SaveChanges: 0); Release(doc); }
    }

    static void ExcelToPdf(dynamic excel, string input, string output)
    {
        excel.ScreenUpdating = false;
        dynamic book = excel.Workbooks.Open(Filename: input, UpdateLinks: 0, ReadOnly: true, Password: NoPassword,
            IgnoreReadOnlyRecommended: true, AddToMru: false, Notify: false);
        try { book.ExportAsFixedFormat(Type: 0 /* xlTypePDF */, Filename: output, Quality: 0, IncludeDocProperties: true, IgnorePrintAreas: false, OpenAfterPublish: false); }
        finally { book.Close(SaveChanges: false); Release(book); }
    }

    static void PowerPointToPdf(dynamic powerPoint, string input, string output)
    {
        // "file::password::" makes PowerPoint fail on protected files instead of prompting.
        dynamic presentation = powerPoint.Presentations.Open(FileName: input + "::" + NoPassword + "::", ReadOnly: -1, Untitled: 0, WithWindow: 0);
        try { presentation.SaveAs(output, 32 /* ppSaveAsPDF */); }
        finally { presentation.Close(); Release(presentation); }
    }

    /// <summary>Runs an Office job on its own STA thread with a timeout; Office is shut down if this call started it.</summary>
    static Task RunAsync(OfficeApp app, Action<dynamic> job, string output, CancellationToken cancellationToken)
    {
        var type = Type.GetTypeFromProgID(ProgId(app))
                   ?? throw new OfficeConversionException($"Microsoft {app} is not installed, so this conversion is not available.");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = Pids(app);
        int? started = null;

        var thread = new Thread(() =>
        {
            dynamic? office = null;
            try
            {
                office = Activator.CreateInstance(type)!;
                started = Pids(app).Except(before).Cast<int?>().FirstOrDefault();
                if (app != OfficeApp.PowerPoint) office.Visible = false; // PowerPoint cannot be hidden; WithWindow:false instead.
                if (app == OfficeApp.Excel) office.DisplayAlerts = false;
                else office.DisplayAlerts = 0; // ppAlertsNone / wdAlertsNone
                office.AutomationSecurity = 3; // msoAutomationSecurityForceDisable: no macros.
                if (File.Exists(output)) File.Delete(output);
                job(office);
                if (!File.Exists(output)) throw new OfficeConversionException($"Microsoft {app} did not produce the output file.");
                done.TrySetResult();
            }
            catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Reflection.TargetInvocationException)
            {
                done.TrySetException(new OfficeConversionException(Explain(app, e), e));
            }
            catch (Exception e) { done.TrySetException(e); }
            finally
            {
                if (office is not null)
                {
                    try { if (started is not null) office.Quit(); } catch (COMException) { }
                    Release(office);
                }
            }
        }) { IsBackground = true, Name = $"Office {app} conversion" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return Wait();

        async Task Wait()
        {
            var timeout = Task.Delay(Timeout, cancellationToken);
            if (await Task.WhenAny(done.Task, timeout) == done.Task) { await done.Task; return; }
            // Stuck (e.g. a dialog Office could not suppress): stop the instance we started, never the user's.
            if (started is { } pid) try { Process.GetProcessById(pid).Kill(); } catch (ArgumentException) { } catch (InvalidOperationException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new OfficeConversionException($"Microsoft {app} did not finish within {Timeout.TotalMinutes:0} minutes.");
        }
    }

    static string Explain(OfficeApp app, Exception e)
    {
        var message = (e.InnerException ?? e).Message;
        if (message.Contains("password", StringComparison.OrdinalIgnoreCase))
            return "The file is password protected. Open it in Office, remove the password, and try again.";
        return $"Microsoft {app} could not convert the file: {message}";
    }

    static HashSet<int> Pids(OfficeApp app) => Process.GetProcessesByName(ProcessName(app)).Select(p => p.Id).ToHashSet();

    static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}
