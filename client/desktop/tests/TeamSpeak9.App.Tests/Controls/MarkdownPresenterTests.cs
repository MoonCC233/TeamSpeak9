// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Immutable;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using TeamSpeak9.App.Controls;
using TeamSpeak9.App.Tests.Infrastructure;
using TeamSpeak9.Core.Model;
using WpfList = System.Windows.Documents.List;

namespace TeamSpeak9.App.Tests.Controls;

/// <summary>
/// The Markdown AST to <see cref="FlowDocument"/> mapping.
/// </summary>
/// <remarks>
/// <para>
/// Every case runs on an STA thread because a <see cref="MarkdownPresenter"/> is a
/// <c>DispatcherObject</c>, and the assertions run <em>inside</em> that thread: a WPF object may only
/// be read from the thread that created it, so handing the document back to the xunit thread would
/// only produce cross-thread failures.
/// </para>
/// <para>
/// The presenter is built outside any application resource scope, which also keeps proving the theme
/// lookups tolerate missing keys rather than throwing. The two cases that care about the theme inject
/// the keys into the control's own <see cref="FrameworkElement.Resources"/>.
/// </para>
/// </remarks>
public class MarkdownPresenterTests
{
    private static void WithBlocks(string markup, Action<BlockCollection> assert) =>
        Sta.Run(() => assert(new MarkdownPresenter { Blocks = Markdown.Parse(markup) }.Document.Blocks));

    private static void WithInlines(string markup, Action<InlineCollection> assert) =>
        Sta.Run(() =>
        {
            var document = new MarkdownPresenter { Blocks = Markdown.Parse(markup) }.Document;
            assert(((Paragraph)document.Blocks.First()).Inlines);
        });

    private static string TextOf(string markup) =>
        Sta.Run(() =>
        {
            var document = new MarkdownPresenter { Blocks = Markdown.Parse(markup) }.Document;
            return new TextRange(document.ContentStart, document.ContentEnd).Text;
        });

    [Fact]
    public void AnUnsetBlocksPropertyRendersAnEmptyDocument()
    {
        // A default ImmutableArray throws when enumerated, and that is exactly what a binding hands
        // over before it has produced a value.
        Sta.Run(() => Assert.Empty(new MarkdownPresenter().Document.Blocks));
    }

    [Fact]
    public void AnEmptyMessageRendersAnEmptyDocument()
    {
        WithBlocks(string.Empty, Assert.Empty);
    }

    [Fact]
    public void AParagraphBecomesAParagraph()
    {
        WithBlocks("你好", blocks =>
        {
            var paragraph = Assert.IsType<Paragraph>(Assert.Single(blocks));
            Assert.Equal("你好", Assert.IsType<Run>(Assert.Single(paragraph.Inlines)).Text);
        });
    }

    [Fact]
    public void BoldItalicUnderlineAndStrikethroughMapToTheirInlines()
    {
        WithInlines("**粗** *斜* __下__ ~~删~~", inlines =>
        {
            Assert.Contains(inlines, i => i is Bold);
            Assert.Contains(inlines, i => i is Italic);
            Assert.Contains(inlines, i => i is Underline);
            Assert.Contains(inlines, i => i is Span { TextDecorations.Count: > 0 } and not Underline);
        });
    }

    [Fact]
    public void AHardBreakStaysInsideTheParagraph()
    {
        // The parser folds consecutive lines into one paragraph separated by LineBreak; splitting
        // those into separate paragraphs instead would double the spacing of a wrapped message.
        WithBlocks("一\n二", blocks =>
        {
            var paragraph = Assert.IsType<Paragraph>(Assert.Single(blocks));
            Assert.Contains(paragraph.Inlines, i => i is LineBreak);
        });
    }

    [Theory]
    [InlineData("# 标题")]
    [InlineData("###### 标题")]
    public void AHeadingIsABoldParagraph(string markup)
    {
        WithBlocks(markup, blocks =>
            Assert.Equal(FontWeights.SemiBold, Assert.IsType<Paragraph>(Assert.Single(blocks)).FontWeight));
    }

    [Fact]
    public void ALowerHeadingLevelIsSmaller()
    {
        Sta.Run(() =>
        {
            double SizeOf(string markup)
            {
                var document = new MarkdownPresenter { Blocks = Markdown.Parse(markup) }.Document;
                return ((Paragraph)document.Blocks.First()).FontSize;
            }

            Assert.True(SizeOf("# 标题") > SizeOf("#### 标题"));
        });
    }

    [Fact]
    public void AHeadingTakesItsSizeFromTheTheme()
    {
        Sta.Run(() =>
        {
            var presenter = new MarkdownPresenter();
            presenter.Resources.Add("Font.Size.Title", 42d);
            presenter.Blocks = Markdown.Parse("# 标题");

            Assert.Equal(42d, ((Paragraph)presenter.Document.Blocks.First()).FontSize);
        });
    }

    [Fact]
    public void AQuoteBecomesASectionWithALeftBorder()
    {
        WithBlocks("> 引用", blocks =>
        {
            var section = Assert.IsType<Section>(Assert.Single(blocks));
            Assert.Equal(new Thickness(3, 0, 0, 0), section.BorderThickness);
            Assert.NotEmpty(section.Blocks);
        });
    }

    [Fact]
    public void ABulletListUsesADiscMarker()
    {
        WithBlocks("- 一\n- 二", blocks =>
        {
            var list = Assert.IsType<WpfList>(Assert.Single(blocks));
            Assert.Equal(TextMarkerStyle.Disc, list.MarkerStyle);
            Assert.Equal(2, list.ListItems.Count);
        });
    }

    [Fact]
    public void AnOrderedListStartsAtItsFirstNumber()
    {
        WithBlocks("3. 三\n4. 四", blocks =>
        {
            var list = Assert.IsType<WpfList>(Assert.Single(blocks));
            Assert.Equal(TextMarkerStyle.Decimal, list.MarkerStyle);
            Assert.Equal(3, list.StartIndex);
        });
    }

    [Fact]
    public void AFencedBlockKeepsItsLineBreaks()
    {
        WithBlocks("```\n一\n二\n```", blocks =>
        {
            var paragraph = Assert.IsType<Paragraph>(Assert.Single(blocks));
            Assert.Contains(paragraph.Inlines, i => i is LineBreak);
        });
    }

    [Fact]
    public void ARuleBecomesAThinBorder()
    {
        WithBlocks("---", blocks =>
        {
            var container = Assert.IsType<BlockUIContainer>(Assert.Single(blocks));
            Assert.Equal(1, Assert.IsType<System.Windows.Controls.Border>(container.Child).Height);
        });
    }

    [Fact]
    public void ASafeLinkBecomesAHyperlink()
    {
        WithInlines("[点我](https://teamspeak.com/)", inlines =>
        {
            var link = Assert.IsType<Hyperlink>(Assert.Single(inlines));
            Assert.Equal("https://teamspeak.com/", link.NavigateUri?.AbsoluteUri);
        });
    }

    [Fact]
    public void AnAutoLinkShowsItsUrlAsTheLabel()
    {
        WithInlines("<https://teamspeak.com/>", inlines =>
        {
            var link = Assert.IsType<Hyperlink>(Assert.Single(inlines));
            Assert.Equal("https://teamspeak.com/", Assert.IsType<Run>(Assert.Single(link.Inlines)).Text);
        });
    }

    [Fact]
    public void ClickingALinkReportsItsTarget()
    {
        string? clicked = null;

        Sta.Run(() =>
        {
            var presenter = new MarkdownPresenter { Blocks = Markdown.Parse("[点我](https://teamspeak.com/)") };
            presenter.LinkClicked += (_, url) => clicked = url;

            var link = (Hyperlink)((Paragraph)presenter.Document.Blocks.First()).Inlines.First();
            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, link));
        });

        Assert.Equal("https://teamspeak.com/", clicked);
    }

    [Fact]
    public void ASpoilerIsPaintedOverUntilItIsClicked()
    {
        Sta.Run(() =>
        {
            var presenter = new MarkdownPresenter();
            presenter.Resources.Add("Brush.BorderStrong", Brushes.SlateGray);
            presenter.Blocks = Markdown.Parse("||秘密||");

            var span = Assert.IsType<Span>(((Paragraph)presenter.Document.Blocks.First()).Inlines.First());

            // Text painted in its own background colour keeps its width, so revealing it does not
            // reflow the message.
            Assert.Same(Brushes.SlateGray, span.Background);
            Assert.Same(Brushes.SlateGray, span.Foreground);
            Assert.NotNull(span.ToolTip);

            Assert.True(MarkdownPresenter.TryReveal(span));

            Assert.Null(span.Background);
            Assert.Null(span.ToolTip);
            Assert.NotSame(Brushes.SlateGray, span.Foreground);
        });
    }

    [Fact]
    public void ASpoilerIsRevealedByClickingAnythingInsideIt()
    {
        Sta.Run(() =>
        {
            var presenter = new MarkdownPresenter { Blocks = Markdown.Parse("||**秘密**||") };
            var span = (Span)((Paragraph)presenter.Document.Blocks.First()).Inlines.First();

            Assert.True(MarkdownPresenter.TryReveal(span.Inlines.First()));
        });
    }

    [Fact]
    public void ClickingSomethingThatIsNotASpoilerRevealsNothing()
    {
        Sta.Run(() =>
        {
            var presenter = new MarkdownPresenter { Blocks = Markdown.Parse("**粗体**") };
            var bold = (Bold)((Paragraph)presenter.Document.Blocks.First()).Inlines.First();

            Assert.False(MarkdownPresenter.TryReveal(bold));
        });
    }

    [Fact]
    public void AllTheTextSurvivesTheConversion()
    {
        // Nothing may be dropped silently: the reader has to be able to select and copy the whole
        // message, including the parts that carry formatting.
        string rendered = TextOf("**粗** *斜* `码`\n> 引用\n- 项\n[点我](https://teamspeak.com/)");

        foreach (string fragment in new[] { "粗", "斜", "码", "引用", "项", "点我" })
            Assert.Contains(fragment, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkupItselfIsNotShown()
    {
        Assert.DoesNotContain("*", TextOf("**粗体**"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsafeLinkTargetIsNotClickable()
    {
        // Markdown.Parse already refuses to build a Link node for these, so what matters here is
        // that the label survives as plain text instead of disappearing along with the link.
        const string markup = "[点我](javascript:alert(1))";

        WithInlines(markup, inlines => Assert.DoesNotContain(inlines, i => i is Hyperlink));
        Assert.Contains("点我", TextOf(markup), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentIsRebuiltWhenTheBlocksChange()
    {
        string second = Sta.Run(() =>
        {
            var presenter = new MarkdownPresenter { Blocks = Markdown.Parse("第一条") };
            presenter.Blocks = Markdown.Parse("第二条");

            return new TextRange(presenter.Document.ContentStart, presenter.Document.ContentEnd).Text;
        });

        Assert.Contains("第二条", second, StringComparison.Ordinal);
        Assert.DoesNotContain("第一条", second, StringComparison.Ordinal);
    }

    [Fact]
    public void TheControlIsReadOnly()
    {
        Sta.Run(() =>
        {
            var presenter = new MarkdownPresenter();

            Assert.True(presenter.IsReadOnly);
            Assert.False(presenter.IsUndoEnabled);
        });
    }

    [Fact]
    public void ANestedMessageStillRenders()
    {
        WithBlocks("> - **粗**\n> - *斜*", blocks =>
        {
            var section = Assert.IsType<Section>(Assert.Single(blocks));
            Assert.IsType<WpfList>(Assert.Single(section.Blocks));
        });
    }

    [Fact]
    public void AnEmptyQuoteStillProducesAParagraph()
    {
        // Dropping the empty paragraph would collapse the quote bar with it.
        WithBlocks(">", blocks => Assert.NotEmpty(Assert.IsType<Section>(Assert.Single(blocks)).Blocks));
    }

    [Fact]
    public void EveryNodeKindIsHandled()
    {
        // A MarkdownNodeKind the presenter forgets about would otherwise only show up as a missing
        // piece of somebody's message at run time.
        var missed = Sta.Run(() =>
        {
            var kinds = new List<MarkdownNodeKind>();

            foreach (var kind in Enum.GetValues<MarkdownNodeKind>())
            {
                bool leaf = kind is MarkdownNodeKind.Text or MarkdownNodeKind.Code or MarkdownNodeKind.LineBreak;

                var node = new MarkdownNode
                {
                    Kind = kind,
                    Text = "文",
                    Argument = kind switch
                    {
                        MarkdownNodeKind.Heading => "2",
                        MarkdownNodeKind.List => "1",
                        MarkdownNodeKind.Link => "https://teamspeak.com/",
                        _ => string.Empty,
                    },
                    Children = leaf ? ImmutableArray<MarkdownNode>.Empty : [MarkdownNode.OfText("文")],
                };

                var blocks = node.IsBlock
                    ? ImmutableArray.Create(node)
                    : ImmutableArray.Create(new MarkdownNode { Kind = MarkdownNodeKind.Paragraph, Children = [node] });

                var document = new MarkdownPresenter { Blocks = blocks }.Document;

                bool rendered = node.IsBlock
                    ? document.Blocks.Count > 0
                    : document.Blocks.OfType<Paragraph>().Any(p => p.Inlines.Count > 0);

                if (!rendered)
                    kinds.Add(kind);
            }

            return kinds;
        });

        Assert.Empty(missed);
    }
}
