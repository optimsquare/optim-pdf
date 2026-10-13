using OptimPdf.Core.Documents;
using OptimPdf.Core.IO;
using OptimPdf.Core.Pages;

namespace OptimPdf.Core.Tests;

public class SessionTests
{
    [Fact]
    public void Undo_and_redo_restore_exact_versions()
    {
        using var session = DocumentSession.FromBytes(TestPdf.Create(3));
        var original = session.CurrentBytes;
        int changes = 0;
        session.Changed += (_, _) => changes++;

        session.Apply("Delete page", doc => PageOperations.Delete(doc, [0]));
        session.Apply("Rotate", doc => PageOperations.Rotate(doc, [0], 90));
        Assert.Equal(2, session.PageCount);
        Assert.Equal("Rotate", session.UndoDescription);

        session.Undo();
        session.Undo();
        Assert.Same(original, session.CurrentBytes);
        Assert.Equal(3, session.PageCount);
        Assert.False(session.CanUndo);
        Assert.Equal("Delete page", session.RedoDescription);

        session.Redo();
        Assert.Equal(2, session.PageCount);
        Assert.Equal(5, changes);

        // A new edit clears the redo stack.
        session.Apply("Rotate", doc => PageOperations.Rotate(doc, [0], 90));
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void Failed_edit_leaves_document_unchanged()
    {
        using var session = DocumentSession.FromBytes(TestPdf.Create(2));
        var before = session.CurrentBytes;
        Assert.Throws<InvalidOperationException>(() => session.Apply("Delete all", doc => PageOperations.Delete(doc, [0, 1])));
        Assert.Same(before, session.CurrentBytes);
        Assert.False(session.CanUndo);
        Assert.Equal(2, session.PageCount);
    }

    [Fact]
    public void Dirty_state_tracks_save_and_undo()
    {
        var dir = TestPdf.TempDir();
        var path = Path.Combine(dir, "a.pdf");
        File.WriteAllBytes(path, TestPdf.Create(2));

        using var session = DocumentSession.Open(path);
        Assert.False(session.IsDirty);
        session.Apply("Rotate", doc => PageOperations.Rotate(doc, [0], 90));
        Assert.True(session.IsDirty);
        session.Undo();
        Assert.False(session.IsDirty);

        session.Apply("Rotate", doc => PageOperations.Rotate(doc, [0], 90));
        session.Save();
        Assert.False(session.IsDirty);
        Assert.Equal(session.CurrentBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Save_keeps_original_as_backup_and_leaves_no_temp_files()
    {
        var dir = TestPdf.TempDir();
        var backups = Path.Combine(dir, "backups");
        var path = Path.Combine(dir, "report.pdf");
        var original = TestPdf.Create(1);
        File.WriteAllBytes(path, original);

        var backup = SafeFileWriter.Write(path, TestPdf.Create(2), backupDirectory: backups);
        Assert.NotNull(backup);
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(2, TestPdf.PageTitles(File.ReadAllBytes(path)).Count);
        Assert.Equal(["report.pdf"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Backups_are_pruned_per_file()
    {
        var dir = TestPdf.TempDir();
        var backups = Path.Combine(dir, "backups");
        var path = Path.Combine(dir, "x.pdf");
        File.WriteAllBytes(path, [1]);
        for (int i = 0; i < SafeFileWriter.BackupsPerFile + 3; i++)
        {
            SafeFileWriter.Write(path, [(byte)i], backupDirectory: backups);
            Thread.Sleep(5); // Backup names have millisecond resolution.
        }
        Assert.Equal(SafeFileWriter.BackupsPerFile, Directory.GetFiles(backups).Length);
    }

    [Fact]
    public void Save_as_switches_file_and_new_document_requires_name()
    {
        var dir = TestPdf.TempDir();
        using var session = DocumentSession.FromBytes(TestPdf.Create(1));
        Assert.True(session.IsDirty);
        Assert.Throws<InvalidOperationException>(() => session.Save());

        var target = Path.Combine(dir, "new.pdf");
        Assert.Null(session.SaveAs(target));
        Assert.Equal(target, session.FilePath);
        Assert.Equal("new.pdf", session.DisplayName);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Reads_and_writes_properties()
    {
        using var session = DocumentSession.FromBytes(TestPdf.Create(2, title: "Old"));
        session.Apply("Properties", doc => DocumentProperties.Write(doc, "New title", "Optim Square", null, "pdf, test"));
        var props = DocumentProperties.Read(session);
        Assert.Equal("New title", props.Title);
        Assert.Equal("Optim Square", props.Author);
        Assert.Equal("pdf, test", props.Keywords);
        Assert.Equal(2, props.PageCount);
        Assert.False(props.Encrypted);
        Assert.Equal(session.CurrentBytes.LongLength, props.FileSize);
    }
}
