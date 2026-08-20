using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MineIDE.Services;

namespace MineIDE.Controls;

/// <summary>
/// Syntax-highlighted, editable code editor.
/// Rendering is custom (gutter, tokens, minimap); editing happens in an invisible
/// TextBox overlay that provides caret, selection, clipboard and undo for free.
/// </summary>
public class SharpCodeEditor : Control
{
    private const double GutterWidth = 48;      // line-number gutter
    private const double Gap = 8;               // gutter → text gap
    private const double TopPad = 8;
    private const double RightPad = 12;
    private const double BottomPad = 8;
    private const double LeftPad = GutterWidth + Gap;

    private TextBox? _input;
    private ScrollViewer? _scroller;
    private int _caretLine;
    private bool _suppressTextSync;
    private bool _hooked;

    static SharpCodeEditor()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SharpCodeEditor),
            new FrameworkPropertyMetadata(typeof(SharpCodeEditor)));
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(SharpCodeEditor),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnTextPropertyChanged));

    public new static readonly DependencyProperty LanguageProperty =
        DependencyProperty.Register(nameof(Language), typeof(string), typeof(SharpCodeEditor),
            new FrameworkPropertyMetadata("plaintext", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowMinimapProperty =
        DependencyProperty.Register(nameof(ShowMinimap), typeof(bool), typeof(SharpCodeEditor),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public new string Language
    {
        get => (string)GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    public bool ShowMinimap
    {
        get => (bool)GetValue(ShowMinimapProperty);
        set => SetValue(ShowMinimapProperty, value);
    }

    /// <summary>Raised on caret moves: (0-based line, 1-based column).</summary>
    public event Action<int, int>? CaretChanged;

    public SharpCodeEditor()
    {
        Focusable = false;
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New");
        FontSize = 13;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Intercept clicks that land on the minimap before the TextBox sees them.
        AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewLeftButtonDown), true);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_input != null)
        {
            _input.TextChanged -= OnInputTextChanged;
            _input.SelectionChanged -= OnInputSelectionChanged;
        }

        _input = GetTemplateChild("PART_Input") as TextBox;
        if (_input == null) return;

        _input.TextChanged += OnInputTextChanged;
        _input.SelectionChanged += OnInputSelectionChanged;
        _input.Padding = new Thickness(LeftPad, TopPad, RightPad, BottomPad);
        _input.SetResourceReference(TextBox.CaretBrushProperty, "ForegroundBrush");
        _input.SetResourceReference(TextBox.SelectionBrushProperty, "SelectionBrush");
        _input.SelectionOpacity = 0.35;

        _suppressTextSync = true;
        _input.Text = Text ?? "";
        _suppressTextSync = false;

        UpdateSize();
        UpdateCaret();
    }

    // ---------- lifecycle / scroll tracking ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hooked) return;
        _hooked = true;
        _scroller = FindAncestorScrollViewer(this);
        if (_scroller != null)
            _scroller.ScrollChanged += OnScrollChanged;
        ThemeService.Instance.ThemeChanged += OnThemeChanged;
        UpdateSize();
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_scroller != null)
            _scroller.ScrollChanged -= OnScrollChanged;
        ThemeService.Instance.ThemeChanged -= OnThemeChanged;
        _scroller = null;
        _hooked = false;
    }

    /// <summary>Repaints with the new theme colors when the user switches Dark/Light.</summary>
    private void OnThemeChanged(object? sender, AppTheme e)
    {
        InvalidateVisual();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? d)
    {
        while (d != null)
        {
            d = VisualTreeHelper.GetParent(d);
            if (d is ScrollViewer sv) return sv;
        }
        return null;
    }

    // ---------- text / caret synchronization ----------

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (SharpCodeEditor)d;
        var input = editor._input;
        if (input == null || editor._suppressTextSync) return;
        var newText = e.NewValue as string ?? "";
        if (input.Text == newText) return;

        editor._suppressTextSync = true;
        input.Text = e.NewValue as string ?? "";
        editor._suppressTextSync = false;
        editor.UpdateSize();
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextSync) return;

        _suppressTextSync = true;
        try { SetCurrentValue(TextProperty, _input?.Text ?? ""); }
        finally { _suppressTextSync = false; }

        UpdateSize();
        UpdateCaret();
    }

    private void OnInputSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateCaret();
    }

    private void UpdateCaret()
    {
        if (_input == null) return;
        try
        {
            int idx = Math.Max(0, Math.Min(_input.CaretIndex, _input.Text.Length));
            int line = Math.Max(0, _input.GetLineIndexFromCharacterIndex(idx));
            if (line != _caretLine) { _caretLine = line; InvalidateVisual(); }
            int lineStart = Math.Max(0, _input.GetCharacterIndexFromLineIndex(_caretLine));
            int col = idx - lineStart + 1;
            CaretChanged?.Invoke(_caretLine, col);
            EnsureCaretVisible(_caretLine, Math.Max(0, idx - lineStart));
        }
        catch
        {
            // TextBox internals can throw for exotic caret states; not fatal.
        }
    }

    /// <summary>The TextBox is sized to the content, so the surrounding ScrollViewer
    /// must be scrolled manually to keep the caret in view while typing.</summary>
    private void EnsureCaretVisible(int line, int colIndex)
    {
        if (_scroller == null || _input == null) return;
        try
        {
            double lineTop = TopPad + line * LineHeight;
            double lineBottom = lineTop + LineHeight;
            double top = _scroller.VerticalOffset;
            double bottom = top + _scroller.ViewportHeight;

            if (lineTop < top)
                _scroller.ScrollToVerticalOffset(Math.Max(0, lineTop - TopPad));
            else if (lineBottom > bottom)
                _scroller.ScrollToVerticalOffset(lineBottom - _scroller.ViewportHeight);

            int lineStart = Math.Max(0, _input.GetCharacterIndexFromLineIndex(line));
            double caretX = LeftPad;
            if (lineStart + colIndex <= _input.Text.Length)
            {
                string before = _input.Text.Substring(lineStart, colIndex);
                var tf = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                caretX += new FormattedText(before, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, tf, FontSize, Brushes.Black, Dpi).Width;
            }
            double left = _scroller.HorizontalOffset;
            double right = left + _scroller.ViewportWidth;
            if (caretX < left + 24)
                _scroller.ScrollToHorizontalOffset(Math.Max(0, caretX - 48));
            else if (caretX > right - 24)
                _scroller.ScrollToHorizontalOffset(caretX - _scroller.ViewportWidth + 64);
        }
        catch
        {
            // scroll adjustments are best-effort
        }
    }

    private double LineHeight
    {
        get
        {
            // Must match the TextBox's line layout exactly so the caret aligns
            // with the rendered text. TextBox line height = LineSpacing * FontSize.
            double spacing = FontFamily.LineSpacing > 0 ? FontFamily.LineSpacing : 1.17;
            return FontSize * spacing;
        }
    }

    private double Dpi
    {
        get
        {
            try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
            catch { return 96.0; }
        }
    }

    /// <summary>Sizes the TextBox (and thus the control) to the content so the
    /// surrounding ScrollViewer scrolls both the rendered layer and the input layer.</summary>
    private void UpdateSize()
    {
        if (_input == null) return;

        var text = _input.Text ?? "";
        var lines = text.Split('\n');
        double lineHeight = LineHeight;

        double maxWidth = 0;
        var tf = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        foreach (var line in lines)
        {
            var ft = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                tf, FontSize, Brushes.Black, Dpi);
            if (ft.Width > maxWidth) maxWidth = ft.Width;
        }

        double w = LeftPad + maxWidth + RightPad;
        double h = TopPad + Math.Max(1, lines.Length) * lineHeight + BottomPad;

        _input.Width = Math.Max(200, Math.Ceiling(w));
        _input.Height = Math.Max(96, Math.Ceiling(h));
    }

    // ---------- rendering ----------

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        double dpi = Dpi;

        var bg = GetColor("BgbgColor", Color.FromRgb(30, 30, 30));
        var gutterBg = GetColor("BgbgColorLight", Color.FromRgb(37, 37, 38));
        var fg = GetColor("ForegroundColor", Color.FromRgb(204, 204, 204));
        var muted = GetColor("ForegroundMutedColor", Color.FromRgb(133, 133, 133));
        var sel = GetColor("SelectionColor", Color.FromRgb(9, 71, 113));

        dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, width, height));
        dc.DrawRectangle(new SolidColorBrush(gutterBg), null, new Rect(0, 0, GutterWidth, height));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 1),
            new Point(GutterWidth, 0), new Point(GutterWidth, height));

        var text = Text;
        if (string.IsNullOrEmpty(text)) return;

        var lines = text.Split('\n');
        double lineHeight = LineHeight;
        double offsetY = _scroller?.VerticalOffset ?? 0;
        double viewportH = _scroller?.ViewportHeight ?? height;

        // Current-line highlight
        if (_caretLine >= 0 && _caretLine < lines.Length)
        {
            double y0 = TopPad + _caretLine * lineHeight;
            if (y0 < offsetY + viewportH && y0 + lineHeight > offsetY)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, sel.R, sel.G, sel.B)),
                    null, new Rect(0, y0, width, lineHeight));
            }
        }

        // Only render visible lines
        int first = Math.Max(0, (int)((offsetY - TopPad) / lineHeight) - 1);
        int last = Math.Min(lines.Length - 1, (int)((offsetY + viewportH - TopPad) / lineHeight) + 1);

        var tf = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var kwBrush = new SolidColorBrush(GetColor("SyntaxKeywordColor", Color.FromRgb(86, 156, 214)));
        var strBrush = new SolidColorBrush(GetColor("SyntaxStringColor", Color.FromRgb(206, 145, 120)));
        var numBrush = new SolidColorBrush(GetColor("SyntaxNumberColor", Color.FromRgb(181, 206, 168)));
        var cmtBrush = new SolidColorBrush(GetColor("SyntaxCommentColor", Color.FromRgb(106, 153, 85)));
        var typeBrush = new SolidColorBrush(GetColor("SyntaxTypeColor", Color.FromRgb(78, 201, 176)));
        var methodBrush = new SolidColorBrush(GetColor("SyntaxMethodColor", Color.FromRgb(220, 220, 170)));
        var defaultBrush = new SolidColorBrush(fg);
        var numBrushFmt = new SolidColorBrush(muted);

        for (int i = first; i <= last; i++)
        {
            double y = TopPad + i * lineHeight;

            var numFmt = new FormattedText((i + 1).ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, FontSize - 1, numBrushFmt, dpi);
            dc.DrawText(numFmt, new Point(GutterWidth - 4 - numFmt.Width, y));

            RenderHighlightedLine(dc, lines[i], LeftPad, y, tf, FontSize, dpi,
                kwBrush, strBrush, numBrush, cmtBrush, typeBrush, methodBrush, defaultBrush);
        }

        DrawMinimap(dc, lines, lineHeight, fg, gutterBg, dpi);
    }

    private void DrawMinimap(DrawingContext dc, string[] lines, double lineHeight, Color fg, Color gutterBg, double dpi)
    {
        if (!ShowMinimap || lines.Length == 0) return;

        double mmWidth = Math.Min(110, Math.Max(60, ActualWidth * 0.12));
        double mmLineH = Math.Max(2, lineHeight * 0.18);
        double mmContentH = lines.Length * mmLineH;

        double offsetX = _scroller?.HorizontalOffset ?? 0;
        double offsetY = _scroller?.VerticalOffset ?? 0;
        double viewW = _scroller?.ViewportWidth ?? ActualWidth;
        double viewH = _scroller?.ViewportHeight ?? ActualHeight;

        // Pin the minimap to the top-right of the *viewport* (content coordinates)
        double mmX = Math.Min(ActualWidth - mmWidth - 4, offsetX + viewW - mmWidth - 4);
        double mmY = Math.Min(Math.Max(4, ActualHeight - mmContentH - 4), offsetY + 4);

        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(45, gutterBg.R, gutterBg.G, gutterBg.B)),
            null, new Rect(mmX, mmY, mmWidth, mmContentH), 4, 4);

        // Line glyphs (only those currently inside the viewport)
        var lineBrush = new SolidColorBrush(Color.FromArgb(130, fg.R, fg.G, fg.B));
        int first = Math.Max(0, (int)((offsetY - mmY) / mmLineH));
        int last = Math.Min(lines.Length - 1, (int)((offsetY + viewH - mmY) / mmLineH));
        for (int i = first; i <= last; i++)
        {
            int chars = Math.Min(90, lines[i].Length);
            dc.DrawRectangle(lineBrush, null,
                new Rect(mmX + 4, mmY + i * mmLineH + 1, chars * 1.2, Math.Max(1, mmLineH - 1)));
        }

        // Current-line marker
        if (_caretLine >= 0 && _caretLine < lines.Length)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, fg.R, fg.G, fg.B)),
                null, new Rect(mmX + 2, mmY + _caretLine * mmLineH, mmWidth - 4, mmLineH));
        }

        // Viewport indicator
        double extentH = _scroller?.ExtentHeight ?? ActualHeight;
        if (extentH > viewH && viewH > 0)
        {
            double indH = Math.Max(12, viewH * mmContentH / extentH);
            double indY = mmY + (offsetY / (extentH - viewH)) * Math.Max(0, mmContentH - indH);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(160, fg.R, fg.G, fg.B)), 1);
            dc.DrawRectangle(null, pen, new Rect(mmX, indY, mmWidth, indH));
        }
    }

    private void OnPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ShowMinimap || _scroller == null || _input == null) return;

        var pos = e.GetPosition(this);
        var rect = ComputeMinimapRect();
        if (rect == null || !rect.Value.Contains(pos)) return;

        double mmLineH = Math.Max(2, LineHeight * 0.18);
        int line = (int)((pos.Y - rect.Value.Y) / mmLineH);
        double target = line * LineHeight - _scroller.ViewportHeight / 2;
        _scroller.ScrollToVerticalOffset(Math.Max(0, Math.Min(_scroller.ScrollableHeight, target)));
        _input.Focus();
        e.Handled = true;
    }

    private Rect? ComputeMinimapRect()
    {
        var text = Text;
        if (string.IsNullOrEmpty(text)) return null;

        double lineHeight = LineHeight;
        double mmWidth = Math.Min(110, Math.Max(60, ActualWidth * 0.12));
        double mmLineH = Math.Max(2, lineHeight * 0.18);
        double mmContentH = text.Split('\n').Length * mmLineH;

        double offsetX = _scroller?.HorizontalOffset ?? 0;
        double offsetY = _scroller?.VerticalOffset ?? 0;
        double viewW = _scroller?.ViewportWidth ?? ActualWidth;
        double viewH = _scroller?.ViewportHeight ?? ActualHeight;

        double mmX = Math.Min(ActualWidth - mmWidth - 4, offsetX + viewW - mmWidth - 4);
        double mmY = Math.Min(Math.Max(4, ActualHeight - mmContentH - 4), offsetY + 4);
        return new Rect(mmX, mmY, mmWidth, mmContentH);
    }

    private static Color GetColor(string key, Color fallback)
    {
        if (Application.Current?.Resources[key] is Color c) return c;
        return fallback;
    }

    // ---------- syntax highlighting ----------

    private void RenderHighlightedLine(DrawingContext dc, string line, double x, double y, Typeface tf, double fs,
        double dpi, Brush keywordBrush, Brush stringBrush, Brush numberBrush, Brush commentBrush,
        Brush typeBrush, Brush methodBrush, Brush defaultBrush)
    {
        if (string.IsNullOrEmpty(line)) return;

        var commentStart = FindCommentStart(line, Language);
        if (commentStart >= 0)
        {
            var fmt = new FormattedText(line, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, fs, commentBrush, dpi);
            dc.DrawText(fmt, new Point(x, y));
            return;
        }

        double cur = x;
        foreach (var tok in Tokenize(line, Language))
        {
            Brush b = tok.Kind switch
            {
                TokenKind.Keyword => keywordBrush,
                TokenKind.String => stringBrush,
                TokenKind.Number => numberBrush,
                TokenKind.Comment => commentBrush,
                TokenKind.Type => typeBrush,
                TokenKind.Method => methodBrush,
                _ => defaultBrush
            };
            var ft = new FormattedText(tok.Text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, fs, b, dpi);
            dc.DrawText(ft, new Point(cur, y));
            cur += ft.Width;
        }
    }

    private static int FindCommentStart(string line, string lang)
    {
        if (lang is "java" or "csharp" or "javascript" or "typescript" or "kotlin" or "groovy")
        {
            int idx = line.IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0) return idx;
        }
        if (line.TrimStart().StartsWith("#", StringComparison.Ordinal) &&
            lang is "python" or "ini" or "groovy")
            return line.IndexOf('#');
        return -1;
    }

    private enum TokenKind { Keyword, String, Number, Comment, Type, Method, Default }

    private sealed class Tok
    {
        public string Text = "";
        public TokenKind Kind = TokenKind.Default;
    }

    private static readonly Regex TokenRegex = new(
        @"\b(class|public|private|protected|static|void|int|long|float|double|boolean|char|byte|short|if|else|for|while|return|new|this|super|final|abstract|interface|extends|implements|throws|try|catch|finally|import|package|null|true|false|var|val|fun|object)\b" +
        @"|\b(String|List|Map|Array|HashMap|ArrayList|Optional|Stream|Supplier|Function|RegistryObject|DeferredRegister|ResourceLocation|IEventBus|MinecraftForge|FMLJavaModLoadingContext)\b" +
        @"|""[^""\r\n]*""|'[^'\r\n]*'" +
        @"|\b\d+(\.\d+)?[fLdD]?\b",
        RegexOptions.Compiled);

    private static readonly Regex MethodRegex = new(@"([a-zA-Z_]\w*)\s*\(", RegexOptions.Compiled);

    private static System.Collections.Generic.List<Tok> Tokenize(string line, string lang)
    {
        var list = new System.Collections.Generic.List<Tok>();
        if (string.IsNullOrEmpty(line)) return list;

        int i = 0;
        while (i < line.Length)
        {
            var m = TokenRegex.Match(line, i);
            if (m.Success && m.Index == i)
            {
                var kind = TokenKind.Default;
                if (m.Value.StartsWith("\"", StringComparison.Ordinal) || m.Value.StartsWith("'", StringComparison.Ordinal))
                    kind = TokenKind.String;
                else if (char.IsDigit(m.Value[0]))
                    kind = TokenKind.Number;
                else if (IsKeyword(m.Value))
                    kind = TokenKind.Keyword;
                else if (IsType(m.Value))
                    kind = TokenKind.Type;
                list.Add(new Tok { Text = m.Value, Kind = kind });
                i += m.Length;
                continue;
            }

            var lm = MethodRegex.Match(line, i);
            if (lm.Success && lm.Index == i)
            {
                list.Add(new Tok { Text = lm.Groups[1].Value + "(", Kind = TokenKind.Method });
                i += lm.Length;
                continue;
            }

            int j = i;
            while (j < line.Length)
            {
                var sub = line.Substring(j);
                if ((TokenRegex.IsMatch(sub) && TokenRegex.Match(sub).Index == 0) ||
                    (MethodRegex.IsMatch(sub) && MethodRegex.Match(sub).Index == 0))
                    break;
                j++;
            }
            if (j == i) j++;
            list.Add(new Tok { Text = line.Substring(i, j - i), Kind = TokenKind.Default });
            i = j;
        }
        return list;
    }

    private static bool IsKeyword(string w) => w switch
    {
        "class" or "public" or "private" or "protected" or "static" or "void" or "int" or "long" or "float" or "double"
            or "boolean" or "char" or "byte" or "short" or "if" or "else" or "for" or "while" or "return" or "new"
            or "this" or "super" or "final" or "abstract" or "interface" or "extends" or "implements" or "throws"
            or "try" or "catch" or "finally" or "import" or "package" or "null" or "true" or "false"
            or "var" or "val" or "fun" or "object" => true,
        _ => false
    };

    private static bool IsType(string w) => w switch
    {
        "String" or "List" or "Map" or "Array" or "HashMap" or "ArrayList" or "Optional" or "Stream"
            or "Supplier" or "Function" or "RegistryObject" or "DeferredRegister" or "ResourceLocation"
            or "IEventBus" or "MinecraftForge" or "FMLJavaModLoadingContext" => true,
        _ => false
    };
}
