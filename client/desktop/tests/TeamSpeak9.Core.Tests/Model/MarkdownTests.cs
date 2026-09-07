// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Immutable;
using System.Text;
using TeamSpeak9.Core.Model;

namespace TeamSpeak9.Core.Tests.Model;

public class MarkdownTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingInNothingOut(string? markup)
    {
        Assert.Empty(Markdown.Parse(markup));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void WhitespaceOnlyInputHasNoBlocks(string markup)
    {
        Assert.Empty(Markdown.Parse(markup));
    }

    [Fact]
    public void PlainTextBecomesOneParagraph()
    {
        var block = Assert.Single(Markdown.Parse("你好，世界"));

        Assert.Equal(MarkdownNodeKind.Paragraph, block.Kind);
        var node = Assert.Single(block.Children);
        Assert.Equal(MarkdownNodeKind.Text, node.Kind);
        Assert.Equal("你好，世界", node.Text);
    }

    [Fact]
    public void EveryTopLevelNodeIsABlock()
    {
        var blocks = Markdown.Parse("段落\n\n# 标题\n\n> 引用\n\n- 条目\n\n---\n\n```\n代码\n```");

        Assert.All(blocks, b => Assert.True(b.IsBlock));
        Assert.Equal(6, blocks.Length);
    }

    [Fact]
    public void ABlankLineSeparatesParagraphs()
    {
        var blocks = Markdown.Parse("第一段\n\n第二段");

        Assert.Equal(2, blocks.Length);
        Assert.All(blocks, b => Assert.Equal(MarkdownNodeKind.Paragraph, b.Kind));
        Assert.Equal("第一段", Text(blocks[0]));
        Assert.Equal("第二段", Text(blocks[1]));
    }

    [Fact]
    public void ASingleNewlineIsAHardBreakNotASpace()
    {
        // Chat, not prose: a user who pressed Shift+Enter meant to break the line.
        var block = Assert.Single(Markdown.Parse("第一行\n第二行"));

        Assert.Equal(3, block.Children.Length);
        Assert.Equal("第一行", block.Children[0].Text);
        Assert.Equal(MarkdownNodeKind.LineBreak, block.Children[1].Kind);
        Assert.Equal("第二行", block.Children[2].Text);
    }

    [Theory]
    [InlineData("一\r\n二")]
    [InlineData("一\n二")]
    [InlineData("一\r二")]
    public void AllThreeLineEndingsWork(string markup)
    {
        var block = Assert.Single(Markdown.Parse(markup));
        Assert.Equal("一\n二", Text(block));
    }

    // ===== Inline emphasis =====

    [Theory]
    [InlineData("**粗**", MarkdownNodeKind.Bold)]
    [InlineData("*斜*", MarkdownNodeKind.Italic)]
    [InlineData("_斜_", MarkdownNodeKind.Italic)]
    [InlineData("__下划线__", MarkdownNodeKind.Underline)]
    [InlineData("~~删除~~", MarkdownNodeKind.Strikethrough)]
    [InlineData("||剧透||", MarkdownNodeKind.Spoiler)]
    public void EveryToolbarStyleIsRecognised(string markup, MarkdownNodeKind expected)
    {
        var node = Assert.Single(Assert.Single(Markdown.Parse(markup)).Children);

        Assert.Equal(expected, node.Kind);
        Assert.False(node.IsBlock);
    }

    [Fact]
    public void EmphasisNests()
    {
        var bold = Assert.Single(Assert.Single(Markdown.Parse("**粗*斜***")).Children);

        Assert.Equal(MarkdownNodeKind.Bold, bold.Kind);
        Assert.Equal(2, bold.Children.Length);
        Assert.Equal("粗", bold.Children[0].Text);
        Assert.Equal(MarkdownNodeKind.Italic, bold.Children[1].Kind);
        Assert.Equal("斜", Text(bold.Children[1]));
    }

    [Fact]
    public void TextAroundEmphasisIsKept()
    {
        var block = Assert.Single(Markdown.Parse("前**中**后"));

        Assert.Equal(3, block.Children.Length);
        Assert.Equal("前", block.Children[0].Text);
        Assert.Equal(MarkdownNodeKind.Bold, block.Children[1].Kind);
        Assert.Equal("后", block.Children[2].Text);
    }

    [Fact]
    public void TwoStarsBeatOne()
    {
        // "**a**" must not read as italic-empty-italic.
        var node = Assert.Single(Assert.Single(Markdown.Parse("**a**")).Children);
        Assert.Equal(MarkdownNodeKind.Bold, node.Kind);
    }

    [Theory]
    [InlineData("**没有闭合")]
    [InlineData("~~没有闭合")]
    [InlineData("||没有闭合")]
    [InlineData("*没有闭合")]
    [InlineData("_没有闭合")]
    [InlineData("__没有闭合")]
    public void AnUnclosedDelimiterStaysLiteral(string markup)
    {
        Assert.Equal(markup, Text(Assert.Single(Markdown.Parse(markup))));
    }

    [Theory]
    [InlineData("前**** 后")]
    [InlineData("前* *后")]
    [InlineData("前~~ ~~后")]
    [InlineData("前|| ||后")]
    [InlineData("前__ __后")]
    public void AnEmptySpanIsNotEmphasis(string markup)
    {
        var block = Assert.Single(Markdown.Parse(markup));

        Assert.All(block.Children, n => Assert.Equal(MarkdownNodeKind.Text, n.Kind));
        Assert.Equal(markup, Text(block));
    }

    [Theory]
    [InlineData("***")]
    [InlineData("****")]
    public void ARunOfStarsOnItsOwnLineIsARuleNotEmphasis(string markup)
    {
        // Blocks are recognised first, so a bare run of stars never reaches the inline parser.
        Assert.Equal(MarkdownNodeKind.Rule, Assert.Single(Markdown.Parse(markup)).Kind);
    }

    [Theory]
    [InlineData("snake_case_name", "snake_case_name")]
    [InlineData("a_b_c_d", "a_b_c_d")]
    [InlineData("MAX_VALUE 与 MIN_VALUE", "MAX_VALUE 与 MIN_VALUE")]
    public void UnderscoresInsideAWordAreNotEmphasis(string markup, string expected)
    {
        var block = Assert.Single(Markdown.Parse(markup));

        Assert.All(block.Children, n => Assert.Equal(MarkdownNodeKind.Text, n.Kind));
        Assert.Equal(expected, Text(block));
    }

    [Fact]
    public void AnUnderscorePairAtWordBoundariesIsEmphasis()
    {
        var block = Assert.Single(Markdown.Parse("这是 _斜体_ 文本"));

        Assert.Equal(3, block.Children.Length);
        Assert.Equal(MarkdownNodeKind.Italic, block.Children[1].Kind);
        Assert.Equal("斜体", Text(block.Children[1]));
    }

    [Theory]
    [InlineData('*')]
    [InlineData('_')]
    [InlineData('~')]
    [InlineData('|')]
    public void MultipliedDelimitersDoNotCostAScanEach(char delimiter)
    {
        // Guards the exhausted-delimiter shortcut: without it this input is quadratic. The leading
        // letter keeps the line out of the block rules so the inline parser really sees it.
        string markup = 'a' + new string(delimiter, 20000);

        var block = Assert.Single(Markdown.Parse(markup));
        Assert.Equal(markup, Text(block));
    }

    [Theory]
    [InlineData(@"\*不是斜体\*", "*不是斜体*")]
    [InlineData(@"\*\*粗\*\*", "**粗**")]
    [InlineData(@"a \\ b", @"a \ b")]
    [InlineData(@"\# 不是标题", "# 不是标题")]
    [InlineData(@"\|\|不是剧透\|\|", "||不是剧透||")]
    [InlineData(@"\q", @"\q")]
    public void ABackslashEscapesMarkup(string markup, string expected)
    {
        Assert.Equal(expected, Text(Assert.Single(Markdown.Parse(markup))));
    }

    [Fact]
    public void AnEscapeOnlyNeutralisesOneDelimiter()
    {
        // As in CommonMark the backslash consumes exactly one star, so the pair left over still
        // opens emphasis. Escape both stars to keep a whole "**" literal.
        var block = Assert.Single(Markdown.Parse(@"\**斜*"));

        Assert.Equal(2, block.Children.Length);
        Assert.Equal("*", block.Children[0].Text);
        Assert.Equal(MarkdownNodeKind.Italic, block.Children[1].Kind);
        Assert.Equal("斜", Text(block.Children[1]));
    }

    // ===== Code =====

    [Fact]
    public void AnInlineCodeSpanIsNotParsedInside()
    {
        var node = Assert.Single(Assert.Single(Markdown.Parse("`**不是粗体**`")).Children);

        Assert.Equal(MarkdownNodeKind.Code, node.Kind);
        Assert.Equal("**不是粗体**", node.Text);
        Assert.Empty(node.Children);
    }

    [Fact]
    public void ACodeSpanCanHoldBackticksWhenFencedByMore()
    {
        var node = Assert.Single(Assert.Single(Markdown.Parse("`` a ` b ``")).Children);

        Assert.Equal(MarkdownNodeKind.Code, node.Kind);
        Assert.Equal("a ` b", node.Text);
    }

    [Fact]
    public void AnUnclosedCodeSpanStaysLiteral()
    {
        Assert.Equal("`没有闭合", Text(Assert.Single(Markdown.Parse("`没有闭合"))));
    }

    [Fact]
    public void AFencedBlockIsNotParsedInside()
    {
        var block = Assert.Single(Markdown.Parse("```\n**不是粗体**\n# 不是标题\n```"));

        Assert.Equal(MarkdownNodeKind.CodeBlock, block.Kind);
        Assert.Equal("**不是粗体**\n# 不是标题", Text(block));
    }

    [Fact]
    public void AFencedBlockKeepsItsLanguage()
    {
        var block = Assert.Single(Markdown.Parse("```csharp\nvar x = 1;\n```"));

        Assert.Equal("csharp", block.Argument);
        Assert.Equal("var x = 1;", Text(block));
    }

    [Fact]
    public void ATildeFenceWorksToo()
    {
        var block = Assert.Single(Markdown.Parse("~~~\na\n~~~"));

        Assert.Equal(MarkdownNodeKind.CodeBlock, block.Kind);
        Assert.Equal("a", Text(block));
    }

    [Fact]
    public void ABacktickFenceInsideATildeFenceIsContent()
    {
        var block = Assert.Single(Markdown.Parse("~~~\n```\na\n```\n~~~"));

        Assert.Equal("```\na\n```", Text(block));
    }

    [Fact]
    public void AnUnclosedFenceRunsToTheEnd()
    {
        var block = Assert.Single(Markdown.Parse("```\n一\n二"));

        Assert.Equal(MarkdownNodeKind.CodeBlock, block.Kind);
        Assert.Equal("一\n二", Text(block));
    }

    [Fact]
    public void AnEmptyFencedBlockHasNoChildren()
    {
        var block = Assert.Single(Markdown.Parse("```\n```"));

        Assert.Equal(MarkdownNodeKind.CodeBlock, block.Kind);
        Assert.Empty(block.Children);
    }

    [Fact]
    public void AFencedBlockKeepsItsBlankLines()
    {
        var block = Assert.Single(Markdown.Parse("```\n一\n\n二\n```"));

        Assert.Equal("一\n\n二", Text(block));
    }

    [Fact]
    public void AFencedBlockKeepsRelativeIndentation()
    {
        var block = Assert.Single(Markdown.Parse("  ```\n  一\n    二\n  ```"));

        Assert.Equal("一\n  二", Text(block));
    }

    [Fact]
    public void AFenceEndsTheParagraphBeforeIt()
    {
        var blocks = Markdown.Parse("说明\n```\na\n```");

        Assert.Equal(2, blocks.Length);
        Assert.Equal(MarkdownNodeKind.Paragraph, blocks[0].Kind);
        Assert.Equal(MarkdownNodeKind.CodeBlock, blocks[1].Kind);
    }

    // ===== Headings =====

    [Theory]
    [InlineData("# 一级", 1)]
    [InlineData("## 二级", 2)]
    [InlineData("###### 六级", 6)]
    public void AtxHeadingsCarryTheirLevel(string markup, int level)
    {
        var block = Assert.Single(Markdown.Parse(markup));

        Assert.Equal(MarkdownNodeKind.Heading, block.Kind);
        Assert.Equal(level.ToString(System.Globalization.CultureInfo.InvariantCulture), block.Argument);
    }

    [Theory]
    [InlineData("####### 七级")]
    [InlineData("#没有空格")]
    public void SevenHashesOrNoSpaceIsNotAHeading(string markup)
    {
        Assert.Equal(MarkdownNodeKind.Paragraph, Assert.Single(Markdown.Parse(markup)).Kind);
    }

    [Fact]
    public void AHeadingParsesItsInlines()
    {
        var block = Assert.Single(Markdown.Parse("# **重要**公告"));

        Assert.Equal(MarkdownNodeKind.Bold, block.Children[0].Kind);
        Assert.Equal("公告", block.Children[1].Text);
    }

    [Fact]
    public void ClosingHashesAreDecoration()
    {
        Assert.Equal("标题", Text(Assert.Single(Markdown.Parse("## 标题 ##"))));
    }

    [Fact]
    public void AHashAttachedToAWordSurvives()
    {
        Assert.Equal("C#", Text(Assert.Single(Markdown.Parse("# C#"))));
    }

    [Fact]
    public void AnEmptyHeadingIsStillAHeading()
    {
        var block = Assert.Single(Markdown.Parse("#"));

        Assert.Equal(MarkdownNodeKind.Heading, block.Kind);
        Assert.Empty(block.Children);
    }

    [Fact]
    public void AHeadingEndsTheParagraphBeforeIt()
    {
        var blocks = Markdown.Parse("段落\n# 标题");

        Assert.Equal(2, blocks.Length);
        Assert.Equal(MarkdownNodeKind.Paragraph, blocks[0].Kind);
        Assert.Equal(MarkdownNodeKind.Heading, blocks[1].Kind);
    }

    // ===== Rules =====

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("- - -")]
    [InlineData("-----")]
    public void EveryRuleSpellingWorks(string markup)
    {
        Assert.Equal(MarkdownNodeKind.Rule, Assert.Single(Markdown.Parse(markup)).Kind);
    }

    [Theory]
    [InlineData("--")]
    [InlineData("-*-")]
    public void TwoDashesOrAMixIsNotARule(string markup)
    {
        Assert.NotEqual(MarkdownNodeKind.Rule, Assert.Single(Markdown.Parse(markup)).Kind);
    }

    // ===== Quotes =====

    [Fact]
    public void AQuoteHoldsBlocks()
    {
        var block = Assert.Single(Markdown.Parse("> 引用"));

        Assert.Equal(MarkdownNodeKind.Quote, block.Kind);
        var inner = Assert.Single(block.Children);
        Assert.Equal(MarkdownNodeKind.Paragraph, inner.Kind);
        Assert.Equal("引用", Text(inner));
    }

    [Fact]
    public void ConsecutiveQuoteLinesJoinOneQuote()
    {
        var block = Assert.Single(Markdown.Parse("> 一\n> 二"));

        Assert.Equal(MarkdownNodeKind.Quote, block.Kind);
        Assert.Equal("一\n二", Text(block));
    }

    [Fact]
    public void QuotesNest()
    {
        var outer = Assert.Single(Markdown.Parse("> 外\n> > 内"));

        Assert.Equal(MarkdownNodeKind.Quote, outer.Kind);
        Assert.Equal(2, outer.Children.Length);
        Assert.Equal(MarkdownNodeKind.Quote, outer.Children[1].Kind);
        Assert.Equal("内", Text(outer.Children[1]));
    }

    [Fact]
    public void AQuoteCanHoldAList()
    {
        var quote = Assert.Single(Markdown.Parse("> - 一\n> - 二"));

        var list = Assert.Single(quote.Children);
        Assert.Equal(MarkdownNodeKind.List, list.Kind);
        Assert.Equal(2, list.Children.Length);
    }

    [Fact]
    public void AQuoteWithoutASpaceStillWorks()
    {
        Assert.Equal("引用", Text(Assert.Single(Markdown.Parse(">引用"))));
    }

    // ===== Lists =====

    [Theory]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("+")]
    public void EveryBulletMarkerWorks(string bullet)
    {
        var list = Assert.Single(Markdown.Parse($"{bullet} 一\n{bullet} 二"));

        Assert.Equal(MarkdownNodeKind.List, list.Kind);
        Assert.Equal(string.Empty, list.Argument);
        Assert.Equal(2, list.Children.Length);
        Assert.All(list.Children, c => Assert.Equal(MarkdownNodeKind.ListItem, c.Kind));
        Assert.Equal("一", Text(list.Children[0]));
        Assert.Equal("二", Text(list.Children[1]));
    }

    [Fact]
    public void AnOrderedListKeepsItsStartNumber()
    {
        var list = Assert.Single(Markdown.Parse("3. 一\n4. 二"));

        Assert.Equal(MarkdownNodeKind.List, list.Kind);
        Assert.Equal("3", list.Argument);
        Assert.Equal(2, list.Children.Length);
    }

    [Fact]
    public void AParenthesisAlsoMarksAnOrderedList()
    {
        Assert.Equal("1", Assert.Single(Markdown.Parse("1) 一")).Argument);
    }

    [Fact]
    public void SwitchingMarkerKindStartsANewList()
    {
        var blocks = Markdown.Parse("- 一\n1. 二");

        Assert.Equal(2, blocks.Length);
        Assert.Equal(string.Empty, blocks[0].Argument);
        Assert.Equal("1", blocks[1].Argument);
    }

    [Fact]
    public void ListEntriesHoldBlocks()
    {
        var list = Assert.Single(Markdown.Parse("- 条目"));

        var item = Assert.Single(list.Children);
        Assert.Equal(MarkdownNodeKind.Paragraph, Assert.Single(item.Children).Kind);
    }

    [Fact]
    public void AnIndentedLineContinuesTheEntryAboveIt()
    {
        var list = Assert.Single(Markdown.Parse("- 第一行\n  第二行\n- 下一条"));

        Assert.Equal(2, list.Children.Length);
        Assert.Equal("第一行\n第二行", Text(list.Children[0]));
    }

    [Fact]
    public void ListsNest()
    {
        var list = Assert.Single(Markdown.Parse("- 外\n  - 内"));

        var item = Assert.Single(list.Children);
        Assert.Equal(2, item.Children.Length);
        Assert.Equal(MarkdownNodeKind.List, item.Children[1].Kind);
        Assert.Equal("内", Text(item.Children[1]));
    }

    [Fact]
    public void AListEntryCanHoldAFencedBlock()
    {
        var list = Assert.Single(Markdown.Parse("- 说明\n  ```\n  code\n  ```"));

        var item = Assert.Single(list.Children);
        Assert.Equal(MarkdownNodeKind.CodeBlock, item.Children[1].Kind);
        Assert.Equal("code", Text(item.Children[1]));
    }

    [Fact]
    public void ABlankLineEndsTheList()
    {
        // Chat, not prose: two lists separated by a blank line are two lists, not one loose list.
        var blocks = Markdown.Parse("- 一\n\n- 二");

        Assert.Equal(2, blocks.Length);
        Assert.All(blocks, b => Assert.Equal(MarkdownNodeKind.List, b.Kind));
    }

    [Fact]
    public void AHeadingEndsTheList()
    {
        var blocks = Markdown.Parse("- 一\n# 标题");

        Assert.Equal(2, blocks.Length);
        Assert.Equal(MarkdownNodeKind.List, blocks[0].Kind);
        Assert.Equal(MarkdownNodeKind.Heading, blocks[1].Kind);
    }

    [Fact]
    public void AnUnindentedLineAfterAnEntryIsALazyContinuation()
    {
        var list = Assert.Single(Markdown.Parse("- 第一行\n第二行"));

        Assert.Equal("第一行\n第二行", Text(Assert.Single(list.Children)));
    }

    [Theory]
    [InlineData("-没有空格")]
    [InlineData("1.没有空格")]
    [InlineData("1234567890. 太多位")]
    public void AMarkerWithoutASpaceIsNotAList(string markup)
    {
        Assert.Equal(MarkdownNodeKind.Paragraph, Assert.Single(Markdown.Parse(markup)).Kind);
    }

    [Fact]
    public void AListEntryParsesItsInlines()
    {
        var list = Assert.Single(Markdown.Parse("- **粗**条目"));

        var paragraph = Assert.Single(Assert.Single(list.Children).Children);
        Assert.Equal(MarkdownNodeKind.Bold, paragraph.Children[0].Kind);
    }

    [Fact]
    public void AnEmptyEntryIsStillAnEntry()
    {
        var list = Assert.Single(Markdown.Parse("- \n- 二"));

        Assert.Equal(2, list.Children.Length);
        Assert.Empty(list.Children[0].Children);
    }

    // ===== Links =====

    [Fact]
    public void AnInlineLinkUsesItsTarget()
    {
        var node = Assert.Single(Assert.Single(Markdown.Parse("[点这里](https://example.com)")).Children);

        Assert.Equal(MarkdownNodeKind.Link, node.Kind);
        Assert.Equal("https://example.com", node.Argument);
        Assert.Equal("点这里", Text(node));
    }

    [Fact]
    public void ALinkLabelParsesItsInlines()
    {
        var node = Assert.Single(Assert.Single(Markdown.Parse("[**粗**标签](https://example.com)")).Children);

        Assert.Equal(MarkdownNodeKind.Bold, node.Children[0].Kind);
    }

    [Fact]
    public void AnEmptyLabelFallsBackToTheTarget()
    {
        var node = Assert.Single(Assert.Single(Markdown.Parse("[](https://example.com)")).Children);

        Assert.Equal("https://example.com", node.Argument);
        Assert.Equal("https://example.com", Text(node));
    }

    [Theory]
    [InlineData("[x](  https://example.com  )")]
    [InlineData("[x](<https://example.com>)")]
    public void APaddedOrBracketedTargetIsUnwrapped(string markup)
    {
        var node = Assert.Single(Assert.Single(Markdown.Parse(markup)).Children);
        Assert.Equal("https://example.com", node.Argument);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](file:///C:/x)")]
    [InlineData("[x](/relative)")]
    [InlineData("[x](#anchor)")]
    public void AnUnsafeTargetIsNotALink(string markup)
    {
        var block = Assert.Single(Markdown.Parse(markup));

        Assert.DoesNotContain(block.Children, n => n.Kind == MarkdownNodeKind.Link);
        Assert.Equal(markup, Text(block));
    }

    [Fact]
    public void AnUnclosedLinkStaysLiteral()
    {
        Assert.Equal("[点这里](https://a", Text(Assert.Single(Markdown.Parse("[点这里](https://a"))));
    }

    [Fact]
    public void AnAngleBracketAutoLinkWorks()
    {
        var node = Assert.Single(Assert.Single(Markdown.Parse("<https://example.com>")).Children);

        Assert.Equal(MarkdownNodeKind.Link, node.Kind);
        Assert.Equal("https://example.com", node.Argument);
    }

    [Fact]
    public void ABareUrlBecomesALink()
    {
        var block = Assert.Single(Markdown.Parse("看看 https://example.com/a?b=1 这个"));

        Assert.Equal(3, block.Children.Length);
        Assert.Equal("看看 ", block.Children[0].Text);
        Assert.Equal(MarkdownNodeKind.Link, block.Children[1].Kind);
        Assert.Equal("https://example.com/a?b=1", block.Children[1].Argument);
        Assert.Equal(" 这个", block.Children[2].Text);
    }

    [Theory]
    [InlineData("https://example.com/a.", "https://example.com/a")]
    [InlineData("https://example.com/a!", "https://example.com/a")]
    [InlineData("https://example.com/a，", "https://example.com/a，")]
    [InlineData("(https://example.com/a)", "https://example.com/a")]
    [InlineData("https://en.wikipedia.org/wiki/Foo_(bar)", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    public void SentencePunctuationIsNotPartOfALink(string markup, string expected)
    {
        var link = Assert.Single(Descendants(Markdown.Parse(markup)), n => n.Kind == MarkdownNodeKind.Link);
        Assert.Equal(expected, link.Argument);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void BothSchemesAreAutoLinked(string markup)
    {
        Assert.Contains(Descendants(Markdown.Parse(markup)), n => n.Kind == MarkdownNodeKind.Link);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///C:/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("example.com")]
    [InlineData("shttps://example.com")]
    public void OnlyHttpSchemesAreAutoLinked(string markup)
    {
        Assert.DoesNotContain(Descendants(Markdown.Parse(markup)), n => n.Kind == MarkdownNodeKind.Link);
    }

    [Fact]
    public void ABareUrlInsideACodeSpanIsNotALink()
    {
        var node = Assert.Single(Assert.Single(Markdown.Parse("`https://example.com`")).Children);
        Assert.Equal(MarkdownNodeKind.Code, node.Kind);
    }

    [Fact]
    public void ABareUrlInSpoilerMarkupDoesNotEatTheClosingBars()
    {
        var node = Assert.Single(Assert.Single(Markdown.Parse("||https://example.com||")).Children);

        Assert.Equal(MarkdownNodeKind.Spoiler, node.Kind);
        Assert.Equal("https://example.com", Assert.Single(node.Children).Argument);
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/x", false)]
    [InlineData("C:\\windows\\x.exe", false)]
    [InlineData("/relative", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyHttpUrlsAreConsideredSafe(string? url, bool expected)
    {
        Assert.Equal(expected, Markdown.IsSafeUrl(url));
    }

    // ===== Robustness =====

    [Fact]
    public void NestingDeeperThanTheLimitStopsBeingMarkup()
    {
        // A crafted message must not be able to grow the parser's stack without bound.
        string markup = string.Concat(Enumerable.Repeat("> ", 400)) + "底";
        var blocks = Markdown.Parse(markup);

        Assert.InRange(Depth(blocks), 1, 32);
        Assert.EndsWith("底", Text(Assert.Single(blocks)), StringComparison.Ordinal);
    }

    [Fact]
    public void DeeplyNestedEmphasisDoesNotOverflow()
    {
        string markup = string.Concat(Enumerable.Repeat("**", 400)) + "底" + string.Concat(Enumerable.Repeat("**", 400));

        Assert.InRange(Depth(Markdown.Parse(markup)), 1, 32);
    }

    [Fact]
    public void DeeplyNestedListsDoNotOverflow()
    {
        var lines = Enumerable.Range(0, 200).Select(i => new string(' ', i * 2) + "- 条目");

        Assert.InRange(Depth(Markdown.Parse(string.Join('\n', lines))), 1, 32);
    }

    [Fact]
    public void NodesCompareByValueSoTheSameInputParsesEqual()
    {
        const string Markup = "# 标题\n\n- 一\n- 二";

        // AsEnumerable picks xUnit's sequence overload: ImmutableArray compares by the identity of
        // its backing array, which would pass for any two parses regardless of content.
        Assert.Equal(Markdown.Parse(Markup).AsEnumerable(), Markdown.Parse(Markup).AsEnumerable());
        Assert.NotEqual(Markdown.Parse(Markup).AsEnumerable(), Markdown.Parse("# 标题\n\n- 一").AsEnumerable());
    }

    [Fact]
    public void ARealisticMessageParsesAsAWhole()
    {
        var blocks = Markdown.Parse(
            "# 服务器公告\n"
            + "\n"
            + "请见 [文档](https://example.com/doc)，注意：\n"
            + "\n"
            + "- **必须**更新客户端\n"
            + "- 端口改为 `9987`\n"
            + "\n"
            + "> 有问题联系管理员\n");

        Assert.Collection(
            blocks,
            b => Assert.Equal(MarkdownNodeKind.Heading, b.Kind),
            b => Assert.Equal(MarkdownNodeKind.Paragraph, b.Kind),
            b => Assert.Equal(MarkdownNodeKind.List, b.Kind),
            b => Assert.Equal(MarkdownNodeKind.Quote, b.Kind));
    }

    // ===== Plain text =====

    [Fact]
    public void ToPlainTextFlattensEverything()
    {
        string plain = Markdown.ToPlainText("**粗** [链接](https://a.example)\n\n- 一\n- 二");

        Assert.Equal("粗 链接\n• 一\n• 二", plain);
    }

    [Fact]
    public void ToPlainTextKeepsCodeVerbatim()
    {
        Assert.Equal("**x**", Markdown.ToPlainText("`**x**`"));
    }

    [Fact]
    public void ToPlainTextSeparatesBlocks()
    {
        Assert.Equal("标题\n段落", Markdown.ToPlainText("# 标题\n\n段落"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ToPlainTextOfNothingIsEmpty(string? markup)
    {
        Assert.Equal(string.Empty, Markdown.ToPlainText(markup));
    }

    private static string Text(MarkdownNode node) => Text(node.Children);

    private static string Text(IEnumerable<MarkdownNode> nodes)
    {
        var builder = new StringBuilder();

        foreach (var node in nodes)
        {
            switch (node.Kind)
            {
                case MarkdownNodeKind.LineBreak:
                    builder.Append('\n');
                    break;

                case MarkdownNodeKind.Text or MarkdownNodeKind.Code:
                    builder.Append(node.Text);
                    break;

                default:
                    if (node.IsBlock && builder.Length > 0 && builder[^1] != '\n')
                        builder.Append('\n');

                    builder.Append(Text(node.Children));
                    break;
            }
        }

        return builder.ToString();
    }

    private static int Depth(ImmutableArray<MarkdownNode> nodes) =>
        nodes.IsEmpty ? 0 : 1 + nodes.Max(n => Depth(n.Children));

    private static IEnumerable<MarkdownNode> Descendants(ImmutableArray<MarkdownNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            foreach (var child in Descendants(node.Children))
                yield return child;
        }
    }
}
