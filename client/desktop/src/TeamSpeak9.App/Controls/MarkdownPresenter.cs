// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TeamSpeak9.Core.Model;
using WpfList = System.Windows.Documents.List;
using WpfListItem = System.Windows.Documents.ListItem;

namespace TeamSpeak9.App.Controls;

/// <summary>
/// Renders a parsed Markdown message, the way the official TeamSpeak 6 client formats chat.
/// </summary>
/// <remarks>
/// <para>
/// A read-only <see cref="RichTextBox"/> rather than a panel of <see cref="TextBlock"/>s because the
/// chat has to stay selectable and copyable across the whole message, including its lists and code
/// blocks — WPF only offers text selection on the text box family and on
/// <see cref="FlowDocumentScrollViewer"/>, and the latter brings its own scroll viewer that would
/// fight the message list's.
/// </para>
/// <para>
/// Colours, fonts and sizes come from the theme dictionaries by key, so this control never holds a
/// literal colour. Font and foreground are inherited properties shared between
/// <see cref="Control"/> and <see cref="TextElement"/>, which is what lets the caller restyle a
/// whole message — server notices are drawn italic and dimmed — with a plain setter on the control.
/// </para>
/// </remarks>
public class MarkdownPresenter : RichTextBox
{
    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(
        nameof(Blocks),
        typeof(ImmutableArray<MarkdownNode>),
        typeof(MarkdownPresenter),
        new FrameworkPropertyMetadata(ImmutableArray<MarkdownNode>.Empty, OnBlocksChanged));

    /// <summary>Marks the spans a click has to reveal. See <see cref="OnPreviewMouseLeftButtonDown"/>.</summary>
    private static readonly DependencyProperty IsSpoilerProperty = DependencyProperty.RegisterAttached(
        "IsSpoiler",
        typeof(bool),
        typeof(MarkdownPresenter),
        new PropertyMetadata(false));

    public MarkdownPresenter()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        AcceptsReturn = false;
        AcceptsTab = false;

        AddHandler(Hyperlink.ClickEvent, new RoutedEventHandler(OnHyperlinkClick));

        // A RichTextBox starts with a document of its own making; replacing it up front keeps every
        // message on the same padding and block layout, whether or not Blocks was ever set.
        Rebuild();
    }

    /// <summary>Raised when the reader clicks a link. Carries the target, always <c>http(s)</c>.</summary>
    public event EventHandler<string>? LinkClicked;

    /// <summary>The blocks to draw, as produced by <see cref="Markdown.Parse"/>.</summary>
    public ImmutableArray<MarkdownNode> Blocks
    {
        get => (ImmutableArray<MarkdownNode>)GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        // Hit testing rather than an event on the span itself: a text box captures the mouse for
        // selection, so input events on plain content elements are unreliable inside one.
        if (GetPositionFromPoint(e.GetPosition(this), snapToText: false)?.Parent is DependencyObject hit)
            TryReveal(hit);
    }

    /// <summary>
    /// Reveals the innermost spoiler covering <paramref name="hit"/>, if any.
    /// </summary>
    /// <returns>True when something was revealed.</returns>
    internal static bool TryReveal(DependencyObject? hit)
    {
        for (var element = hit; element is not null; element = (element as TextElement)?.Parent)
        {
            if (element is not Span span || !(bool)span.GetValue(IsSpoilerProperty))
                continue;

            span.SetValue(IsSpoilerProperty, false);
            span.ClearValue(TextElement.ForegroundProperty);
            span.ClearValue(TextElement.BackgroundProperty);
            span.ToolTip = null;
            return true;
        }

        return false;
    }

    private static void OnBlocksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownPresenter)d).Rebuild();

    private void OnHyperlinkClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Hyperlink { NavigateUri: { } uri })
            LinkClicked?.Invoke(this, uri.AbsoluteUri);
    }

    private void Rebuild()
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
        };

        // A default ImmutableArray is not the same as an empty one and throws when enumerated; a
        // binding that has not produced a value yet hands us exactly that.
        if (!Blocks.IsDefaultOrEmpty)
        {
            foreach (var node in Blocks)
            {
                if (ConvertBlock(node) is { } block)
                    document.Blocks.Add(block);
            }
        }

        Document = document;
    }

    private Block? ConvertBlock(MarkdownNode node) => node.Kind switch
    {
        MarkdownNodeKind.Paragraph => Paragraph(node),
        MarkdownNodeKind.Heading => Heading(node),
        MarkdownNodeKind.Quote => Quote(node),
        MarkdownNodeKind.List => List(node),
        MarkdownNodeKind.CodeBlock => CodeBlock(node),
        MarkdownNodeKind.Rule => Rule(),

        // A block position holding an inline node cannot happen with the current parser, but
        // wrapping beats dropping the text if that ever changes.
        _ => Paragraph(node),
    };

    private Paragraph Paragraph(MarkdownNode node)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        paragraph.Inlines.AddRange(ConvertInlines(node.Children));
        return paragraph;
    }

    private Paragraph Heading(MarkdownNode node)
    {
        int level = int.TryParse(node.Argument, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, 1, 6)
            : 1;

        var paragraph = Paragraph(node);
        paragraph.FontWeight = FontWeights.SemiBold;
        paragraph.FontSize = level switch
        {
            1 => SizeFor("Font.Size.Title", 20),
            2 => SizeFor("Font.Size.Subtitle", 16),
            3 => SizeFor("Font.Size.BodyLarge", 14),
            _ => SizeFor("Font.Size.Body", 13),
        };
        paragraph.Margin = new Thickness(0, level <= 2 ? 6 : 4, 0, 2);

        if (level <= 2 && Resource<FontFamily>("Font.Display") is { } display)
            paragraph.FontFamily = display;

        return paragraph;
    }

    private Section Quote(MarkdownNode node)
    {
        var section = new Section
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = BrushFor("Brush.BorderStrong"),
            Padding = new Thickness(8, 2, 0, 2),
            Margin = new Thickness(0, 2, 0, 2),
        };

        AddBlocks(section.Blocks, node.Children);
        return section;
    }

    private WpfList List(MarkdownNode node)
    {
        bool ordered = node.Argument.Length > 0;

        var list = new WpfList
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(20, 0, 0, 0),
        };

        if (ordered && int.TryParse(node.Argument, CultureInfo.InvariantCulture, out int start))
            list.StartIndex = Math.Max(start, 1);

        foreach (var child in node.Children)
        {
            var item = new WpfListItem();
            AddBlocks(item.Blocks, child.Children);
            list.ListItems.Add(item);
        }

        return list;
    }

    private Paragraph CodeBlock(MarkdownNode node)
    {
        var paragraph = new Paragraph
        {
            Background = BrushFor("Brush.SurfaceSunken"),
            BorderBrush = BrushFor("Brush.BorderSubtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 4, 0, 4),
            FontSize = SizeFor("Font.Size.Small", 12),
        };

        if (Resource<FontFamily>("Font.Mono") is { } mono)
            paragraph.FontFamily = mono;

        // The fence's children are only text and hard breaks, so they need no inline dispatch
        // beyond what ConvertInlines already does — but they must not pick up the code styling
        // twice, which is why the font lives on the paragraph.
        paragraph.Inlines.AddRange(ConvertInlines(node.Children));
        return paragraph;
    }

    private BlockUIContainer Rule() => new(new Border
    {
        Height = 1,
        Background = BrushFor("Brush.BorderSubtle"),
    })
    {
        Margin = new Thickness(0, 6, 0, 6),
    };

    private void AddBlocks(BlockCollection target, ImmutableArray<MarkdownNode> nodes)
    {
        if (!nodes.IsDefaultOrEmpty)
        {
            foreach (var node in nodes)
            {
                if (ConvertBlock(node) is { } block)
                    target.Add(block);
            }
        }

        // A quote or list item with no content would otherwise render as nothing at all, collapsing
        // the marker along with it.
        if (target.Count == 0)
            target.Add(new Paragraph { Margin = new Thickness(0) });
    }

    private List<Inline> ConvertInlines(ImmutableArray<MarkdownNode> nodes)
    {
        if (nodes.IsDefaultOrEmpty)
            return [];

        var inlines = new List<Inline>(nodes.Length);

        foreach (var node in nodes)
        {
            if (ConvertInline(node) is { } inline)
                inlines.Add(inline);
        }

        return inlines;
    }

    private Inline? ConvertInline(MarkdownNode node) => node.Kind switch
    {
        MarkdownNodeKind.Text => new Run(node.Text),
        MarkdownNodeKind.LineBreak => new LineBreak(),
        MarkdownNodeKind.Bold => Wrap(new Bold(), node),
        MarkdownNodeKind.Italic => Wrap(new Italic(), node),
        MarkdownNodeKind.Underline => Wrap(new Underline(), node),
        MarkdownNodeKind.Strikethrough => Wrap(new Span { TextDecorations = TextDecorations.Strikethrough }, node),
        MarkdownNodeKind.Code => CodeSpan(node),
        MarkdownNodeKind.Spoiler => Spoiler(node),
        MarkdownNodeKind.Link => Link(node),

        // Blocks never reach here; anything unknown degrades to its plain text.
        _ => new Run(Markdown.ToPlainText(node.Text)),
    };

    private Inline CodeSpan(MarkdownNode node)
    {
        var run = new Run(node.Text)
        {
            Background = BrushFor("Brush.SurfaceSunken"),
            FontSize = SizeFor("Font.Size.Small", 12),
        };

        if (Resource<FontFamily>("Font.Mono") is { } mono)
            run.FontFamily = mono;

        return run;
    }

    private Inline Spoiler(MarkdownNode node)
    {
        // Hidden by painting the text in its own background colour, so the covered run keeps its
        // width and the layout does not jump when it is revealed.
        var cover = BrushFor("Brush.BorderStrong");
        var span = Wrap(new Span { Background = cover, Foreground = cover }, node);
        span.ToolTip = "点击显示剧透内容";
        span.SetValue(IsSpoilerProperty, true);
        return span;
    }

    private Inline Link(MarkdownNode node)
    {
        if (!Markdown.IsSafeUrl(node.Argument)
            || !Uri.TryCreate(node.Argument, UriKind.Absolute, out var uri))
        {
            return Wrap(new Span(), node);
        }

        var link = new Hyperlink
        {
            NavigateUri = uri,
            Foreground = BrushFor("Brush.Accent"),
            ToolTip = uri.AbsoluteUri,
            Cursor = Cursors.Hand,
        };

        link.Inlines.AddRange(ConvertInlines(node.Children));

        if (link.Inlines.Count == 0)
            link.Inlines.Add(new Run(uri.AbsoluteUri));

        return link;
    }

    private TSpan Wrap<TSpan>(TSpan span, MarkdownNode node)
        where TSpan : Span
    {
        span.Inlines.AddRange(ConvertInlines(node.Children));
        return span;
    }

    private Brush? BrushFor(string key) => Resource<Brush>(key);

    private double SizeFor(string key, double fallback) =>
        TryFindResource(key) is double size ? size : fallback;

    /// <summary>
    /// Looks a theme resource up, tolerating its absence so the control still renders text when it
    /// is instantiated outside the application's resource scope, as the unit tests do.
    /// </summary>
    private T? Resource<T>(string key)
        where T : class => TryFindResource(key) as T;
}
