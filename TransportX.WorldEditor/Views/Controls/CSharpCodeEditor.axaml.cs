using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TransportX.WorldEditor.Serialization.Csx;

namespace TransportX.WorldEditor.Views.Controls;

/// <summary>
/// Reusable dark-themed C# script editor powered by AvaloniaEdit and Roslyn AST classification.
/// </summary>
public partial class CSharpCodeEditor : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CSharpCodeEditor, string>(
            nameof(Text),
            string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<CSharpCodeEditor, bool>(nameof(IsReadOnly), false);

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<CSharpCodeEditor, string>(nameof(Watermark), string.Empty);

    public static readonly StyledProperty<bool> CommitOnTextChangedProperty =
        AvaloniaProperty.Register<CSharpCodeEditor, bool>(nameof(CommitOnTextChanged), false);

    private readonly RoslynDocumentColorizer Colorizer = new();
    private bool IsUpdatingFromControl;
    private bool IsUpdatingFromProperty;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public bool CommitOnTextChanged
    {
        get => GetValue(CommitOnTextChangedProperty);
        set => SetValue(CommitOnTextChangedProperty, value);
    }

    public CSharpCodeEditor()
    {
        InitializeComponent();

        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextArea.TextView.LineTransformers.Add(Colorizer);

        Editor.TextChanged += OnEditorTextChanged;
        Editor.LostFocus += OnEditorLostFocus;

        UpdateWatermark();
    }

    /// <summary>
    /// Commits any pending text edits from the underlying editor buffer to <see cref="Text"/>.
    /// </summary>
    public void CommitPendingText()
    {
        string current = Editor.Text ?? string.Empty;
        if (!string.Equals(Text, current, StringComparison.Ordinal))
        {
            IsUpdatingFromControl = true;
            try
            {
                SetCurrentValue(TextProperty, current);
            }
            finally
            {
                IsUpdatingFromControl = false;
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && !IsUpdatingFromControl)
        {
            string nextText = change.GetNewValue<string>() ?? string.Empty;
            if (!string.Equals(Editor.Text, nextText, StringComparison.Ordinal))
            {
                IsUpdatingFromProperty = true;
                try
                {
                    Editor.Text = nextText;
                }
                finally
                {
                    IsUpdatingFromProperty = false;
                }
            }

            UpdateWatermark();
        }
        else if (change.Property == IsReadOnlyProperty)
        {
            Editor.IsReadOnly = IsReadOnly;
        }
        else if (change.Property == WatermarkProperty)
        {
            UpdateWatermark();
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        UpdateWatermark();
        if (IsUpdatingFromProperty || !CommitOnTextChanged)
        {
            return;
        }

        CommitPendingText();
    }

    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (IsUpdatingFromProperty)
        {
            return;
        }

        CommitPendingText();
    }

    private void UpdateWatermark()
    {
        string watermark = Watermark ?? string.Empty;
        bool hasText = !string.IsNullOrEmpty(Editor.Text);
        WatermarkTextBlock.Text = watermark;
        WatermarkTextBlock.IsVisible = !hasText && !string.IsNullOrEmpty(watermark);
    }

    private sealed class RoslynDocumentColorizer : DocumentColorizingTransformer
    {
        private static readonly IBrush CommentBrush = new SolidColorBrush(Color.Parse("#6A9955"));
        private static readonly IBrush StringBrush = new SolidColorBrush(Color.Parse("#CE9178"));
        private static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#B5CEA8"));
        private static readonly IBrush ControlKeywordBrush = new SolidColorBrush(Color.Parse("#C586C0"));
        private static readonly IBrush KeywordBrush = new SolidColorBrush(Color.Parse("#569CD6"));
        private static readonly IBrush TypeBrush = new SolidColorBrush(Color.Parse("#4EC9B0"));
        private static readonly IBrush MethodBrush = new SolidColorBrush(Color.Parse("#DCDCAA"));
        private static readonly IBrush ScriptGlobalBrush = new SolidColorBrush(Color.Parse("#9CDCFE"));

        private string? CachedText;
        private IReadOnlyList<CSharpHighlightSpan> CachedSpans = Array.Empty<CSharpHighlightSpan>();

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length <= 0 || CurrentContext.Document is null)
            {
                return;
            }

            IReadOnlyList<CSharpHighlightSpan> spans = GetOrClassify(CurrentContext.Document);
            if (spans.Count == 0)
            {
                return;
            }

            int lineStart = line.Offset;
            int lineEnd = line.EndOffset;

            int idx = FindFirstOverlappingIndex(spans, lineStart);
            while (idx < spans.Count)
            {
                CSharpHighlightSpan span = spans[idx];
                if (lineEnd <= span.Start)
                {
                    break;
                }

                int segStart = int.Max(lineStart, span.Start);
                int segEnd = int.Min(lineEnd, span.Start + span.Length);
                if (segStart < segEnd)
                {
                    IBrush brush = ResolveBrush(span.Kind);
                    ChangeLinePart(segStart, segEnd, element => element.TextRunProperties.SetForegroundBrush(brush));
                }

                idx++;
            }
        }

        private IReadOnlyList<CSharpHighlightSpan> GetOrClassify(TextDocument document)
        {
            string text = document.Text;
            if (!string.Equals(CachedText, text, StringComparison.Ordinal))
            {
                CachedText = text;
                CachedSpans = CSharpSyntaxHighlightClassifier.Classify(text);
            }

            return CachedSpans;
        }

        private static int FindFirstOverlappingIndex(IReadOnlyList<CSharpHighlightSpan> spans, int lineStart)
        {
            int lo = 0;
            int hi = spans.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                CSharpHighlightSpan span = spans[mid];
                if (span.Start + span.Length <= lineStart)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        private static IBrush ResolveBrush(CSharpHighlightKind kind)
        {
            return kind switch
            {
                CSharpHighlightKind.Comment => CommentBrush,
                CSharpHighlightKind.String => StringBrush,
                CSharpHighlightKind.Number => NumberBrush,
                CSharpHighlightKind.ControlKeyword => ControlKeywordBrush,
                CSharpHighlightKind.Keyword => KeywordBrush,
                CSharpHighlightKind.Type => TypeBrush,
                CSharpHighlightKind.Method => MethodBrush,
                CSharpHighlightKind.ScriptGlobal => ScriptGlobalBrush,
                _ => ScriptGlobalBrush,
            };
        }
    }
}
