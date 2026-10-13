using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using OptimPdf.App.ViewModels;
using OptimPdf.Core.Rendering;

namespace OptimPdf.App.Views;

/// <summary>
/// One rendered page with an interaction overlay. Converts mouse gestures to PDF coordinates and
/// forwards them to the owning <see cref="DocumentView"/>, which applies the active tool.
/// </summary>
public sealed class PageView : Grid
{
    // PDFium maps integer device pixels; a 4x virtual surface keeps mapping precise to a quarter DIP.
    const double MapPrecision = 4;

    readonly Image _image = new() { Stretch = Stretch.Fill };
    readonly Canvas _overlay = new() { Background = Brushes.Transparent, ClipToBounds = true };
    readonly DocumentView _host;
    Point _dragStart;
    bool _dragging;
    Rectangle? _dragRect;
    Polyline? _ink;

    // Moving or resizing the selected item.
    enum Gesture { None, Move, Resize }
    Gesture _gesture;
    Point _anchor;          // Fixed corner while resizing (view coordinates).
    Rect _selectionView;    // Selection rectangle when the gesture started.
    Rectangle? _ghost;
    const double HandleSize = 9;

    public PageView(DocumentView host, PageViewModel page)
    {
        _host = host;
        Page = page;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _image.SetBinding(Image.SourceProperty, new Binding(nameof(PageViewModel.Image)) { Source = page });

        var sheet = new Border
        {
            Background = Brushes.White,
            Child = _image,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1.5, Opacity = 0.28, Direction = 270 },
        };
        Children.Add(sheet);
        Children.Add(_overlay);
        UpdateSize();
    }

    public PageViewModel Page { get; }
    DocumentViewModel Doc => _host.ViewModel;

    /// <summary>Exposes each page to screen readers and UI Automation as "Page N".</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new PagePeer(this);

    sealed class PagePeer(PageView owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => $"Page {owner.Page.Number}";
        protected override string GetClassNameCore() => "PageView";
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
            System.Windows.Automation.Peers.AutomationControlType.Image;
    }
    PdfiumDocument Renderer => Doc.Session.Renderer;

    public void UpdateSize()
    {
        Width = Page.DisplayWidth;
        Height = Page.DisplayHeight;
    }

    (int W, int H) VirtualSize => ((int)Math.Round(Width * MapPrecision), (int)Math.Round(Height * MapPrecision));

    public (double X, double Y) ToPdf(Point p)
    {
        var (w, h) = VirtualSize;
        return Renderer.DeviceToPage(Page.Index, w, h, Doc.ViewRotation,
            (int)Math.Round(p.X * MapPrecision), (int)Math.Round(p.Y * MapPrecision));
    }

    public Point ToView(double x, double y)
    {
        var (w, h) = VirtualSize;
        var (dx, dy) = Renderer.PageToDevice(Page.Index, w, h, Doc.ViewRotation, x, y);
        return new Point(dx / MapPrecision, dy / MapPrecision);
    }

    public Rect ToView(PdfRect r)
    {
        var a = ToView(r.Left, r.Bottom);
        var b = ToView(r.Right, r.Top);
        return new Rect(a, b);
    }

    /// <summary>Redraws search hits and the text selection for this page.</summary>
    public void RedrawOverlays()
    {
        for (int i = _overlay.Children.Count - 1; i >= 0; i--)
            if (_overlay.Children[i] is FrameworkElement { Tag: "overlay" }) _overlay.Children.RemoveAt(i);

        var results = Doc.SearchResults;
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Hit.PageIndex != Page.Index) continue;
            var brush = (Brush)FindResource(i == Doc.CurrentHit ? "Viewer.SearchHitCurrent" : "Viewer.SearchHit");
            foreach (var r in results[i].Hit.Rects) AddOverlayRect(ToView(r), brush);
        }

        if (Doc.Selection is { } sel && sel.PageIndex == Page.Index)
        {
            var brush = (Brush)FindResource("Viewer.Selection");
            foreach (var r in sel.Rects) AddOverlayRect(ToView(r), brush);
        }

        // Form fields, tinted so they are easy to spot (as other PDF apps do); display only.
        if (Doc.HighlightFields)
        {
            var tint = new SolidColorBrush(Color.FromArgb(40, 11, 92, 214));
            foreach (var f in Doc.Fields)
                if (f.Info.PageIndex == Page.Index && f.Info.Rect is { } fr && f.Editable)
                    AddOverlayRect(ToView(fr), tint);
        }

        // The selected item: dashed outline with corner handles.
        if (Doc.SelectedObject is { } so && so.PageIndex == Page.Index)
        {
            var r = ToView(so.Bounds);
            r.Inflate(3, 3);
            var accent = (Brush)FindResource("Brand.Indigo");
            var outline = new Rectangle { Width = r.Width, Height = r.Height, Stroke = accent, StrokeThickness = 1.5, StrokeDashArray = [4, 2], Tag = "overlay", IsHitTestVisible = false };
            Canvas.SetLeft(outline, r.X);
            Canvas.SetTop(outline, r.Y);
            _overlay.Children.Add(outline);
            foreach (var c in Corners(r))
            {
                var handle = new Rectangle { Width = HandleSize, Height = HandleSize, Fill = Brushes.White, Stroke = accent, StrokeThickness = 1.5, Tag = "overlay", IsHitTestVisible = false };
                Canvas.SetLeft(handle, c.X - HandleSize / 2);
                Canvas.SetTop(handle, c.Y - HandleSize / 2);
                _overlay.Children.Add(handle);
            }
        }

        // Areas marked for redaction: red outline over a light red tint, so the content stays readable until applied.
        foreach (var area in Doc.Redactions)
        {
            if (area.PageIndex != Page.Index) continue;
            var r = ToView(area.Rect);
            var mark = new Rectangle
            {
                Width = Math.Max(1, r.Width), Height = Math.Max(1, r.Height), Tag = "overlay", IsHitTestVisible = false,
                Stroke = new SolidColorBrush(Color.FromRgb(220, 38, 38)), StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(56, 220, 38, 38)),
            };
            Canvas.SetLeft(mark, r.X);
            Canvas.SetTop(mark, r.Y);
            _overlay.Children.Add(mark);
        }
    }

    Control? _editor;

    /// <summary>
    /// Shows an in-place editor over a line of existing text. Enter or clicking elsewhere commits, Esc cancels.
    /// <paramref name="commit"/> receives the new text, or null when cancelled or unchanged.
    /// </summary>
    public void ShowTextEditor(PdfRect bounds, string text, double fontSizePt, bool rightToLeft, Action<string?> commit)
    {
        CloseTextEditor();
        var r = ToView(bounds);
        double size = Math.Max(8, fontSizePt * DocumentViewModel.DipPerPoint * Doc.Zoom);
        var box = new TextBox
        {
            Text = text, FontSize = size, MinWidth = Math.Max(80, r.Width + 40), Padding = new Thickness(2, 0, 2, 0),
            VerticalContentAlignment = VerticalAlignment.Center, Background = Brushes.White, Foreground = Brushes.Black,
            BorderBrush = (Brush)FindResource("Brand.Indigo"), BorderThickness = new Thickness(1.5),
            FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            ToolTip = "Enter to apply · Esc to cancel",
        };
        Canvas.SetLeft(box, Math.Max(0, r.X - 3));
        Canvas.SetTop(box, Math.Max(0, r.Y - 3));
        box.Height = Math.Max(r.Height + 6, size * 1.45);
        bool done = false;
        void Finish(bool apply)
        {
            if (done) return;
            done = true;
            var value = box.Text;
            CloseTextEditor();
            commit(apply && value != text ? value : null);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        _editor = box;
        _overlay.Children.Add(box);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    /// <summary>
    /// A box for typing new text right on the page, in the chosen font, size and color. Enter adds a line; Ctrl+Enter or
    /// clicking elsewhere places the text; Esc cancels. <paramref name="widthPt"/> wraps the text (from a dragged box).
    /// </summary>
    public void ShowNewTextEditor((double X, double Y) topLeft, double? widthPt, Core.Editing.TextStyle style, Action<string?> commit)
    {
        CloseTextEditor();
        var at = ToView(topLeft.X, topLeft.Y);
        double scale = DocumentViewModel.DipPerPoint * Doc.Zoom;
        var c = style.EffectiveColor;
        var box = new TextBox
        {
            FontFamily = new FontFamily(style.FontFamily), FontSize = Math.Max(6, style.Size * scale),
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255))),
            Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), AcceptsReturn = true, Padding = new Thickness(0),
            BorderBrush = (Brush)FindResource("Brand.Indigo"), BorderThickness = new Thickness(1), MinWidth = 60 * Doc.Zoom,
            TextWrapping = widthPt is null ? TextWrapping.NoWrap : TextWrapping.Wrap,
            ToolTip = "Type your text · Enter for a new line · Ctrl+Enter or click outside to place · Esc to cancel",
        };
        if (widthPt is { } w) box.Width = Math.Max(40, w * scale + 2);
        System.Windows.Automation.AutomationProperties.SetName(box, "New text");
        Canvas.SetLeft(box, at.X - 1);
        Canvas.SetTop(box, at.Y - 1);
        bool done = false;
        void Finish(bool apply)
        {
            if (done) return;
            done = true;
            var value = box.Text;
            CloseTextEditor();
            commit(apply && value.Trim().Length > 0 ? value : null);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        _editor = box;
        _overlay.Children.Add(box);
        box.Loaded += (_, _) => box.Focus();
    }

    /// <summary>
    /// Fills a form field in place: a text box (or a dropdown list) exactly over the field. Enter or clicking elsewhere
    /// saves; Tab saves and moves to the next field; Esc cancels. <paramref name="commit"/> gets (value, moveToNext).
    /// </summary>
    public void ShowFieldEditor(PdfRect bounds, string value, bool multiline, IReadOnlyList<string>? options, bool editableChoice,
        Action<string?, int> commit)
    {
        CloseTextEditor();
        bool done = false;
        var r = ToView(bounds);
        double fontSize = Math.Clamp(r.Height * (multiline ? 0.42 : 0.62), 9, 22);
        Control editor;
        Func<string> read;
        if (options is not null)
        {
            var combo = new ComboBox { ItemsSource = options, IsEditable = editableChoice, Text = value, SelectedItem = options.Contains(value) ? value : null, FontSize = fontSize };
            editor = combo;
            read = () => combo.Text;
            combo.DropDownClosed += (_, _) => { if (combo.SelectedItem is string) Done(0, true); };
            combo.Loaded += (_, _) => { combo.Focus(); combo.IsDropDownOpen = true; };
        }
        else
        {
            var box = new TextBox
            {
                Text = value, FontSize = fontSize, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center, Padding = new Thickness(2, 0, 2, 0),
            };
            editor = box;
            read = () => box.Text;
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        }
        editor.Width = Math.Max(40, r.Width + 2);
        editor.Height = Math.Max(22, r.Height + 2);
        editor.Background = Brushes.White;
        editor.Foreground = Brushes.Black;
        editor.BorderBrush = (Brush)FindResource("Brand.Indigo");
        editor.BorderThickness = new Thickness(1.5);
        editor.ToolTip = multiline ? "Ctrl+Enter or click outside to save · Tab: next field · Esc: cancel" : "Enter to save · Tab: next field · Esc: cancel";
        System.Windows.Automation.AutomationProperties.SetName(editor, "Field value");
        Canvas.SetLeft(editor, r.X - 1);
        Canvas.SetTop(editor, r.Y - 1);

        void Done(int move, bool apply)
        {
            if (done) return;
            done = true;
            var text = read();
            CloseTextEditor();
            commit(apply && text != value ? text : null, move);
        }
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab) { Done(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1, true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Done(0, false); e.Handled = true; }
            else if (e.Key == Key.Enter && (!multiline || Keyboard.Modifiers == ModifierKeys.Control)) { Done(0, true); e.Handled = true; }
        };
        editor.LostKeyboardFocus += (_, e) =>
        {
            // Focus moving into the dropdown's own popup is not "leaving" the field.
            if (e.NewFocus is DependencyObject d && IsInside(d, editor)) return;
            if (editor is ComboBox { IsDropDownOpen: true }) return;
            Done(0, true);
        };
        _editor = editor;
        _overlay.Children.Add(editor);
    }

    void CloseTextEditor()
    {
        if (_editor is null) return;
        _overlay.Children.Remove(_editor);
        _editor = null;
    }

    void AddOverlayRect(Rect r, Brush brush)
    {
        var shape = new Rectangle { Width = r.Width + 2, Height = r.Height + 2, Fill = brush, Tag = "overlay", IsHitTestVisible = false };
        Canvas.SetLeft(shape, r.X - 1);
        Canvas.SetTop(shape, r.Y - 1);
        _overlay.Children.Add(shape);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Cursor = BaseCursor();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_editor is not null && e.OriginalSource is DependencyObject source && IsInside(source, _editor)) return; // Typing in the editor.
        Focus();
        _dragStart = e.GetPosition(this);
        _dragging = true;
        CaptureMouse();

        // Pressing on the selected item moves it; pressing on a corner handle resizes it.
        _gesture = HitSelection(_dragStart, out var anchor);
        if (_gesture != Gesture.None)
        {
            _anchor = anchor;
            _selectionView = SelectionRect()!.Value;
            _ghost = new Rectangle
            {
                Width = _selectionView.Width, Height = _selectionView.Height, Stroke = (Brush)FindResource("Brand.Indigo"), StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(30, 11, 92, 214)), IsHitTestVisible = false,
            };
            Canvas.SetLeft(_ghost, _selectionView.X);
            Canvas.SetTop(_ghost, _selectionView.Y);
            _overlay.Children.Add(_ghost);
            e.Handled = true;
            return;
        }

        if (ToolState.Mode == ToolMode.Ink)
        {
            _ink = new Polyline
            {
                Stroke = new SolidColorBrush(ToolState.MediaColor), StrokeThickness = ToolState.InkWidth * DocumentViewModel.DipPerPoint * Doc.Zoom,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            };
            _ink.Points.Add(_dragStart);
            _overlay.Children.Add(_ink);
        }
        else if (ToolState.Mode is ToolMode.FreeText or ToolMode.Crop or ToolMode.AddImage or ToolMode.Sign or ToolMode.AddText
                 or ToolMode.Redact or ToolMode.TextField or ToolMode.CheckBoxField or ToolMode.DropdownField or ToolMode.SignatureField
                 or ToolMode.DigitalSign)
        {
            _dragRect = new Rectangle
            {
                Stroke = (Brush)FindResource("Brand.Indigo"), StrokeThickness = 1.5, StrokeDashArray = [4, 3],
                Fill = new SolidColorBrush(Color.FromArgb(28, 11, 92, 214)), IsHitTestVisible = false,
            };
            _overlay.Children.Add(_dragRect);
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) { UpdateHoverCursor(e.GetPosition(this)); return; }
        var p = Clamp(e.GetPosition(this));

        if (_gesture != Gesture.None && _ghost is not null)
        {
            var r = _gesture == Gesture.Move ? Rect.Offset(_selectionView, p - _dragStart) : ScaledRect(p);
            Canvas.SetLeft(_ghost, r.X);
            Canvas.SetTop(_ghost, r.Y);
            _ghost.Width = r.Width;
            _ghost.Height = r.Height;
            return;
        }

        if (_ink is not null)
        {
            // Skip sub-pixel moves to keep the stroke light.
            if ((p - _ink.Points[^1]).Length >= 1.5) _ink.Points.Add(p);
        }
        else if (_dragRect is not null)
        {
            var r = new Rect(_dragStart, p);
            Canvas.SetLeft(_dragRect, r.X);
            Canvas.SetTop(_dragRect, r.Y);
            _dragRect.Width = r.Width;
            _dragRect.Height = r.Height;
        }
        else if (ToolState.Mode is ToolMode.Select or ToolMode.Highlight or ToolMode.Underline or ToolMode.StrikeOut)
        {
            _host.UpdateSelection(this, ToPdf(_dragStart), ToPdf(p));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var end = Clamp(e.GetPosition(this));
        bool isClick = (end - _dragStart).Length < 4;

        if (_gesture != Gesture.None)
        {
            var gesture = _gesture;
            _gesture = Gesture.None;
            if (_ghost is not null) { _overlay.Children.Remove(_ghost); _ghost = null; }
            if (!isClick)
            {
                if (gesture == Gesture.Move)
                {
                    var a0 = ToPdf(_dragStart);
                    var a1 = ToPdf(end);
                    _host.MoveSelected(a1.X - a0.X, a1.Y - a0.Y);
                }
                else
                {
                    double factor = ScaleFactor(end);
                    _host.ResizeSelected(factor, ToPdf(_anchor));
                }
            }
            e.Handled = true;
            return;
        }

        if (_ink is not null)
        {
            var points = _ink.Points.Select(ToPdf).ToList();
            _overlay.Children.Remove(_ink);
            _ink = null;
            _host.FinishInk(this, points);
        }
        else
        {
            if (_dragRect is not null)
            {
                _overlay.Children.Remove(_dragRect);
                _dragRect = null;
            }
            var a = ToPdf(_dragStart);
            if (isClick) _host.PageClicked(this, a);
            else
            {
                var b = ToPdf(end);
                _host.PageDragged(this, PdfRect.FromPoints(a.X, a.Y, b.X, b.Y));
            }
        }
        e.Handled = true;
    }

    Rect? SelectionRect()
    {
        if (Doc.SelectedObject is not { } so || so.PageIndex != Page.Index) return null;
        var r = ToView(so.Bounds);
        r.Inflate(3, 3);
        return r;
    }

    static Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft];

    /// <summary>Which part of the selection is under a point; for a handle, the opposite corner is the resize anchor.</summary>
    Gesture HitSelection(Point p, out Point anchor)
    {
        anchor = default;
        if (SelectionRect() is not { } r) return Gesture.None;
        if (ToolState.Mode is not (ToolMode.SelectObject or ToolMode.AddText or ToolMode.AddImage or ToolMode.Sign or ToolMode.Select or ToolMode.FreeText or ToolMode.Stamp))
            return Gesture.None;
        var corners = Corners(r);
        var exact = Corners(ToView(Doc.SelectedObject!.Bounds)); // The anchor is the item's own corner, not the outline's.
        for (int i = 0; i < 4; i++)
            if (Math.Abs(p.X - corners[i].X) <= HandleSize && Math.Abs(p.Y - corners[i].Y) <= HandleSize)
            {
                anchor = exact[(i + 2) % 4];
                return Gesture.Resize;
            }
        return r.Contains(p) ? Gesture.Move : Gesture.None;
    }

    /// <summary>Uniform scale from dragging a corner away from (or toward) the anchor.</summary>
    double ScaleFactor(Point p)
    {
        var start = _dragStart - _anchor;
        var now = p - _anchor;
        double s = start.Length < 1 ? 1 : Math.Max(Math.Abs(now.X) / Math.Max(1, Math.Abs(start.X)), Math.Abs(now.Y) / Math.Max(1, Math.Abs(start.Y)));
        return Math.Clamp(s, 0.1, 10);
    }

    Rect ScaledRect(Point p)
    {
        double s = ScaleFactor(p);
        var corners = Corners(_selectionView).Select(c => _anchor + (c - _anchor) * s).ToList();
        return new Rect(new Point(corners.Min(c => c.X), corners.Min(c => c.Y)), new Point(corners.Max(c => c.X), corners.Max(c => c.Y)));
    }

    /// <summary>Shows what a click will do: move/resize over the selection, type over a text field, toggle over a check box.</summary>
    void UpdateHoverCursor(Point p)
    {
        var g = HitSelection(p, out var anchor);
        if (g == Gesture.Resize)
        {
            var r = SelectionRect()!.Value;
            var exact = ToView(Doc.SelectedObject!.Bounds);
            bool mainDiagonal = (anchor == exact.TopLeft) || (anchor == exact.BottomRight);
            Cursor = mainDiagonal ? Cursors.SizeNWSE : Cursors.SizeNESW;
            return;
        }
        if (g == Gesture.Move) { Cursor = Cursors.SizeAll; return; }
        if (ToolState.Mode == ToolMode.Select && _host.FieldAt(Page.Index, ToPdf(p)) is { } field)
        {
            Cursor = field.IsText || field.IsChoice ? Cursors.IBeam : Cursors.Hand;
            return;
        }
        Cursor = BaseCursor();
    }

    static Cursor BaseCursor() => ToolState.Mode switch
    {
        ToolMode.Select or ToolMode.Highlight or ToolMode.Underline or ToolMode.StrikeOut => Cursors.IBeam,
        ToolMode.Ink => Cursors.Pen,
        ToolMode.AddText or ToolMode.EditText => Cursors.IBeam,
        ToolMode.SelectObject => Cursors.Arrow,
        _ => Cursors.Cross,
    };

    static bool IsInside(DependencyObject node, DependencyObject container)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n) ?? LogicalTreeHelper.GetParent(n))
            if (ReferenceEquals(n, container)) return true;
        return false;
    }

    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, ActualWidth), Math.Clamp(p.Y, 0, ActualHeight));
}

/// <summary>The active tool and its options, shared by all document tabs.</summary>
public static class ToolState
{
    public static ToolMode Mode { get; set; } = ToolMode.Select;
    public static Core.Writing.PdfColor? ColorOverride { get; set; }
    public static string StampLabel { get; set; } = "APPROVED";
    public static double InkWidth { get; set; } = 2;

    /// <summary>Font for the Add text tool.</summary>
    public static Core.Editing.TextStyle TextStyle { get; set; } = new("Segoe UI", 12);

    /// <summary>Signature placed by the Sign tool.</summary>
    public static Services.SavedSignature? Signature { get; set; }

    /// <summary>The color for the current tool: the user's pick, or the tool's default.</summary>
    public static Core.Writing.PdfColor Color => ColorOverride ?? Mode switch
    {
        ToolMode.Highlight => Core.Writing.PdfColor.Yellow,
        ToolMode.StrikeOut => Core.Writing.PdfColor.Red,
        ToolMode.Underline => Core.Writing.PdfColor.Green,
        ToolMode.FreeText => Core.Writing.PdfColor.Black,
        ToolMode.Ink => Core.Writing.PdfColor.Blue,
        _ => Core.Writing.PdfColor.Blue,
    };

    public static Color MediaColor => System.Windows.Media.Color.FromRgb(
        (byte)(Color.R * 255), (byte)(Color.G * 255), (byte)(Color.B * 255));

    public static Core.Writing.PdfColor StampColor => StampLabel switch
    {
        "APPROVED" or "FINAL" => Core.Writing.PdfColor.Green,
        "REJECTED" or "CONFIDENTIAL" => Core.Writing.PdfColor.Red,
        _ => Core.Writing.PdfColor.Blue,
    };
}
