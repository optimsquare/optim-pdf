using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;

namespace OptimPdf.Core.Forms;

public enum FormFieldKind { Text, CheckBox, RadioGroup, ComboBox, ListBox, PushButton, Signature, Unknown }

public sealed record FormFieldInfo(
    string Name,
    FormFieldKind Kind,
    string? Value,
    IReadOnlyList<string> Options,
    bool ReadOnly,
    bool Required,
    bool MultiLine,
    int PageIndex,
    PdfRect? Rect);

/// <summary>Reads and fills AcroForm fields (ISO 32000-2 §12.7). XFA-only forms are not supported.</summary>
public static class FormService
{
    public static bool HasXfa(PdfDocument doc) =>
        doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.ContainsKey("/XFA") == true;

    /// <summary>The form, or null. (PDFsharp's AcroForm getter throws when a document has none.)</summary>
    static PdfAcroForm? AcroFormOf(PdfDocument doc) =>
        doc.Internals.Catalog.Elements.ContainsKey("/AcroForm") ? doc.AcroForm : null;

    public static IReadOnlyList<FormFieldInfo> ListFields(PdfDocument doc)
    {
        var result = new List<FormFieldInfo>();
        var form = AcroFormOf(doc);
        if (form is null) return result;
        var pageOf = WidgetPages(doc);
        Collect(form.Fields, prefix: null, pageOf, result);
        return result;
    }

    /// <summary>
    /// Sets a field value. Text: any string. Check box: "true"/"false" (or the on-state name).
    /// Radio, combo and list boxes: one of the field's <see cref="FormFieldInfo.Options"/>.
    /// </summary>
    public static void SetValue(PdfDocument doc, string fieldName, string value)
    {
        var field = Find(doc, fieldName) ?? throw new KeyNotFoundException($"There is no field named \"{fieldName}\".");
        if (field.ReadOnly) throw new InvalidOperationException($"\"{fieldName}\" is read-only.");

        switch (field)
        {
            case PdfTextField text:
                int max = text.Elements.GetInteger("/MaxLen");
                if (max > 0 && value.Length > max)
                    throw new ArgumentException($"\"{fieldName}\" accepts at most {max} characters.");
                text.Elements.SetString("/V", value);
                FieldAppearance.WriteText(doc, text, value);
                break;

            case PdfCheckBoxField box:
                bool on = value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"
                          || value.TrimStart('/') == OnStateName(box);
                SetButtonState(box, on ? OnStateName(box) : null);
                break;

            case PdfRadioButtonField radio:
                if (!RadioOptions(radio).Contains(value)) throw new ArgumentException($"\"{value}\" is not an option of \"{fieldName}\".");
                SetButtonState(radio, value);
                break;

            case PdfComboBoxField combo:
                SetChoice(combo, value, fieldName, allowCustom: (combo.Flags & PdfAcroFieldFlags.Edit) != 0);
                FieldAppearance.WriteText(doc, combo, ChoiceValue(combo) ?? "");
                break;

            case PdfListBoxField list:
                SetChoice(list, value, fieldName, allowCustom: false);
                FieldAppearance.WriteText(doc, list, ChoiceValue(list) ?? "");
                break;

            default:
                throw new NotSupportedException($"\"{fieldName}\" cannot be filled in.");
        }
        // Appearances were generated, so viewers should show them as they are.
        doc.AcroForm!.Elements.SetBoolean("/NeedAppearances", false);
    }

    /// <summary>
    /// Removes interactivity: each visible field widget's appearance is drawn into its page as ordinary
    /// content, then the widgets and the form are removed.
    /// </summary>
    public static int Flatten(PdfDocument doc)
    {
        int flattened = 0;
        foreach (var page in doc.Pages)
        {
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) continue;
            var drawing = new System.Text.StringBuilder();
            var xobjects = new List<(string Name, PdfDictionary Form)>();
            for (int i = annots.Elements.Count - 1; i >= 0; i--)
            {
                if ((annots.Elements[i] as PdfReference)?.Value is not PdfDictionary w || w.Elements.GetName("/Subtype") != "/Widget") continue;
                annots.Elements.RemoveAt(i);
                flattened++;
                if ((w.Elements.GetInteger("/F") & 2) != 0) continue; // Hidden widgets leave no trace.
                if (FieldAppearance.NormalAppearance(w) is not { } form) continue;
                var r = w.Elements.GetRectangle("/Rect");
                var bbox = form.Elements.GetArray("/BBox");
                if (bbox is null || bbox.Elements.Count < 4) continue;
                double bx = bbox.Elements.GetReal(0), by = bbox.Elements.GetReal(1);
                double bw = bbox.Elements.GetReal(2) - bx, bh = bbox.Elements.GetReal(3) - by;
                if (bw <= 0 || bh <= 0) continue;
                // Map the form's BBox onto the widget rectangle (ISO 32000-2 §12.5.5, ignoring /Matrix rotation).
                double sx = (r.X2 - r.X1) / bw, sy = (r.Y2 - r.Y1) / bh;
                var name = $"OptimFlat{flattened}";
                xobjects.Add((name, form));
                drawing.Append(System.FormattableString.Invariant($"q {sx} 0 0 {sy} {r.X1 - bx * sx} {r.Y1 - by * sy} cm /{name} Do Q\n"));
            }
            if (xobjects.Count == 0) continue;

            var resources = page.Resources;
            var xo = resources.Elements.GetDictionary("/XObject");
            if (xo is null) { xo = new PdfDictionary(doc); resources.Elements["/XObject"] = xo; }
            foreach (var (name, form) in xobjects) xo.Elements[$"/{name}"] = form.Reference ?? AddIndirect(doc, form);
            page.Contents.PrependContent().CreateStream("q\n"u8.ToArray());
            page.Contents.AppendContent().CreateStream(System.Text.Encoding.ASCII.GetBytes("Q\n" + drawing));
        }
        doc.Internals.Catalog.Elements.Remove("/AcroForm");
        return flattened;
    }

    static PdfReference AddIndirect(PdfDocument doc, PdfDictionary dict)
    {
        doc.Internals.AddObject(dict);
        return dict.Reference!;
    }

    /// <summary>Sets a button field's value and each widget's appearance state (/AS).</summary>
    static void SetButtonState(PdfButtonField field, string? onState)
    {
        field.Elements.SetName("/V", onState is null ? "/Off" : "/" + onState);
        foreach (var widget in Widgets(field))
        {
            var normal = widget.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
            bool has = onState is not null && normal?.Elements.ContainsKey("/" + onState) == true;
            widget.Elements.SetName("/AS", has ? "/" + onState : "/Off");
        }
    }

    static void SetChoice(PdfChoiceField field, string value, string fieldName, bool allowCustom)
    {
        var options = ChoiceOptions(field);
        int index = options.FindIndex(o => o.Display == value || o.Export == value);
        if (index >= 0)
        {
            field.Elements.SetString("/V", options[index].Export);
            field.Elements["/I"] = PdfObjects.Numbers(field.Owner, index);
        }
        else if (allowCustom)
        {
            field.Elements.SetString("/V", value);
            field.Elements.Remove("/I");
        }
        else throw new ArgumentException($"\"{value}\" is not an option of \"{fieldName}\".");
    }

    static PdfAcroField? Find(PdfDocument doc, string name)
    {
        var form = AcroFormOf(doc);
        if (form is null) return null;
        return FindIn(form.Fields, null, name);
    }

    static PdfAcroField? FindIn(PdfAcroField.PdfAcroFieldCollection fields, string? prefix, string name)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var full = Qualify(prefix, f);
            if (full == name) return f;
            if (IsNonTerminal(f) && FindIn(f.Fields, full, name) is { } found) return found;
        }
        return null;
    }

    static void Collect(PdfAcroField.PdfAcroFieldCollection fields, string? prefix, Dictionary<PdfObjectID, int> pageOf, List<FormFieldInfo> result)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var name = Qualify(prefix, f);
            if (IsNonTerminal(f)) { Collect(f.Fields, name, pageOf, result); continue; }

            var widget = FirstWidget(f);
            int page = widget?.Reference is { } wr && pageOf.TryGetValue(wr.ObjectID, out var p) ? p : -1;
            PdfRect? rect = null;
            if (widget?.Elements.GetRectangle("/Rect") is { IsZero: false } r) rect = PdfRect.FromPoints(r.X1, r.Y1, r.X2, r.Y2);

            var (kind, value, options) = f switch
            {
                PdfTextField t => (FormFieldKind.Text, t.Elements.GetString("/V"), (IReadOnlyList<string>)[]),
                PdfCheckBoxField c => (FormFieldKind.CheckBox, IsOn(c) ? "true" : "false", ["true", "false"]),
                PdfRadioButtonField rb => (FormFieldKind.RadioGroup, CurrentRadio(rb), RadioOptions(rb)),
                PdfComboBoxField cb => (FormFieldKind.ComboBox, ChoiceValue(cb), ChoiceOptions(cb).Select(o => o.Display).ToList()),
                PdfListBoxField lb => (FormFieldKind.ListBox, ChoiceValue(lb), ChoiceOptions(lb).Select(o => o.Display).ToList()),
                PdfPushButtonField => (FormFieldKind.PushButton, null, []),
                PdfSignatureField => (FormFieldKind.Signature, null, []),
                _ => (FormFieldKind.Unknown, f.Elements.GetString("/V"), []),
            };
            result.Add(new FormFieldInfo(name, kind, value, options, f.ReadOnly,
                (f.Flags & PdfAcroFieldFlags.Required) != 0, (f.Flags & PdfAcroFieldFlags.Multiline) != 0, page, rect));
        }
    }

    /// <summary>A field whose kids are fields (not just widget annotations) only groups names.</summary>
    static bool IsNonTerminal(PdfAcroField f)
    {
        if (!f.HasKids) return false;
        var kids = f.Elements.GetArray("/Kids");
        return kids?.Elements.Any(k => (k as PdfReference)?.Value is PdfDictionary d && d.Elements.ContainsKey("/T")) == true;
    }

    static string Qualify(string? prefix, PdfAcroField f)
    {
        var partial = f.Elements.GetString("/T");
        return string.IsNullOrEmpty(prefix) ? partial : string.IsNullOrEmpty(partial) ? prefix : $"{prefix}.{partial}";
    }

    static PdfDictionary? FirstWidget(PdfAcroField f)
    {
        if (f.Elements.GetName("/Subtype") == "/Widget") return f;
        var kids = f.Elements.GetArray("/Kids");
        return kids?.Elements.Select(k => (k as PdfReference)?.Value as PdfDictionary).FirstOrDefault(d => d is not null);
    }

    /// <summary>Page of each widget, keyed by object ID (field wrappers are distinct objects from the page's entries).</summary>
    static Dictionary<PdfObjectID, int> WidgetPages(PdfDocument doc)
    {
        var map = new Dictionary<PdfObjectID, int>();
        for (int p = 0; p < doc.PageCount; p++)
        {
            var annots = doc.Pages[p].Elements.GetArray("/Annots");
            if (annots is null) continue;
            foreach (var item in annots.Elements)
                if (item is PdfReference r) map.TryAdd(r.ObjectID, p);
        }
        return map;
    }

    static bool IsOn(PdfButtonField field)
    {
        var v = field.Elements.GetName("/V");
        return !string.IsNullOrEmpty(v) && v != "/Off";
    }

    static string? OnStateName(PdfButtonField field)
    {
        foreach (var widget in Widgets(field))
        {
            var normal = widget.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
            if (normal is null) continue;
            var on = normal.Elements.Keys.FirstOrDefault(k => k != "/Off");
            if (on is not null) return on.TrimStart('/');
        }
        return "Yes";
    }

    static List<string> RadioOptions(PdfRadioButtonField radio)
    {
        var names = new List<string>();
        foreach (var widget in Widgets(radio))
        {
            var normal = widget.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
            var on = normal?.Elements.Keys.FirstOrDefault(k => k != "/Off");
            if (on is not null && !names.Contains(on.TrimStart('/'))) names.Add(on.TrimStart('/'));
        }
        return names;
    }

    static string? CurrentRadio(PdfRadioButtonField radio)
    {
        var v = radio.Elements.GetName("/V");
        return string.IsNullOrEmpty(v) || v == "/Off" ? null : v.TrimStart('/');
    }

    internal static IEnumerable<PdfDictionary> Widgets(PdfDictionary field)
    {
        var kids = field.Elements.GetArray("/Kids");
        if (kids is null) { yield return field; yield break; }
        foreach (var k in kids.Elements)
            if ((k as PdfReference)?.Value is PdfDictionary d) yield return d;
    }

    static List<(string Export, string Display)> ChoiceOptions(PdfChoiceField field)
    {
        var list = new List<(string, string)>();
        var opt = field.Elements.GetArray("/Opt");
        if (opt is null) return list;
        foreach (var item in opt.Elements)
        {
            var value = item is PdfReference r ? r.Value : item;
            if (value is PdfArray pair && pair.Elements.Count >= 2)
                list.Add((pair.Elements.GetString(0), pair.Elements.GetString(1)));
            else if (value is PdfString s)
                list.Add((s.Value, s.Value));
        }
        return list;
    }

    static string? ChoiceValue(PdfChoiceField field)
    {
        var v = field.Elements["/V"];
        string? export = v switch
        {
            PdfString s => s.Value,
            PdfArray a when a.Elements.Count > 0 => a.Elements.GetString(0),
            _ => null,
        };
        if (export is null) return null;
        var match = ChoiceOptions(field).FirstOrDefault(o => o.Export == export);
        return match.Display ?? export;
    }
}

/// <summary>Creates basic AcroForm fields. Used by tests now; field creation tools build on it in phase 3.</summary>
public static class FormBuilder
{
    public static PdfDictionary AddTextField(PdfDocument doc, int pageIndex, string name, PdfRect rect, string value = "", double fontSize = 11)
    {
        var form = EnsureAcroForm(doc);
        var field = NewWidget(doc, pageIndex, name, rect);
        field.Elements.SetName("/FT", "/Tx");
        field.Elements.SetString("/V", value);
        field.Elements.SetString("/DA", $"/Helv {fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)} Tf 0 g");
        SetLook(doc, field);
        Register(doc, form, field, pageIndex);
        FieldAppearance.WriteText(doc, field, value); // Drawn even when empty, so the field can be seen.
        return field;
    }

    /// <summary>A light background and a gray border (/MK), as created fields should be visible on the page.</summary>
    static void SetLook(PdfDocument doc, PdfDictionary field)
    {
        var mk = new PdfDictionary(doc);
        mk.Elements["/BG"] = PdfObjects.Numbers(doc, 0.93, 0.95, 1.0);
        mk.Elements["/BC"] = PdfObjects.Numbers(doc, 0.45, 0.47, 0.6);
        field.Elements["/MK"] = mk;
    }

    public static PdfDictionary AddCheckBox(PdfDocument doc, int pageIndex, string name, PdfRect rect, bool isChecked = false)
    {
        var form = EnsureAcroForm(doc);
        var field = NewWidget(doc, pageIndex, name, rect);
        field.Elements.SetName("/FT", "/Btn");
        field.Elements.SetName("/V", isChecked ? "/Yes" : "/Off");
        field.Elements.SetName("/AS", isChecked ? "/Yes" : "/Off");
        field.Elements.SetString("/DA", "/ZaDb 0 Tf 0 g");

        var box = new PdfRect(0, 0, rect.Width, rect.Height);
        var off = new ContentBuilder().StrokeColor(PdfColor.Black).LineWidth(1).Rect(0.5, 0.5, rect.Width - 1, rect.Height - 1).Stroke();
        var on = new ContentBuilder().StrokeColor(PdfColor.Black).LineWidth(1).Rect(0.5, 0.5, rect.Width - 1, rect.Height - 1).Stroke()
            .LineWidth(Math.Max(1.5, rect.Width / 8)).RoundCaps()
            .MoveTo(rect.Width * 0.2, rect.Height * 0.5).LineTo(rect.Width * 0.42, rect.Height * 0.25).LineTo(rect.Width * 0.8, rect.Height * 0.78).Stroke();
        var normal = new PdfDictionary(doc);
        normal.Elements["/Yes"] = PdfObjects.FormXObject(doc, box, on).Reference;
        normal.Elements["/Off"] = PdfObjects.FormXObject(doc, box, off).Reference;
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = normal;
        field.Elements["/AP"] = ap;

        Register(doc, form, field, pageIndex);
        return field;
    }

    /// <summary>A dropdown list; the first option is selected.</summary>
    public static PdfDictionary AddComboBox(PdfDocument doc, int pageIndex, string name, PdfRect rect, IReadOnlyList<string> options, double fontSize = 11)
    {
        var choices = options.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();
        if (choices.Count == 0) throw new ArgumentException("A dropdown needs at least one option.", nameof(options));
        var form = EnsureAcroForm(doc);
        var field = NewWidget(doc, pageIndex, name, rect);
        field.Elements.SetName("/FT", "/Ch");
        field.Elements.SetInteger("/Ff", 1 << 17); // Combo.
        var opt = new PdfArray(doc);
        foreach (var c in choices) opt.Elements.Add(new PdfString(c));
        field.Elements["/Opt"] = opt;
        field.Elements.SetString("/DA", $"/Helv {fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)} Tf 0 g");
        field.Elements.SetString("/V", choices[0]);
        field.Elements["/I"] = PdfObjects.Numbers(doc, 0);
        SetLook(doc, field);
        Register(doc, form, field, pageIndex);
        FieldAppearance.WriteText(doc, field, choices[0]);
        return field;
    }

    /// <summary>Removes a field and its widgets from the form and from their pages.</summary>
    public static bool RemoveField(PdfDocument doc, string name)
    {
        if (doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is not { } fields) return false;
        if (Find(fields, null, name) is not { } found) return false;
        found.Parent.Elements.RemoveAt(found.Index);
        var widgets = new HashSet<PdfObjectID>();
        if (found.Field.Reference is { } self) widgets.Add(self.ObjectID);
        if (found.Field.Elements.GetArray("/Kids") is { } kids)
            foreach (var k in kids.Elements.OfType<PdfReference>()) widgets.Add(k.ObjectID);
        foreach (var page in doc.Pages)
            if (page.Elements.GetArray("/Annots") is { } annots)
                for (int i = annots.Elements.Count - 1; i >= 0; i--)
                    if (annots.Elements[i] is PdfReference r && widgets.Contains(r.ObjectID)) annots.Elements.RemoveAt(i);
        return true;
    }

    static (PdfArray Parent, int Index, PdfDictionary Field)? Find(PdfArray array, string? prefix, string name)
    {
        for (int i = 0; i < array.Elements.Count; i++)
        {
            if ((array.Elements[i] as PdfReference)?.Value is not PdfDictionary d) continue;
            var partial = d.Elements.GetString("/T");
            var full = string.IsNullOrEmpty(prefix) ? partial : string.IsNullOrEmpty(partial) ? prefix : $"{prefix}.{partial}";
            if (full == name) return (array, i, d);
            if (d.Elements.GetArray("/Kids") is { } kids && Find(kids, full, name) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>An empty signature field, to be signed later (digitally) by the recipient.</summary>
    public static PdfDictionary AddSignatureField(PdfDocument doc, int pageIndex, string name, PdfRect rect)
    {
        var form = EnsureAcroForm(doc);
        var field = NewWidget(doc, pageIndex, name, rect);
        field.Elements.SetName("/FT", "/Sig");
        // A light frame with a baseline so the empty field is visible.
        var box = new PdfRect(0, 0, rect.Width, rect.Height);
        var look = new ContentBuilder().StrokeColor(new PdfColor(0.45, 0.45, 0.55)).LineWidth(0.75)
            .Rect(0.5, 0.5, rect.Width - 1, rect.Height - 1).Stroke()
            .MoveTo(rect.Width * 0.08, rect.Height * 0.3).LineTo(rect.Width * 0.92, rect.Height * 0.3).Stroke();
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = PdfObjects.FormXObject(doc, box, look).Reference;
        field.Elements["/AP"] = ap;
        Register(doc, form, field, pageIndex);
        return field;
    }

    static PdfDictionary NewWidget(PdfDocument doc, int pageIndex, string name, PdfRect rect)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('.')) throw new ArgumentException("Field names must be non-empty and contain no periods.", nameof(name));
        if (FormService.ListFields(doc).Any(f => f.Name == name)) throw new ArgumentException($"A field named \"{name}\" already exists.", nameof(name));
        var field = new PdfDictionary(doc);
        field.Elements.SetName("/Type", "/Annot");
        field.Elements.SetName("/Subtype", "/Widget");
        field.Elements.SetString("/T", name);
        field.Elements["/Rect"] = PdfObjects.Numbers(doc, rect.Left, rect.Bottom, rect.Right, rect.Top);
        field.Elements.SetInteger("/F", (int)PdfAnnotationFlags.Print);
        field.Elements.SetReference("/P", doc.Pages[pageIndex]);
        return field;
    }

    static void Register(PdfDocument doc, PdfDictionary form, PdfDictionary field, int pageIndex)
    {
        doc.Internals.AddObject(field);
        var page = doc.Pages[pageIndex];
        var annots = page.Elements.GetArray("/Annots");
        if (annots is null)
        {
            annots = new PdfArray(doc);
            page.Elements["/Annots"] = annots;
        }
        annots.Elements.Add(field.Reference!);
        form.Elements.GetArray("/Fields")!.Elements.Add(field.Reference!);
    }

    static PdfDictionary EnsureAcroForm(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog;
        var form = catalog.Elements.GetDictionary("/AcroForm");
        if (form is null)
        {
            form = new PdfDictionary(doc);
            doc.Internals.AddObject(form);
            catalog.Elements["/AcroForm"] = form.Reference;
        }
        if (form.Elements.GetArray("/Fields") is null) form.Elements["/Fields"] = new PdfArray(doc);
        if (form.Elements.GetDictionary("/DR") is null)
        {
            var dr = new PdfDictionary(doc);
            dr.Elements["/Font"] = PdfObjects.FontResources(doc);
            form.Elements["/DR"] = dr;
        }
        if (!form.Elements.ContainsKey("/DA")) form.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
        return form;
    }
}
