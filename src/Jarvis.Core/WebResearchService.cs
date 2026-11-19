// WebResearchService.cs: a simple web search for Jarvis. It fetches DuckDuckGo's plain-HTML results page and pulls
// out titles, links and snippets with regular expressions (no search API key needed). The planner's web research
// tool calls ResearchAsync and shows the results as citations. This is a normal internet request, so it is not
// covered by the local-only AI setting. If DuckDuckGo changes its page layout, the regexes below will stop matching.
using System.Net;
using System.Text.RegularExpressions;
using System.Web;

namespace Jarvis.Core;

// The outcome of a search: the cleaned query, the sources found, whether the web was unreachable (IsOffline) and a one-line summary.
public sealed record WebResearchResult(
    string Query,
    AssistantCitation[] Citations,
    bool IsOffline,
    string SummaryText);

// Lets the planner search the web without knowing which search site is behind it.
public interface IWebResearchService
{
    Task<WebResearchResult> ResearchAsync(string query, int maxResults, CancellationToken cancellationToken);
}

// Searches by scraping html.duckduckgo.com.
public sealed class DuckDuckGoWebResearchService : IWebResearchService
{
    // Matches a result's title link, e.g. <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com">Example</a>.
    // Captures "href" (the link) and "title" (the link text, which may still contain tags).
    private static readonly Regex ResultRegex = new(
        "<a[^>]*class=\"result__a\"[^>]*href=\"(?<href>[^\"]+)\"[^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches the short description under a result; DuckDuckGo uses either an <a> or a <div> with class result__snippet,
    // so there are two alternatives (captured as "snippet" and "snippet2").
    private static readonly Regex SnippetRegex = new(
        "<a[^>]*class=\"result__snippet\"[^>]*>(?<snippet>.*?)</a>|<div[^>]*class=\"result__snippet\"[^>]*>(?<snippet2>.*?)</div>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;

    public DuckDuckGoWebResearchService()
    {
        // 15-second timeout, and a User-Agent so the request identifies itself as Jarvis.
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Jarvis/2.0 (+https://localhost)");
    }

    // Runs the search and never throws for ordinary failures: network errors come back as IsOffline = true with a message.
    // maxResults is limited to 1-8. Only a user cancellation is allowed to propagate.
    public async Task<WebResearchResult> ResearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new WebResearchResult(string.Empty, Array.Empty<AssistantCitation>(), false, "No query was provided.");
        }

        try
        {
            var citations = await SearchDuckDuckGoAsync(query.Trim(), Math.Clamp(maxResults, 1, 8), cancellationToken);

            if (citations.Length > 0)
            {
                return new WebResearchResult(
                    query.Trim(),
                    citations,
                    IsOffline: false,
                    $"Found {citations.Length} web source{(citations.Length == 1 ? string.Empty : "s")}.");
            }

            return new WebResearchResult(
                query.Trim(),
                Array.Empty<AssistantCitation>(),
                IsOffline: false,
                "No public web results were found.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new WebResearchResult(
                query.Trim(),
                Array.Empty<AssistantCitation>(),
                IsOffline: true,
                $"Web research is unavailable right now: {TrimError(exception.Message)}");
        }
    }

    // Downloads the results page and pairs the Nth title match with the Nth snippet match to build each citation.
    private async Task<AssistantCitation[]> SearchDuckDuckGoAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Search HTTP {(int)response.StatusCode}");
        }

        var titleMatches = ResultRegex.Matches(body);
        var citations = new List<AssistantCitation>();

        for (var index = 0; index < titleMatches.Count && citations.Count < maxResults; index++)
        {
            var match = titleMatches[index];
            var rawUrl = HtmlDecode(match.Groups["href"].Value);
            var resolvedUrl = ResolveDuckDuckGoRedirect(rawUrl);

            if (string.IsNullOrWhiteSpace(resolvedUrl))
            {
                continue;
            }

            var title = StripHtml(HtmlDecode(match.Groups["title"].Value));

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            // The snippet is the first one between this title and the next, so a result without one cannot shift the rest.
            var from = match.Index + match.Length;
            var to = index + 1 < titleMatches.Count ? titleMatches[index + 1].Index : body.Length;
            var snippetMatch = SnippetRegex.Match(body, from, to - from);
            var snippet = snippetMatch.Success
                ? StripHtml(HtmlDecode(snippetMatch.Groups["snippet"].Success ? snippetMatch.Groups["snippet"].Value : snippetMatch.Groups["snippet2"].Value))
                : string.Empty;

            citations.Add(new AssistantCitation(title, resolvedUrl, snippet));
        }

        return citations.ToArray();
    }

    // DuckDuckGo wraps result links as /l/?uddg=<encoded real address>. This returns the real address, or the link itself
    // if it is already direct (not on duckduckgo.com) or has no uddg part.
    private static string ResolveDuckDuckGoRedirect(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var absolute)
            && !absolute.Host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            return absolute.ToString();
        }

        if (!Uri.TryCreate("https://duckduckgo.com" + rawUrl, UriKind.Absolute, out var redirectUri))
        {
            return rawUrl;
        }

        return HttpUtility.ParseQueryString(redirectUri.Query)["uddg"] ?? rawUrl;
    }

    // Turns HTML entities such as &amp; back into normal characters.
    private static string HtmlDecode(string value) => WebUtility.HtmlDecode(value).Trim();

    // Removes any <tags> from the text (the lazy pattern "<.*?>" stops at the first ">") and decodes entities.
    private static string StripHtml(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var noTags = Regex.Replace(value, "<.*?>", string.Empty);
        return WebUtility.HtmlDecode(noTags).Trim();
    }

    // Shortens an error message to 240 characters for display.
    private static string TrimError(string text)
    {
        const int maxLength = 240;
        var value = text.Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
