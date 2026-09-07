// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace TeamSpeak9.Core.Model;

/// <summary>What a <see cref="MarkdownNode"/> stands for.</summary>
/// <remarks>
/// The inline kinds come first and the block kinds after, so <see cref="MarkdownNode.IsBlock"/> is a
/// single comparison. Keep <see cref="Paragraph"/> as the first block kind if the list changes.
/// </remarks>
public enum MarkdownNodeKind
{
    /// <summary>A literal run of text, held in <see cref="MarkdownNode.Text"/>.</summary>
    Text,

    /// <summary>A hard line break inside a paragraph. Carries no content.</summary>
    LineBreak,

    /// <summary><c>**bold**</c>.</summary>
    Bold,

    /// <summary><c>*italic*</c> or <c>_italic_</c>.</summary>
    Italic,

    /// <summary><c>__underline__</c>.</summary>
    Underline,

    /// <summary><c>~~strikethrough~~</c>.</summary>
    Strikethrough,

    /// <summary>
    /// An inline <c>`code`</c> span. Nothing inside is markup, so the content is in
    /// <see cref="MarkdownNode.Text"/> and there are no children.
    /// </summary>
    Code,

    /// <summary><c>||spoiler||</c>. Content the reader has to reveal.</summary>
    Spoiler,

    /// <summary>
    /// <c>[label](target)</c>, <c>&lt;https://…&gt;</c> or a bare URL. The target is in
    /// <see cref="MarkdownNode.Argument"/> and the label in the children.
    /// </summary>
    Link,

    /// <summary>A run of text. Children are inline nodes.</summary>
    Paragraph,

    /// <summary><c># heading</c>. The level (<c>1</c>–<c>6</c>) is in <see cref="MarkdownNode.Argument"/>.</summary>
    Heading,

    /// <summary><c>&gt; quote</c>. Children are block nodes.</summary>
    Quote,

    /// <summary>
    /// A bullet or numbered list. Children are <see cref="ListItem"/>; the start number of an
    /// ordered list is in <see cref="MarkdownNode.Argument"/>, which is empty for a bullet list.
    /// </summary>
    List,

    /// <summary>One list entry. Children are block nodes.</summary>
    ListItem,

    /// <summary>
    /// A fenced <c>```</c> block. Nothing inside is markup, so the children are only
    /// <see cref="Text"/> and <see cref="LineBreak"/>; the info string is in
    /// <see cref="MarkdownNode.Argument"/>.
    /// </summary>
    CodeBlock,

    /// <summary><c>---</c>. A horizontal rule, with no content.</summary>
    Rule,
}

/// <summary>
/// One node of a parsed Markdown message.
/// </summary>
/// <remarks>
/// Deliberately free of any UI type: the shell decides how each <see cref="MarkdownNodeKind"/> is
/// drawn, which keeps the parser testable without an STA thread.
/// </remarks>
public sealed record MarkdownNode
{
    public required MarkdownNodeKind Kind { get; init; }

    /// <summary>
    /// Literal content. Only <see cref="MarkdownNodeKind.Text"/> and
    /// <see cref="MarkdownNodeKind.Code"/> use it.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The node's parameter, if the kind defines one. Never <c>null</c>.</summary>
    public string Argument { get; init; } = string.Empty;

    public ImmutableArray<MarkdownNode> Children { get; init; } = [];

    /// <summary>The shared hard line break node.</summary>
    public static MarkdownNode LineBreak { get; } = new() { Kind = MarkdownNodeKind.LineBreak };

    /// <summary>The shared horizontal rule node.</summary>
    public static MarkdownNode Rule { get; } = new() { Kind = MarkdownNodeKind.Rule };

    /// <summary>
    /// True for the kinds that render as a block, so a renderer knows whether it needs a
    /// <c>Block</c> or an <c>Inline</c>. <see cref="Markdown.Parse"/> only ever returns blocks.
    /// </summary>
    public bool IsBlock => Kind >= MarkdownNodeKind.Paragraph;

    public static MarkdownNode OfText(string text) => new() { Kind = MarkdownNodeKind.Text, Text = text };

    /// <summary>Compares two subtrees by value.</summary>
    /// <remarks>
    /// <see cref="ImmutableArray{T}" /> compares by its underlying array reference, so the equality
    /// the compiler generates for a record would report two structurally identical trees as
    /// different. Walking the children is what callers expect of a value like this.
    /// </remarks>
    public bool Equals(MarkdownNode? other) =>
        other is not null
        && Kind == other.Kind
        && Text == other.Text
        && Argument == other.Argument
        && Children.SequenceEqual(other.Children);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Text);
        hash.Add(Argument);

        foreach (var child in Children)
            hash.Add(child);

        return hash.ToHashCode();
    }

    public override string ToString() => Kind switch
    {
        MarkdownNodeKind.Text or MarkdownNodeKind.Code => Text,
        MarkdownNodeKind.LineBreak => "\\n",
        _ when Argument.Length > 0 => $"{Kind}={Argument}[{Children.Length}]",
        _ => $"{Kind}[{Children.Length}]",
    };
}

/// <summary>
/// Parses the Markdown dialect TeamSpeak clients exchange into a small tree.
/// </summary>
/// <remarks>
/// <para>
/// The official TeamSpeak 6 client formats chat with Markdown rather than the BBCode of TeamSpeak 3,
/// so this follows the same chat-oriented dialect: the CommonMark blocks that a chat line can
/// reasonably carry (paragraph, ATX heading, blockquote, bullet and numbered list, fenced code,
/// horizontal rule) plus the two inline extensions every chat client of this kind grew,
/// <c>__underline__</c> and <c>||spoiler||</c>.
/// </para>
/// <para>
/// Two deliberate departures from CommonMark, because this renders chat and not documents: a single
/// newline is a hard break rather than a space, and a blank line ends a list instead of making it
/// loose. Indented code blocks are not supported either — in chat, leading spaces are accidental far
/// more often than deliberate.
/// </para>
/// <para>
/// Chat text comes straight from the server and from other clients, so it is untrusted input: every
/// malformed shape has to produce something renderable instead of throwing. An unterminated
/// delimiter stays literal text, an unterminated fence runs to the end of the message, nesting past
/// <see cref="MaxDepth"/> stops being read as markup, and only <c>http</c> and <c>https</c> targets
/// ever become links.
/// </para>
/// </remarks>
public static class Markdown
{
    /// <summary>Block and inline nesting above which markup is kept as literal text.</summary>
    private const int MaxDepth = 8;

    private const int MaxHeadingLevel = 6;

    private const int MinFenceLength = 3;

    private const int TabWidth = 4;

    /// <summary>Parses <paramref name="markup"/>. Returns an empty tree for null or empty input.</summary>
    /// <remarks>Every returned node is a block, so a renderer never has to check.</remarks>
    public static ImmutableArray<MarkdownNode> Parse(string? markup)
    {
        if (string.IsNullOrEmpty(markup))
            return [];

        var lines = SplitLines(markup);
        return ParseBlocks(lines, 0, lines.Count, 0);
    }

    /// <summary>
    /// Renders <paramref name="markup"/> as plain text, for places that cannot show formatting such
    /// as toast notifications and log lines.
    /// </summary>
    public static string ToPlainText(string? markup)
    {
        if (string.IsNullOrEmpty(markup))
            return string.Empty;

        var builder = new StringBuilder(markup.Length);
        AppendPlainText(Parse(markup), builder);
        return builder.ToString();
    }

    /// <summary>
    /// True when <paramref name="url"/> is an absolute http(s) URL and therefore safe to turn into a
    /// clickable link.
    /// </summary>
    /// <remarks>
    /// Anything else — <c>file:</c>, <c>javascript:</c>, a bare Windows path — stays plain text, so a
    /// hostile message cannot offer the reader a one-click way to launch something.
    /// </remarks>
    public static bool IsSafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // ===== Blocks =====

    private static ImmutableArray<MarkdownNode> ParseBlocks(List<string> lines, int from, int to, int depth)
    {
        var blocks = ImmutableArray.CreateBuilder<MarkdownNode>();
        int i = from;

        while (i < to)
        {
            string line = lines[i];

            if (IsBlank(line))
            {
                i++;
                continue;
            }

            string content = Unindent(line);

            if (TryReadFence(content, out int fenceLength, out char fenceChar, out string info))
            {
                i = ReadFencedBlock(lines, i, to, fenceLength, fenceChar, info, IndentWidth(line), blocks);
                continue;
            }

            if (TryReadHeading(content, out int level, out string heading))
            {
                blocks.Add(new MarkdownNode
                {
                    Kind = MarkdownNodeKind.Heading,
                    Argument = level.ToString(CultureInfo.InvariantCulture),
                    Children = ParseInlines(heading, depth),
                });

                i++;
                continue;
            }

            if (IsRule(content))
            {
                blocks.Add(MarkdownNode.Rule);
                i++;
                continue;
            }

            if (depth < MaxDepth && IsQuote(content))
            {
                i = ReadQuote(lines, i, to, depth, blocks);
                continue;
            }

            if (depth < MaxDepth && TryReadListMarker(line, out var marker))
            {
                i = ReadList(lines, i, to, marker, depth, blocks);
                continue;
            }

            i = ReadParagraph(lines, i, to, depth, blocks);
        }

        return blocks.ToImmutable();
    }

    private static int ReadParagraph(
        List<string> lines,
        int from,
        int to,
        int depth,
        ImmutableArray<MarkdownNode>.Builder blocks)
    {
        var text = new StringBuilder();
        int i = from;

        // The first line got here by not starting a block, so it is taken unconditionally; the ones
        // after it stop the paragraph as soon as they start something else.
        while (i < to && !IsBlank(lines[i]) && (i == from || !StartsBlock(Unindent(lines[i]))))
        {
            if (i > from)
                text.Append('\n');

            text.Append(Unindent(lines[i]).TrimEnd());
            i++;
        }

        blocks.Add(new MarkdownNode
        {
            Kind = MarkdownNodeKind.Paragraph,
            Children = ParseInlines(text.ToString(), depth),
        });

        return i;
    }

    private static int ReadQuote(
        List<string> lines,
        int from,
        int to,
        int depth,
        ImmutableArray<MarkdownNode>.Builder blocks)
    {
        var inner = new List<string>();
        int i = from;

        while (i < to && IsQuote(Unindent(lines[i])))
        {
            inner.Add(StripQuote(Unindent(lines[i])));
            i++;
        }

        blocks.Add(new MarkdownNode
        {
            Kind = MarkdownNodeKind.Quote,
            Children = ParseBlocks(inner, 0, inner.Count, depth + 1),
        });

        return i;
    }

    private static int ReadFencedBlock(
        List<string> lines,
        int from,
        int to,
        int fenceLength,
        char fenceChar,
        string info,
        int indent,
        ImmutableArray<MarkdownNode>.Builder blocks)
    {
        var body = ImmutableArray.CreateBuilder<MarkdownNode>();
        int i = from + 1;
        bool first = true;

        while (i < to && !IsClosingFence(Unindent(lines[i]), fenceChar, fenceLength))
        {
            if (!first)
                body.Add(MarkdownNode.LineBreak);

            string text = Dedent(lines[i], indent);
            if (text.Length > 0)
                body.Add(MarkdownNode.OfText(text));

            first = false;
            i++;
        }

        blocks.Add(new MarkdownNode
        {
            Kind = MarkdownNodeKind.CodeBlock,
            Argument = info,
            Children = body.ToImmutable(),
        });

        // An unterminated fence runs to the end of the message rather than being dropped.
        return i < to ? i + 1 : to;
    }

    private static int ReadList(
        List<string> lines,
        int from,
        int to,
        in ListMarker first,
        int depth,
        ImmutableArray<MarkdownNode>.Builder blocks)
    {
        var items = new List<List<string>>();
        int contentIndent = first.ContentIndent;
        int i = from;

        while (i < to && !IsBlank(lines[i]))
        {
            // A marker no further right than the list's own is either the next entry or, if it is a
            // different kind of list, the start of a new one.
            if (TryReadListMarker(lines[i], out var marker) && marker.Indent <= first.Indent)
            {
                if (marker.Ordered != first.Ordered)
                    break;

                items.Add([marker.Content]);
                contentIndent = marker.ContentIndent;
                i++;
                continue;
            }

            if (items.Count == 0)
                break;

            // A heading, quote, rule or fence at the outer level ends the list instead of being
            // swallowed as a continuation of the last entry.
            if (IndentWidth(lines[i]) < contentIndent && EndsList(Unindent(lines[i])))
                break;

            items[^1].Add(Dedent(lines[i], contentIndent));
            i++;
        }

        if (items.Count == 0)
            return from + 1;

        var children = ImmutableArray.CreateBuilder<MarkdownNode>(items.Count);
        foreach (var item in items)
        {
            children.Add(new MarkdownNode
            {
                Kind = MarkdownNodeKind.ListItem,
                Children = ParseBlocks(item, 0, item.Count, depth + 1),
            });
        }

        blocks.Add(new MarkdownNode
        {
            Kind = MarkdownNodeKind.List,
            Argument = first.Ordered ? first.Start.ToString(CultureInfo.InvariantCulture) : string.Empty,
            Children = children.ToImmutable(),
        });

        return i;
    }

    // ===== Block recognition =====

    private static bool StartsBlock(string content) =>
        TryReadFence(content, out _, out _, out _)
        || TryReadHeading(content, out _, out _)
        || IsRule(content)
        || IsQuote(content)
        || TryReadListMarker(content, out _);

    private static bool EndsList(string content) =>
        TryReadFence(content, out _, out _, out _)
        || TryReadHeading(content, out _, out _)
        || IsRule(content)
        || IsQuote(content);

    private static bool IsBlank(string line) => line.AsSpan().IsWhiteSpace();

    private static bool IsQuote(string content) => content.StartsWith('>');

    private static string StripQuote(string content)
    {
        string rest = content[1..];
        return rest.StartsWith(' ') ? rest[1..] : rest;
    }

    private static bool TryReadHeading(string content, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        while (level < content.Length && content[level] == '#')
            level++;

        if (level == 0 || level > MaxHeadingLevel)
            return false;

        if (level < content.Length && content[level] is not (' ' or '\t'))
            return false;

        var span = content.AsSpan(level).Trim();

        // A closing run of hashes is decoration and only counts when it is set off by a space, so
        // "# C#" keeps its hash.
        int end = span.Length;
        while (end > 0 && span[end - 1] == '#')
            end--;

        if (end < span.Length && (end == 0 || span[end - 1] is ' ' or '\t'))
            span = span[..end].TrimEnd();

        text = span.ToString();
        return true;
    }

    private static bool IsRule(string content)
    {
        char marker = '\0';
        int count = 0;

        foreach (char c in content)
        {
            if (c is ' ' or '\t')
                continue;

            if (c is not ('-' or '*' or '_'))
                return false;

            if (marker == '\0')
                marker = c;
            else if (c != marker)
                return false;

            count++;
        }

        return count >= MinFenceLength;
    }

    private static bool TryReadFence(string content, out int length, out char marker, out string info)
    {
        length = 0;
        marker = '\0';
        info = string.Empty;

        if (content.Length == 0 || content[0] is not ('`' or '~'))
            return false;

        marker = content[0];
        while (length < content.Length && content[length] == marker)
            length++;

        if (length < MinFenceLength)
            return false;

        string rest = content[length..].Trim();

        // A backtick fence cannot carry backticks in its info string; that shape is an inline span.
        if (marker == '`' && rest.Contains('`', StringComparison.Ordinal))
            return false;

        info = rest;
        return true;
    }

    private static bool IsClosingFence(string content, char marker, int length)
    {
        int run = 0;
        while (run < content.Length && content[run] == marker)
            run++;

        return run >= length && content.AsSpan(run).IsWhiteSpace();
    }

    private static bool TryReadListMarker(string line, out ListMarker marker)
    {
        marker = default;

        int indent = IndentWidth(line);
        int i = SkipIndent(line);
        if (i >= line.Length)
            return false;

        char c = line[i];
        int markerEnd;
        bool ordered;
        int start = 1;

        if (c is '-' or '*' or '+')
        {
            markerEnd = i + 1;
            ordered = false;
        }
        else if (char.IsAsciiDigit(c))
        {
            int digits = i;
            while (digits < line.Length && char.IsAsciiDigit(line[digits]) && digits - i < 9)
                digits++;

            if (digits >= line.Length || line[digits] is not ('.' or ')'))
                return false;

            start = int.Parse(line.AsSpan(i, digits - i), CultureInfo.InvariantCulture);
            markerEnd = digits + 1;
            ordered = true;
        }
        else
        {
            return false;
        }

        // Without the space this is emphasis, a negative number or a sentence, not a list.
        if (markerEnd < line.Length && line[markerEnd] is not (' ' or '\t'))
            return false;

        int contentStart = markerEnd;
        while (contentStart < line.Length && line[contentStart] is ' ' or '\t')
            contentStart++;

        marker = new ListMarker(
            indent,
            indent + (contentStart == markerEnd ? markerEnd - i + 1 : contentStart - i),
            ordered,
            start,
            line[contentStart..]);

        return true;
    }

    // ===== Indentation =====

    private static int IndentWidth(string line)
    {
        int width = 0;

        foreach (char c in line)
        {
            if (c == ' ')
                width++;
            else if (c == '\t')
                width += TabWidth - (width % TabWidth);
            else
                break;
        }

        return width;
    }

    private static int SkipIndent(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] is ' ' or '\t')
            i++;

        return i;
    }

    private static string Unindent(string line) => line[SkipIndent(line)..];

    /// <summary>Removes up to <paramref name="width"/> columns of leading whitespace.</summary>
    private static string Dedent(string line, int width)
    {
        int i = 0;
        int column = 0;

        while (i < line.Length && column < width)
        {
            if (line[i] == ' ')
                column++;
            else if (line[i] == '\t')
                column += TabWidth - (column % TabWidth);
            else
                break;

            i++;
        }

        return line[i..];
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
                continue;

            lines.Add(text[start..i]);

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;

            start = i + 1;
        }

        lines.Add(text[start..]);
        return lines;
    }

    // ===== Inlines =====

    /// <summary>
    /// Parses the inline markup of one block. <paramref name="text"/> may contain <c>\n</c>, which
    /// becomes a hard break.
    /// </summary>
    /// <remarks>
    /// The <c>exhausted</c> flags make this linear in the length of the input. Once the search for a
    /// delimiter's partner has failed it can never succeed for a later opener, because that opener
    /// searches a subrange; without the flags a message of nothing but <c>_a </c> repeated would cost
    /// a scan to the end per opener.
    /// </remarks>
    private static ImmutableArray<MarkdownNode> ParseInlines(string text, int depth)
    {
        var target = ImmutableArray.CreateBuilder<MarkdownNode>();
        var literal = new StringBuilder();

        bool noBold = false;
        bool noUnderline = false;
        bool noStrike = false;
        bool noSpoiler = false;
        bool noItalic = false;
        bool noUnderscore = false;
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                literal.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '\n')
            {
                Flush(literal, target);
                target.Add(MarkdownNode.LineBreak);
                i++;
                continue;
            }

            if (c == '`' && TryReadCodeSpan(text, ref i, target, literal))
                continue;

            if (depth < MaxDepth)
            {
                if (c == '*' && TryReadWrapped(text, ref i, "**", MarkdownNodeKind.Bold, ref noBold, target, literal, depth))
                    continue;

                if (c == '_' && TryReadWrapped(text, ref i, "__", MarkdownNodeKind.Underline, ref noUnderline, target, literal, depth))
                    continue;

                if (c == '~' && TryReadWrapped(text, ref i, "~~", MarkdownNodeKind.Strikethrough, ref noStrike, target, literal, depth))
                    continue;

                if (c == '|' && TryReadWrapped(text, ref i, "||", MarkdownNodeKind.Spoiler, ref noSpoiler, target, literal, depth))
                    continue;

                if (c == '*' && TryReadWrapped(text, ref i, "*", MarkdownNodeKind.Italic, ref noItalic, target, literal, depth))
                    continue;

                if (c == '_' && TryReadUnderscoreItalic(text, ref i, ref noUnderscore, target, literal, depth))
                    continue;

                if (c == '[' && TryReadLink(text, ref i, target, literal, depth))
                    continue;
            }

            if (c == '<' && TryReadAutoLink(text, ref i, target, literal))
                continue;

            if (c is 'h' or 'H' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && TryReadBareUrl(text, ref i, target, literal))
                continue;

            literal.Append(c);
            i++;
        }

        Flush(literal, target);
        return target.ToImmutable();
    }

    private static void Flush(StringBuilder literal, ImmutableArray<MarkdownNode>.Builder target)
    {
        if (literal.Length == 0)
            return;

        target.Add(MarkdownNode.OfText(literal.ToString()));
        literal.Clear();
    }

    /// <summary>
    /// Reads a paired delimiter such as <c>**bold**</c>. Returns false when the delimiter at
    /// <paramref name="i"/> is not an opener, leaving it to the caller to emit as literal text.
    /// </summary>
    private static bool TryReadWrapped(
        string text,
        ref int i,
        string delimiter,
        MarkdownNodeKind kind,
        ref bool exhausted,
        ImmutableArray<MarkdownNode>.Builder target,
        StringBuilder literal,
        int depth)
    {
        if (exhausted || !text.AsSpan(i).StartsWith(delimiter, StringComparison.Ordinal))
            return false;

        int start = i + delimiter.Length;
        int close = text.IndexOf(delimiter, start, StringComparison.Ordinal);

        if (close < 0)
        {
            exhausted = true;
            return false;
        }

        // "**bold *italic***" ends on a run of three stars: the first closes the italic, the last two
        // close the bold. Sliding the closer one char right is what makes the nesting work, but only
        // when the span does not itself end in the delimiter — otherwise a message of nothing but
        // stars would read its own tail as emphasis. The lookahead is O(1), so the scan stays linear.
        if (delimiter.Length == 2
            && text[close - 1] != delimiter[0]
            && close + 2 < text.Length
            && text[close + 2] == delimiter[0]
            && (close + 3 >= text.Length || text[close + 3] != delimiter[0]))
            close++;

        // "****" and "* *" are not emphasis: an empty or all-space span has nothing to emphasise.
        if (text.AsSpan(start, close - start).IsWhiteSpace())
            return false;

        Flush(literal, target);
        target.Add(new MarkdownNode
        {
            Kind = kind,
            Children = ParseInlines(text[start..close], depth + 1),
        });

        i = close + delimiter.Length;
        return true;
    }

    /// <summary>
    /// Reads <c>_italic_</c>, which unlike the other delimiters only counts at a word boundary so
    /// that <c>snake_case_names</c> survive.
    /// </summary>
    private static bool TryReadUnderscoreItalic(
        string text,
        ref int i,
        ref bool exhausted,
        ImmutableArray<MarkdownNode>.Builder target,
        StringBuilder literal,
        int depth)
    {
        if (exhausted)
            return false;

        if (i > 0 && char.IsLetterOrDigit(text[i - 1]))
            return false;

        int start = i + 1;
        if (start >= text.Length || char.IsWhiteSpace(text[start]) || text[start] == '_')
            return false;

        int close = -1;
        for (int j = start + 1; j < text.Length; j++)
        {
            if (text[j] != '_' || char.IsWhiteSpace(text[j - 1]))
                continue;

            if (j + 1 < text.Length && char.IsLetterOrDigit(text[j + 1]))
                continue;

            close = j;
            break;
        }

        if (close < 0)
        {
            exhausted = true;
            return false;
        }

        Flush(literal, target);
        target.Add(new MarkdownNode
        {
            Kind = MarkdownNodeKind.Italic,
            Children = ParseInlines(text[start..close], depth + 1),
        });

        i = close + 1;
        return true;
    }

    private static bool TryReadCodeSpan(
        string text,
        ref int i,
        ImmutableArray<MarkdownNode>.Builder target,
        StringBuilder literal)
    {
        int run = 0;
        while (i + run < text.Length && text[i + run] == '`')
            run++;

        int start = i + run;
        int close = -1;

        for (int j = start; j < text.Length; j++)
        {
            if (text[j] != '`')
                continue;

            int length = 0;
            while (j + length < text.Length && text[j + length] == '`')
                length++;

            if (length == run)
            {
                close = j;
                break;
            }

            j += length - 1;
        }

        if (close < 0)
            return false;

        // A line break inside a span is a space, and one padding space on each side is dropped so
        // that "` `` `" can hold backticks.
        string content = text[start..close].Replace('\n', ' ');
        if (content.Length > 2 && content[0] == ' ' && content[^1] == ' ' && !content.AsSpan().IsWhiteSpace())
            content = content[1..^1];

        Flush(literal, target);
        target.Add(new MarkdownNode { Kind = MarkdownNodeKind.Code, Text = content });

        i = close + run;
        return true;
    }

    private static bool TryReadLink(
        string text,
        ref int i,
        ImmutableArray<MarkdownNode>.Builder target,
        StringBuilder literal,
        int depth)
    {
        int label = text.IndexOf(']', i + 1);
        if (label < 0 || label + 1 >= text.Length || text[label + 1] != '(')
            return false;

        int close = text.IndexOf(')', label + 2);
        if (close < 0)
            return false;

        string url = text[(label + 2)..close].Trim();
        if (url.StartsWith('<') && url.EndsWith('>'))
            url = url[1..^1].Trim();

        if (!IsSafeUrl(url))
            return false;

        string caption = text[(i + 1)..label];

        Flush(literal, target);
        target.Add(new MarkdownNode
        {
            Kind = MarkdownNodeKind.Link,
            Argument = url,
            Children = caption.Length == 0
                ? [MarkdownNode.OfText(url)]
                : ParseInlines(caption, depth + 1),
        });

        i = close + 1;
        return true;
    }

    private static bool TryReadAutoLink(
        string text,
        ref int i,
        ImmutableArray<MarkdownNode>.Builder target,
        StringBuilder literal)
    {
        int close = text.IndexOf('>', i + 1);
        if (close < 0)
            return false;

        string url = text[(i + 1)..close];
        if (!IsSafeUrl(url))
            return false;

        Flush(literal, target);
        target.Add(LinkTo(url));

        i = close + 1;
        return true;
    }

    private static bool TryReadBareUrl(
        string text,
        ref int i,
        ImmutableArray<MarkdownNode>.Builder target,
        StringBuilder literal)
    {
        int length = MeasureUrl(text, i);
        if (length == 0)
            return false;

        Flush(literal, target);
        target.Add(LinkTo(text.Substring(i, length)));

        i += length;
        return true;
    }

    private static MarkdownNode LinkTo(string url) => new()
    {
        Kind = MarkdownNodeKind.Link,
        Argument = url,
        Children = [MarkdownNode.OfText(url)],
    };

    /// <summary>Length of the URL starting at <paramref name="start"/>, or 0 if there is none.</summary>
    private static int MeasureUrl(string text, int start)
    {
        var span = text.AsSpan(start);

        int scheme = span.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8
            : span.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7
            : 0;

        if (scheme == 0)
            return 0;

        int end = scheme;
        while (end < span.Length && !char.IsWhiteSpace(span[end]) && span[end] is not ('<' or '>' or '"' or '|'))
            end++;

        end = TrimUrlEnd(span[..end], scheme);

        return end > scheme && IsSafeUrl(new string(span[..end])) ? end : 0;
    }

    /// <summary>
    /// Drops trailing punctuation that belongs to the sentence rather than to the URL.
    /// </summary>
    /// <remarks>
    /// A closing bracket only counts as sentence punctuation when the URL does not open it itself,
    /// so wiki style links such as <c>…/Foo_(bar)</c> survive.
    /// </remarks>
    private static int TrimUrlEnd(ReadOnlySpan<char> url, int minimum)
    {
        int end = url.Length;

        while (end > minimum)
        {
            char last = url[end - 1];

            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"' or '*' or '~' or '_')
            {
                end--;
                continue;
            }

            if (last is ')' or ']' or '}')
            {
                char opening = last switch { ')' => '(', ']' => '[', _ => '{' };
                if (Count(url[..end], opening) < Count(url[..end], last))
                {
                    end--;
                    continue;
                }
            }

            break;
        }

        return end;
    }

    private static int Count(ReadOnlySpan<char> span, char value)
    {
        int total = 0;
        foreach (char c in span)
        {
            if (c == value)
                total++;
        }

        return total;
    }

    private static bool IsEscapable(char c) =>
        c is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')'
            or '#' or '+' or '-' or '.' or '!' or '>' or '|' or '~';

    // ===== Plain text =====

    private static void AppendPlainText(
        ImmutableArray<MarkdownNode> nodes,
        StringBuilder builder,
        bool suppressFirstBreak = false)
    {
        foreach (var node in nodes)
        {
            bool suppress = suppressFirstBreak;
            suppressFirstBreak = false;

            switch (node.Kind)
            {
                case MarkdownNodeKind.Text or MarkdownNodeKind.Code:
                    builder.Append(node.Text);
                    break;

                case MarkdownNodeKind.LineBreak:
                    builder.Append('\n');
                    break;

                case MarkdownNodeKind.Link when node.Children.IsEmpty:
                    builder.Append(node.Argument);
                    break;

                case MarkdownNodeKind.Rule:
                    StartLine(builder);
                    break;

                case MarkdownNodeKind.ListItem:
                    StartLine(builder);
                    builder.Append("• ");

                    // The bullet already opened the line, so the entry's own paragraph must not
                    // break again or every bullet would sit alone on its line.
                    AppendPlainText(node.Children, builder, suppressFirstBreak: true);
                    break;

                case MarkdownNodeKind.Paragraph
                    or MarkdownNodeKind.Heading
                    or MarkdownNodeKind.Quote
                    or MarkdownNodeKind.List
                    or MarkdownNodeKind.CodeBlock:
                    if (!suppress)
                        StartLine(builder);

                    AppendPlainText(node.Children, builder);
                    break;

                default:
                    AppendPlainText(node.Children, builder);
                    break;
            }
        }
    }

    private static void StartLine(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
            builder.Append('\n');
    }

    /// <summary>A list marker as written, with the columns needed to line up its continuations.</summary>
    private readonly record struct ListMarker(int Indent, int ContentIndent, bool Ordered, int Start, string Content);
}
