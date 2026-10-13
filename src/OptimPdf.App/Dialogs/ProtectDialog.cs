using System.Windows;
using System.Windows.Controls;
using OptimPdf.Core.Security;

namespace OptimPdf.App.Dialogs;

/// <summary>Collects passwords, permissions and encryption strength for protecting a document.</summary>
public sealed class ProtectDialog : DialogBase
{
    readonly CheckBox _requireOpen = new() { Content = "Require a password to open the document", IsChecked = true };
    readonly PasswordBox _open = new(), _openConfirm = new(), _owner = new(), _ownerConfirm = new();
    readonly CheckBox _print = new() { Content = "Printing", IsChecked = true };
    readonly CheckBox _highPrint = new() { Content = "High-quality printing", IsChecked = true };
    readonly CheckBox _copy = new() { Content = "Copying text and images", IsChecked = true };
    readonly CheckBox _annotate = new() { Content = "Commenting", IsChecked = true };
    readonly CheckBox _forms = new() { Content = "Filling in forms and signing", IsChecked = true };
    readonly CheckBox _modify = new() { Content = "Changing the document", IsChecked = false };
    readonly CheckBox _assemble = new() { Content = "Inserting, deleting and rotating pages", IsChecked = false };
    readonly ComboBox _strength = new() { ItemsSource = new[] { "AES 256-bit (recommended)", "AES 128-bit (for older PDF readers)" }, SelectedIndex = 0 };

    ProtectDialog(Window owner) : base(owner, "Protect with password", 500, "Protect")
    {
        var body = new StackPanel();
        body.Children.Add(_requireOpen);
        var openRows = LabeledRows(("Open password", _open), ("Confirm", _openConfirm));
        body.Children.Add(openRows);
        _requireOpen.Checked += (_, _) => openRows.IsEnabled = true;
        _requireOpen.Unchecked += (_, _) => openRows.IsEnabled = false;

        body.Children.Add(Heading("Permissions password"));
        body.Children.Add(LabeledRows(("Permissions password", _owner), ("Confirm", _ownerConfirm)));
        body.Children.Add(Note("Needed to change the document or its security later. Keep it safe: it cannot be recovered."));

        body.Children.Add(Heading("Allow people who open the document"));
        var perms = new WrapPanel();
        foreach (var cb in new[] { _print, _highPrint, _copy, _annotate, _forms, _modify, _assemble })
        {
            cb.Margin = new Thickness(0, 2, 18, 2);
            cb.MinWidth = 200;
            perms.Children.Add(cb);
        }
        body.Children.Add(perms);
        body.Children.Add(Note("Permissions are honored by PDF apps that follow the standard. Only the open password keeps the content itself secret."));

        body.Children.Add(Heading("Encryption"));
        body.Children.Add(_strength);
        SetBody(body);
        Loaded += (_, _) => _open.Focus();
    }

    protected override string? OnAccept()
    {
        if (_requireOpen.IsChecked == true)
        {
            if (_open.Password.Length < 4) return "The open password needs at least 4 characters.";
            if (_open.Password != _openConfirm.Password) return "The open passwords don't match.";
        }
        if (_owner.Password.Length < 6) return "The permissions password needs at least 6 characters.";
        if (_owner.Password != _ownerConfirm.Password) return "The permissions passwords don't match.";
        if (_requireOpen.IsChecked == true && _open.Password == _owner.Password) return "Use different open and permissions passwords.";
        return null;
    }

    ProtectionOptions Result => new(
        _requireOpen.IsChecked == true ? _open.Password : null,
        _owner.Password,
        new PdfPermissions(_print.IsChecked == true, _highPrint.IsChecked == true && _print.IsChecked == true, _modify.IsChecked == true,
            _copy.IsChecked == true, _annotate.IsChecked == true, _forms.IsChecked == true, _assemble.IsChecked == true),
        _strength.SelectedIndex == 1 ? EncryptionStrength.Aes128 : EncryptionStrength.Aes256);

    public static ProtectionOptions? Show(Window owner)
    {
        var d = new ProtectDialog(owner);
        return d.ShowDialog() == true ? d.Result : null;
    }
}

/// <summary>Shows hidden data found in the document and lets the user choose what to remove.</summary>
public sealed class SanitizeDialog : DialogBase
{
    readonly CheckBox _info = new() { Content = "Document properties (title, author, dates, application)", IsChecked = true };
    readonly CheckBox _xmp = new() { Content = "XMP metadata", IsChecked = true };
    readonly CheckBox _files = new() { Content = "Attached files", IsChecked = true };
    readonly CheckBox _scripts = new() { Content = "JavaScript and automatic actions", IsChecked = true };
    readonly CheckBox _private = new() { Content = "Hidden application data", IsChecked = true };
    readonly CheckBox _thumbs = new() { Content = "Embedded page thumbnails", IsChecked = true };
    readonly CheckBox _comments = new() { Content = "Comments and markup", IsChecked = false };

    SanitizeDialog(Window owner, IReadOnlyList<string> findings) : base(owner, "Remove hidden information", 520, "Remove")
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = findings.Count == 0 ? "No hidden information was found." : "Found in this document:", FontWeight = FontWeights.SemiBold });
        if (findings.Count > 0)
        {
            var list = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
            foreach (var f in findings.Take(12)) list.Inlines.Add(new System.Windows.Documents.Run("• " + f + "\n"));
            if (findings.Count > 12) list.Inlines.Add(new System.Windows.Documents.Run($"… and {findings.Count - 12} more"));
            body.Children.Add(list);
        }
        body.Children.Add(Heading("Remove"));
        foreach (var cb in new[] { _info, _xmp, _files, _scripts, _private, _thumbs, _comments })
        {
            cb.Margin = new Thickness(0, 2, 0, 2);
            body.Children.Add(cb);
        }
        body.Children.Add(Note("You can undo this until you save. Removed data is not kept anywhere in the saved file."));
        SetBody(body);
    }

    SanitizeOptions Result => new(_info.IsChecked == true, _xmp.IsChecked == true, _files.IsChecked == true, _scripts.IsChecked == true,
        _private.IsChecked == true, _thumbs.IsChecked == true, _comments.IsChecked == true);

    public static SanitizeOptions? Show(Window owner, IReadOnlyList<string> findings)
    {
        var d = new SanitizeDialog(owner, findings);
        return d.ShowDialog() == true ? d.Result : null;
    }
}
