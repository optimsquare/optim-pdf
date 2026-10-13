using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using OptimPdf.App.Services;
using OptimPdf.Core.Signatures;

namespace OptimPdf.App.Dialogs;

public sealed record DigitalSignChoice(X509Certificate2 Certificate, string? Reason, string? Location, SignatureMark? Mark, Uri? TimestampServer);

/// <summary>Chooses the digital ID and the details of a certificate-based signature.</summary>
public sealed class DigitalSignDialog : DialogBase
{
    readonly ComboBox _ids = new() { DisplayMemberPath = nameof(IdOption.Label) };
    readonly ComboBox _reason = new()
    {
        IsEditable = true,
        ItemsSource = new[] { "I approve this document", "I am the author of this document", "I have reviewed this document", "I agree to the terms of this document" },
        SelectedIndex = 0,
    };
    readonly TextBox _location = new();
    readonly CheckBox _showMark = new() { Content = "Show my handwritten signature next to the details" };
    readonly CheckBox _timestamp = new() { Content = "Add a trusted time-stamp (needs an internet connection)" };
    readonly TextBox _tsa = new() { Text = "http://timestamp.digicert.com", IsEnabled = false };
    readonly SignatureMark? _mark;

    sealed record IdOption(X509Certificate2 Certificate, string Label)
    {
        public override string ToString() => Label; // Read by screen readers.
    }

    DigitalSignDialog(Window owner, SignatureMark? mark, bool visible) : base(owner, "Sign digitally", 560, "Sign")
    {
        _mark = mark;
        var body = new StackPanel();
        body.Children.Add(Heading("Digital ID"));
        body.Children.Add(_ids);
        var idButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var create = new Button { Content = "Create a digital ID…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        var file = new Button { Content = "Use a .pfx file…", Padding = new Thickness(10, 3, 10, 3) };
        create.Click += (_, _) => CreateId();
        file.Click += (_, _) => LoadFile();
        idButtons.Children.Add(create);
        idButtons.Children.Add(file);
        body.Children.Add(idButtons);
        body.Children.Add(Note("IDs from your Windows certificate store (including smart cards) are listed. A certificate from a trusted authority " +
                               "lets others confirm your identity; a self-created ID proves the document has not changed, but others must choose to trust it."));

        body.Children.Add(Heading("Details"));
        body.Children.Add(LabeledRows(("Reason", _reason), ("Location (optional)", _location)));
        if (visible && mark is not null) { _showMark.IsChecked = true; body.Children.Add(_showMark); }
        _showMark.Margin = new Thickness(0, 6, 0, 0);

        body.Children.Add(Heading("Time-stamp"));
        body.Children.Add(_timestamp);
        _tsa.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_tsa);
        _timestamp.Checked += (_, _) => _tsa.IsEnabled = true;
        _timestamp.Unchecked += (_, _) => _tsa.IsEnabled = false;
        body.Children.Add(Note("Without a time-stamp the signing time comes from this PC's clock. With one, a time-stamp server certifies it " +
                               "(only a fingerprint of the document is sent, never its content)."));
        body.Children.Add(Note("Save the file after signing. Any later change, other than another signature, makes this signature invalid."));
        SetBody(body);
        Refresh(null);
    }

    void Refresh(X509Certificate2? select)
    {
        var options = DigitalIdStore.SigningCertificates().Select(c => new IdOption(c, DigitalIdStore.Describe(c))).ToList();
        if (select is not null && options.All(o => o.Certificate.Thumbprint != select.Thumbprint))
            options.Insert(0, new IdOption(select, DigitalIdStore.Describe(select) + " — from file"));
        _ids.ItemsSource = options;
        _ids.SelectedItem = options.FirstOrDefault(o => o.Certificate.Thumbprint == select?.Thumbprint) ?? options.FirstOrDefault();
    }

    void CreateId()
    {
        if (CreateIdDialog.Show(this) is not { } info) return;
        try { Refresh(DigitalIdStore.CreateSelfSigned(info.Name, info.Email, info.Organization)); }
        catch (CryptographicException ex) { MessageBox.Show(this, "The digital ID could not be created.\n\n" + ex.Message, "Create digital ID"); }
    }

    void LoadFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Digital ID files (*.pfx;*.p12)|*.pfx;*.p12", Title = "Choose a digital ID file" };
        if (dialog.ShowDialog(this) != true) return;
        var password = InputDialog.ShowPassword(this, "Digital ID password", $"Password for {System.IO.Path.GetFileName(dialog.FileName)}:");
        if (password is null) return;
        try
        {
            var cert = DigitalIdStore.LoadFile(dialog.FileName, password);
            if (!DigitalSignatureService.CanSign(cert)) { MessageBox.Show(this, "This file has no usable signing key, or the certificate has expired.", "Digital ID"); return; }
            Refresh(cert);
        }
        catch (CryptographicException) { MessageBox.Show(this, "The password is wrong or the file is not a digital ID.", "Digital ID"); }
    }

    protected override string? OnAccept()
    {
        if (_ids.SelectedItem is null) return "Choose a digital ID, or create one.";
        if (_timestamp.IsChecked == true && !Uri.TryCreate(_tsa.Text.Trim(), UriKind.Absolute, out _)) return "Enter the time-stamp server address (for example http://timestamp.digicert.com).";
        return null;
    }

    DigitalSignChoice Result => new(
        ((IdOption)_ids.SelectedItem).Certificate,
        string.IsNullOrWhiteSpace(_reason.Text) ? null : _reason.Text.Trim(),
        string.IsNullOrWhiteSpace(_location.Text) ? null : _location.Text.Trim(),
        _showMark.IsChecked == true ? _mark : null,
        _timestamp.IsChecked == true ? new Uri(_tsa.Text.Trim()) : null);

    public static DigitalSignChoice? Show(Window owner, SignatureMark? mark, bool visible)
    {
        var d = new DigitalSignDialog(owner, mark, visible);
        return d.ShowDialog() == true ? d.Result : null;
    }
}

/// <summary>Name, email and organization for a new self-signed digital ID.</summary>
public sealed class CreateIdDialog : DialogBase
{
    readonly TextBox _name = new(), _email = new(), _org = new();

    CreateIdDialog(Window owner) : base(owner, "Create a digital ID", 460, "Create")
    {
        var body = new StackPanel();
        body.Children.Add(LabeledRows(("Name", _name), ("Email (optional)", _email), ("Organization (optional)", _org)));
        body.Children.Add(Note("The ID is stored in your Windows certificate store (Personal), protected by your Windows account. " +
                               "It is valid for 5 years."));
        SetBody(body);
        Loaded += (_, _) => _name.Focus();
    }

    protected override string? OnAccept() => _name.Text.Trim().Length < 2 ? "Enter your name." : null;

    public static (string Name, string? Email, string? Organization)? Show(Window owner)
    {
        var d = new CreateIdDialog(owner);
        if (d.ShowDialog() != true) return null;
        static string? N(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        return (d._name.Text.Trim(), N(d._email.Text), N(d._org.Text));
    }
}
