// WebPageReading.cs: lets Jarvis read aloud or summarize the web page open in Chrome/Edge. The browser reports the
// page as an accessibility tree (the same structure screen readers use) and this file turns that tree into clean text.
// Three parts: PageContentExtractor (strips menus, ads and footers, like a reader mode), PageSummarizer (asks the
// local model for a short spoken summary, in chunks for long pages) and PageTool (the "page read" / "page summarize"
// chat command). The code that actually reads the browser lives in the app project behind IBrowserPageReader.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jarvis.Core;

/// <summary>
/// One element of a browser page as the browser exposes it to screen readers. <see cref="Type"/> is the
/// localized control type ("text", "link", "heading", "navigation", "article", ...), which is how Chromium
/// marks page regions; <see cref="ClassName"/> is the element's HTML class attribute.
/// </summary>
public sealed record PageNode(string Type, string Name, string ClassName, int HeadingLevel, IReadOnlyList<PageNode> Children);

// A whole page as read from the browser: title, address and the root of its element tree.
public sealed record BrowserPage(string Title, string Url, PageNode Document);

// Implemented in the app project; hands back the page currently open in the front browser window.
public interface IBrowserPageReader
{
    /// <summary>The page in the frontmost Chromium browser window (Chrome, Edge, Brave, ...), or null if none is open.</summary>
    Task<BrowserPage?> ReadActivePageAsync(CancellationToken cancellationToken);
}

// One piece of cleaned-up text. Kind is "paragraph", "heading", "item" (list item) or "row" (table row).
public sealed record PageBlock(string Kind, string Text, int HeadingLevel = 0);

/// <param name="Article">The main content only (an article's body), or empty when the page has no clear article.</param>
/// <param name="Page">Everything meaningful on the page, links and lists included, for pages that are not articles.</param>
// The extraction result, kept in two forms because it is not known up front which one the user wants.
public sealed record ExtractedPage(string Title, string Url, IReadOnlyList<PageBlock> Article, IReadOnlyList<PageBlock> Page)
{
    // Total number of words across the blocks.
    public static int CountWords(IEnumerable<PageBlock> blocks) =>
        blocks.Sum(block => block.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
}

/// <summary>
/// Separates what a page is about from what surrounds it, like a browser's reader mode, using the regions
/// and structure the browser reports instead of the raw HTML.
/// </summary>
// Walks the page tree and keeps readable text. It works in two modes: "article mode" (only the main story, strict
// filtering) and "page mode" (everything meaningful, lighter filtering). "Partial" because the regexes are generated at compile time.
public static partial class PageContentExtractor
{
    // Controls and decoration: never content in either mode. A "document" below the page itself is an
    // embedded frame: ads, fundraising banners, social widgets.
    private static readonly HashSet<string> AlwaysSkippedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "document", "navigation", "search", "dialog", "alert", "banner", "header", "content information", "footer",
        "button", "toggle button", "split button", "menu button", "radio button", "check box", "combo box", "edit",
        "spinner", "slider", "menu", "menu bar", "menu item", "tool bar", "tooltip", "scroll bar", "progress indicator",
        "separator", "graphic", "unlabeled graphic", "image", "tab list", "form"
    };

    // Also skipped when reading an article: asides, figures, tables, and page chrome inside the article.
    private static readonly HashSet<string> ArticleSkippedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "complementary", "figure", "table", "note", "section header"
    };

    // Class-name words that mark clutter. Matched as whole words ("ad", not the "ad" in "header").
    private static readonly HashSet<string> ClutterClassWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ad", "ads", "advert", "advertisement", "sponsor", "sponsored", "promo", "newsletter", "subscribe", "cookie",
        "consent", "share", "sharing", "social", "related", "recommended", "recommendations", "comment", "comments",
        "breadcrumb", "breadcrumbs", "navbox", "reflist", "references", "toc", "skip", "paywall"
    };

    // Extra class-name words that only count as clutter inside an article (photo credits, sidebars, media players).
    private static readonly HashSet<string> ArticleClutterClassWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "caption", "credit", "hatnote", "infobox", "sidebar", "byline", "audio", "video", "gallery", "player"
    };

    // Sections after the story that are not part of it; reading stops at them.
    private static readonly HashSet<string> EndSectionHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "references", "notes", "citations", "sources", "footnotes", "external links", "see also", "further reading",
        "bibliography", "related", "related articles", "related stories", "more stories", "comments", "read more",
        "you may also like", "recommended", "more from npr", "popular", "most popular", "trending", "latest news",
        "reviews", "ratings and reviews", "ratings reviews", "user reviews", "customer reviews", "top reviews"
    };

    // Element types that sit inside a sentence (links, bold text...). They are joined into one paragraph instead of starting a new one.
    private static readonly HashSet<string> InlineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "link", "time", "emphasis", "strong", "code", "mark", "abbreviation", "superscript", "subscript"
    };

    // Produces both versions: the article body (empty if no clear article is found) and the full cleaned page.
    public static ExtractedPage Extract(BrowserPage page)
    {
        var root = FindArticleRoot(page.Document);
        var article = root is null ? [] : CleanArticle(BuildBlocks(root, articleMode: true));
        var everything = RemoveDuplicates(BuildBlocks(page.Document, articleMode: false));
        return new ExtractedPage(page.Title, page.Url, article, everything);
    }

    /// <summary>Title, then the blocks, with headings and list items ending in a stop so the voice pauses.</summary>
    // One block per line; headings and list items get a full stop added so the text-to-speech voice pauses after them.
    public static string ToReadableText(IEnumerable<PageBlock> blocks) =>
        string.Join("\n", blocks.Select(block => block.Kind == "paragraph" || EndsSentence(block.Text) ? block.Text : block.Text + "."));

    // The smallest region holding most of the page's prose: the article body, not the page around it.
    // Starts at the top and keeps stepping into the child that holds at least 80% of all the prose, stopping when no
    // child does. The node it ends on is taken as the article. Pages with under 200 characters of prose have no article.
    private static PageNode? FindArticleRoot(PageNode document)
    {
        var total = document.Children.Sum(Prose);

        if (total < 200)
        {
            return null;
        }

        var node = document;

        while (true)
        {
            // ponytail: one threshold for every site; an article split across sibling regions keeps their parent.
            var next = node.Children.FirstOrDefault(child => Prose(child) >= total * 0.8);

            if (next is null)
            {
                return node;
            }

            node = next;
        }
    }

    // Characters of plain text (not link text) outside skipped regions: sentences, not menus.
    private static int Prose(PageNode node)
    {
        if (IsSkipped(node, articleMode: true) || node.Type.Equals("link", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return node.Children.Count == 0
            ? (node.Type.Equals("text", StringComparison.OrdinalIgnoreCase) ? node.Name.Trim().Length : 0)
            : node.Children.Sum(Prose);
    }

    // Turns the element tree into a flat list of text blocks. Text pieces are collected into the current paragraph;
    // headings, list items and table rows become their own block.
    private static List<PageBlock> BuildBlocks(PageNode root, bool articleMode)
    {
        var blocks = new List<PageBlock>();
        var paragraph = new StringBuilder();
        var linkCharacters = 0;

        // Closes the current paragraph and adds it as a block (unless it is empty or, in article mode, mostly links).
        void Flush()
        {
            var text = Normalize(paragraph.ToString());

            // Mostly-link text in article mode is a list of links (a menu, "more stories"), not prose.
            if (text.Length > 0 && !(articleMode && linkCharacters > text.Length * 0.6))
            {
                blocks.Add(new PageBlock("paragraph", text));
            }

            paragraph.Clear();
            linkCharacters = 0;
        }

        // Visits one element: skips junk, makes a block for headings/items/rows, appends leaf text to the paragraph, and
        // recurses into children. Non-inline containers end the current paragraph before and after.
        void Walk(PageNode node, bool insideLink)
        {
            if (IsSkipped(node, articleMode))
            {
                return;
            }

            var type = node.Type.ToLowerInvariant();

            if (type is "heading" or "list item" or "row")
            {
                Flush();
                var (text, links) = type == "row"
                    ? CollectCells(node, articleMode)
                    : CollectText(node, articleMode);
                text = Normalize(text);

                if (text.Length > 0 && !(articleMode && type != "heading" && links > text.Length * 0.8))
                {
                    blocks.Add(new PageBlock(type == "heading" ? "heading" : type == "row" ? "row" : "item", text, node.HeadingLevel));
                }

                return;
            }

            if (node.Children.Count == 0)
            {
                var text = LeafText(node);

                if (text.Length > 0)
                {
                    AppendPiece(paragraph, text);
                    linkCharacters += insideLink || type == "link" ? text.Length : 0;
                }

                return;
            }

            var isInline = InlineTypes.Contains(type);

            if (!isInline)
            {
                Flush();
            }

            foreach (var child in node.Children)
            {
                Walk(child, insideLink || type == "link");
            }

            if (!isInline)
            {
                Flush();
            }
        }

        // The page's own document is walked through its children, since a nested document is skipped.
        foreach (var node in root.Type.Equals("document", StringComparison.OrdinalIgnoreCase) ? root.Children : [root])
        {
            Walk(node, insideLink: false);
        }

        Flush();
        return blocks;
    }

    // Gathers all text under one element into a single string, also counting how many characters were link text.
    private static (string Text, int LinkCharacters) CollectText(PageNode node, bool articleMode)
    {
        var builder = new StringBuilder();
        var links = 0;

        void Walk(PageNode current, bool insideLink)
        {
            if (IsSkipped(current, articleMode))
            {
                return;
            }

            var isLink = insideLink || current.Type.Equals("link", StringComparison.OrdinalIgnoreCase);

            if (current.Children.Count == 0)
            {
                var text = LeafText(current);

                if (text.Length > 0)
                {
                    AppendPiece(builder, text);
                    links += isLink ? text.Length : 0;
                }

                return;
            }

            foreach (var child in current.Children)
            {
                Walk(child, isLink);
            }
        }

        Walk(node, insideLink: false);
        return (builder.ToString(), links);
    }

    // A table row as "Product type | Stationery, paper": cells separated, not run together.
    private static (string Text, int LinkCharacters) CollectCells(PageNode row, bool articleMode)
    {
        var cells = row.Children.Select(cell => CollectText(cell, articleMode)).ToList();
        return (
            string.Join(" | ", cells.Select(cell => Normalize(cell.Text)).Where(text => text.Length > 0)),
            cells.Sum(cell => cell.LinkCharacters));
    }

    private static string LeafText(PageNode node)
    {
        var text = node.Name;

        // Citation markers and edit links ("[12]", "[a]", "edit") mean nothing read aloud.
        if (node.Type.Equals("link", StringComparison.OrdinalIgnoreCase) && CitationRegex().IsMatch(text.Trim()))
        {
            return string.Empty;
        }

        return text;
    }

    // Decides whether an element (and everything under it) is page furniture rather than content: by control type, by
    // telltale names ("Skip to...", "advertisement"), or by words in its HTML class name such as "ad" or "newsletter".
    private static bool IsSkipped(PageNode node, bool articleMode)
    {
        if (AlwaysSkippedTypes.Contains(node.Type) || (articleMode && ArticleSkippedTypes.Contains(node.Type)))
        {
            return true;
        }

        if (node.Name.StartsWith("Skip to", StringComparison.OrdinalIgnoreCase)
            || node.Name.StartsWith("Jump to", StringComparison.OrdinalIgnoreCase)
            || node.Name.Contains("advertisement", StringComparison.OrdinalIgnoreCase)
            || node.Name.Equals("Sponsor Message", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The site's own "this is the content" regions are trusted over class names: Allrecipes wraps its
        // whole recipe in class "sc-ad-container".
        if (node.ClassName.Length == 0 || node.Type is "main" or "article")
        {
            return false;
        }

        // Screen-reader-only helper text ("opens in a new window") duplicates what is visible.
        if (node.ClassName.Contains("sr-only", StringComparison.OrdinalIgnoreCase)
            || node.ClassName.Contains("visually-hidden", StringComparison.OrdinalIgnoreCase)
            || node.ClassName.Contains("screen-reader", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Split the class name on spaces, underscores and hyphens and test each word, so "sc-ad-container" yields "sc", "ad", "container".
        foreach (var word in ClassWordRegex().Split(node.ClassName))
        {
            if (ClutterClassWords.Contains(word) || (articleMode && ArticleClutterClassWords.Contains(word)))
            {
                return true;
            }
        }

        return false;
    }

    // Final tidy-up of article text: drops everything from an "end section" heading (References, Related articles...)
    // until a heading of the same or higher level, and removes non-prose such as ratings and counters.
    private static List<PageBlock> CleanArticle(List<PageBlock> blocks)
    {
        var kept = new List<PageBlock>();
        var skipUntilLevel = 0;

        foreach (var block in RemoveDuplicates(blocks))
        {
            if (block.Kind == "heading")
            {
                var level = block.HeadingLevel == 0 ? 2 : block.HeadingLevel;

                if (skipUntilLevel > 0 && level > skipUntilLevel)
                {
                    continue;
                }

                skipUntilLevel = 0;

                // "Reviews (14,910)" and "See also [edit]" are still end sections: compare letters only.
                // Reduces a heading to plain letters and single spaces: "See also [edit]" becomes "See also" and "Reviews (14,910)" becomes "Reviews".
                var name = Regex.Replace(Regex.Replace(block.Text, @"\[\s*edit\s*\]|[^\p{L}\s]", " "), @"\s+", " ").Trim();

                if (EndSectionHeadings.Contains(name))
                {
                    skipUntilLevel = level;
                    continue;
                }
            }
            else if (skipUntilLevel > 0)
            {
                continue;
            }
            else if (block.Kind == "paragraph"
                && (!block.Text.Any(char.IsLetter)
                    || (block.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4 && !Regex.IsMatch(block.Text, @"[.!?][""'”’]?$"))))
            {
                // Ratings, counters, and player readouts ("4.6", "(19,468)", "4,925 PHOTOS", "00:00") are not prose.
                continue;
            }

            kept.Add(block);
        }

        // A heading with nothing under it (its section was clutter) is only noise.
        return kept.Where((block, index) => block.Kind != "heading" || (index + 1 < kept.Count && kept[index + 1].Kind != "heading")).ToList();
    }

    // Drops repeated text (pages often show the same snippet twice). Headings and very short blocks (under 12 characters) are always kept.
    private static List<PageBlock> RemoveDuplicates(List<PageBlock> blocks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return blocks.Where(block => block.Kind == "heading" || block.Text.Length < 12 || seen.Add(block.Text)).ToList();
    }

    // Pieces come from separate elements: "In 1968, " + "Spencer Silver" + ", a scientist". Add a space only
    // where two words would otherwise run together: "FROM" + "WBUR", "innovation." + "Post-its",
    // "encyclopedia" + "(Redirected".
    private static void AppendPiece(StringBuilder builder, string piece)
    {
        if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]) && !char.IsWhiteSpace(piece[0]))
        {
            var previous = builder[^1];
            var next = piece[0];
            var startsWord = char.IsLetterOrDigit(next) || next is '"' or '“' or '(';

            if ((char.IsLetterOrDigit(previous) && (char.IsLetterOrDigit(next) || next is '(' or '“'))
                || (previous is '.' or '!' or '?' or ')' or '"' or '”' && startsWord))
            {
                builder.Append(' ');
            }
        }

        builder.Append(piece);
    }

    // Collapses any run of whitespace (including line breaks) into one space.
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    // True if the text already ends with sentence punctuation or a closing quote/bracket.
    private static bool EndsSentence(string text) => text.Length > 0 && ".!?:;\"'”’)".Contains(text[^1]);

    // Matches a link that is only a citation marker or edit link: "[12]", "[a]", "[note 3]", "[citation needed]", "[edit]" or "edit".
    [GeneratedRegex(@"^(?:\[(?:\d+|[a-z]{1,2}|note \d+|citation needed|edit)\]|edit)$", RegexOptions.IgnoreCase)]
    private static partial Regex CitationRegex();

    // Matches a run of spaces, underscores or hyphens (the separators inside CSS class names).
    [GeneratedRegex(@"[\s_\-]+")]
    private static partial Regex ClassWordRegex();
}

/// <summary>Summarizes page text with the local model, in chunks when the page is longer than its context.</summary>
public static partial class PageSummarizer
{
    // Characters per chunk when the page is too long for one model call: about 3,000 tokens, inside the local model's 8K context.
    private const int ChunkCharacters = 12_000;
    // Pages longer than 5 chunks are cut off and the summary says it covers only the first part.
    private const int MaximumChunks = 5;

    // Prompt for the final summary. It is read aloud, so no lists, markdown or emoji, and about 100 words.
    private const string SummaryInstructions =
        "You summarize the web page the user is looking at. Your summary is read aloud.\n"
        + "- Write 3 or 4 plain sentences, under 100 words in total: no lists, headings, markdown, or emoji.\n"
        + "- Start with what the page is (a news article, recipe, product page, inbox, search results, documentation, ...) and its subject.\n"
        + "- Then give the most important information: key points, names, numbers, prices, dates, and conclusions.\n"
        + "- Use only what the page says. If a focus is given, answer about that focus from the page.";

    // Prompt for the first pass on long pages: pull out the key facts of each chunk, which are then summarized together.
    private const string NotesInstructions =
        "Extract the key facts from this part of a web page as 3 to 6 short plain sentences. Use only facts from the text. No lists or markdown.";

    // Summarizes the page. One chunk goes straight to the model. Several chunks are first turned into short notes
    // (one model call each) and the notes are then summarized: a "map then reduce" approach.
    public static async Task<string> SummarizeAsync(
        IModelGateway gateway,
        ExtractedPage page,
        string focus,
        CancellationToken cancellationToken)
    {
        // An article's body when there is one; otherwise, or when asked about something specific that may sit
        // outside the article (its reviews, comments, specs), the whole page.
        var blocks = string.IsNullOrWhiteSpace(focus) && ExtractedPage.CountWords(page.Article) >= 150 ? page.Article : page.Page;
        var chunks = SplitIntoChunks(PageContentExtractor.ToReadableText(blocks));
        var truncated = chunks.Count > MaximumChunks;
        var header = $"Title: {page.Title}\nSite: {DescribeSite(page.Url)}\n"
            + (string.IsNullOrWhiteSpace(focus) ? string.Empty : $"Focus: {focus}\n");

        string content;

        if (chunks.Count == 1)
        {
            content = chunks[0];
        }
        else
        {
            // Too long for one pass: note the facts in each part, then summarize the notes.
            var notes = new List<string>();

            foreach (var chunk in chunks.Take(MaximumChunks))
            {
                notes.Add(await CompleteAsync(gateway, NotesInstructions, $"{header}\nPart of the page:\n{chunk}", cancellationToken));
            }

            content = string.Join("\n", notes);
        }

        var summary = await CompleteAsync(gateway, SummaryInstructions, $"{header}\nPage content:\n{content}", cancellationToken);
        return truncated ? summary + " (This covers the first part of a very long page.)" : summary;
    }

    // One model call with a system prompt and a user message. Gives up after 90 seconds, then cleans the reply.
    // Reasoning, not Fast: Fast's 256-token cap can cut summaries off mid-sentence. Reasoning allows 384 on the local model.
    private static async Task<string> CompleteAsync(IModelGateway gateway, string instructions, string input, CancellationToken cancellationToken)
    {
        var text = await ModelCalls.CompleteTextAsync(gateway, instructions, input, ModelRoute.Reasoning, 0.2, TimeSpan.FromSeconds(90), cancellationToken);
        // Read aloud, so markdown symbols would be spoken or mangled.
        // Removes list markers at the start of a line ("- ", "* ", "1. ") and markdown characters (* # _ `).
        text = Regex.Replace(text, @"^\s*(?:[-*•]|\d+\.)\s+|[*#_`]+", string.Empty, RegexOptions.Multiline);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    // Split at line breaks, which fall between paragraphs, so no sentence is cut in half.
    // Packs whole lines into chunks of up to ChunkCharacters; a single over-long line is cut to fit.
    private static List<string> SplitIntoChunks(string text)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var line in text.Split('\n'))
        {
            if (current.Length > 0 && current.Length + line.Length > ChunkCharacters)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            current.AppendLine(line.Length > ChunkCharacters ? line[..ChunkCharacters] : line);
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks.Count == 0 ? [string.Empty] : chunks;
    }

    // The website name (host) for the prompt, e.g. "www.example.com"; falls back to the raw text.
    private static string DescribeSite(string url) =>
        Uri.TryCreate(url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url, UriKind.Absolute, out var uri)
            ? uri.Host
            : url;
}

/// <summary>Reads aloud or summarizes the page open in the browser.</summary>
public sealed class PageTool(IBrowserPageReader? pageReader, IModelGateway? modelGateway) : IAssistantTool
{
    // ponytail: about 20 minutes of speech; say "read this page" again after scrolling for the rest if needed.
    private const int MaximumReadWords = 3_000;

    public string Name => "page";

    public string Description =>
        "The web page open in the user's browser. `page read` reads its main text aloud (just the article, without menus or ads). "
        + "`page summarize` or `page summarize <focus>` summarizes it, for example `page summarize the reviews`.";

    // Handles any input that starts with the word "page".
    public bool CanHandle(string input) => ToolInputHelper.StartsWithCommand(input, "page");

    // Reads the page from the browser, cleans it, then either reads it out ("page read") or summarizes it ("page summarize [focus]").
    public async Task<ToolResult> ExecuteAsync(string input, ToolContext context, CancellationToken cancellationToken)
    {
        var tail = ToolInputHelper.GetCommandTail(input, "page").Trim();
        var summarize = tail.StartsWith("summar", StringComparison.OrdinalIgnoreCase);

        if (!summarize && !tail.StartsWith("read", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolResult("Usage: page read, or page summarize [focus]", Succeeded: false);
        }

        var page = pageReader is null ? null : await pageReader.ReadActivePageAsync(cancellationToken);

        if (page is null)
        {
            return new ToolResult(
                "I couldn't find a page to read. Open it in Chrome or Edge and ask again.",
                Succeeded: false);
        }

        var extracted = PageContentExtractor.Extract(page);
        return summarize
            ? await SummarizeAsync(extracted, Regex.Replace(tail, @"^summar\w*\s*", string.Empty, RegexOptions.IgnoreCase), cancellationToken)
            : Read(extracted);
    }

    // Builds the text to speak: the article if it has at least 80 words, otherwise the whole page, with a spoken intro
    // giving the title and estimated minutes (160 words per minute).
    private static ToolResult Read(ExtractedPage page)
    {
        var isArticle = ExtractedPage.CountWords(page.Article) >= 80;
        var blocks = isArticle ? page.Article : page.Page;

        if (blocks.Count == 0)
        {
            return new ToolResult("I couldn't find any readable text on this page.", Succeeded: false);
        }

        // Keep whole blocks up to the limit so reading stops at a paragraph end.
        var kept = new List<PageBlock>();
        var words = 0;

        foreach (var block in blocks)
        {
            var blockWords = block.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            if (words > 0 && words + blockWords > MaximumReadWords)
            {
                break;
            }

            kept.Add(block);
            words += blockWords;
        }

        var minutes = Math.Max(1, (int)Math.Round(words / 160.0));
        var intro = (isArticle ? $"Reading \"{page.Title}\"" : $"This page isn't an article, so here is its text: \"{page.Title}\"")
            + $", about {minutes} minute{(minutes == 1 ? string.Empty : "s")}."
            + (kept.Count < blocks.Count ? " It's long, so I'll read the first part." : string.Empty);

        return new ToolResult(
            $"{intro}\n\n{PageContentExtractor.ToReadableText(kept)}",
            SummaryText: $"Read {words} words from {page.Title}.",
            SpeakInFull: true);
    }

    // Checks that the model is running and the page has enough text, then summarizes it.
    private async Task<ToolResult> SummarizeAsync(ExtractedPage page, string focus, CancellationToken cancellationToken)
    {
        if (modelGateway is not { IsAvailable: true })
        {
            return new ToolResult("I need the local model running to summarize pages. Run scripts\\Setup-LocalLlm.ps1 once.", Succeeded: false);
        }

        // With next to no text the model summarizes from what it already knows and invents the rest.
        if (ExtractedPage.CountWords(page.Page) < 40)
        {
            return new ToolResult("There's too little readable text on this page to summarize.", Succeeded: false);
        }

        try
        {
            var summary = await PageSummarizer.SummarizeAsync(modelGateway, page, focus, cancellationToken);
            return summary.Length == 0
                ? new ToolResult("The local model returned an empty summary. Please try again.", Succeeded: false)
                : new ToolResult(summary, SummaryText: $"Summarized {page.Title}.", SpeakInFull: true);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new ToolResult($"I couldn't summarize the page: {exception.Message}", Succeeded: false);
        }
    }
}
