// UiaBrowserPageReader.cs: reads the web page currently on screen in Chrome, Edge, Brave, Vivaldi or Opera
// by asking Windows UI Automation (the accessibility system) for the browser's element tree. Implements
// IBrowserPageReader (defined in Jarvis.Core); callers ask for the active page and get a BrowserPage back:
// title, address and a tree of PageNodes (headings, links, text...). Nothing is injected into the browser.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Jarvis.Core;

namespace Jarvis.App;

/// <summary>
/// Reads the page in the frontmost Chromium browser through Windows UI Automation, the interface screen
/// readers use. It sees exactly what is on screen, including pages behind a sign-in, and needs no browser
/// extension or debugging port.
/// </summary>
internal sealed class UiaBrowserPageReader : IBrowserPageReader
{
    // The process names (without .exe) treated as browsers.
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "opera"
    };

    // UIA_HeadingLevelPropertyId. The managed UIA wrapper predates it, so it is looked up by id; values run
    // from HeadingLevel_None (80050) through HeadingLevel9 (80059).
    private static readonly AutomationProperty? HeadingLevelProperty = AutomationProperty.LookupById(30173);

    public Task<BrowserPage?> ReadActivePageAsync(CancellationToken cancellationToken) =>
        // Off the UI thread: UI Automation calls into another process and must not block the form.
        Task.Run(() => Read(cancellationToken), cancellationToken);

    // Find the page to read. Runs on a worker thread (see ReadActivePageAsync).
    private static BrowserPage? Read(CancellationToken cancellationToken)
    {
        // UI Automation represents the web page itself as a control of type "Document".
        var documentCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
        BrowserPage? page = null;

        // Chrome builds its accessibility tree only once something asks for it, so the first look at a page
        // can come back empty or partial. Retry briefly until the page has real content.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Front to back. Pop-ups (a first-run prompt, a download bubble) are browser windows too, but hold
            // no page, so the first window that contains one is the page being looked at.
            foreach (var handle in FindBrowserWindows())
            {
                var window = AutomationElement.FromHandle(handle);
                var document = window.FindFirst(TreeScope.Descendants, documentCondition);

                if (document is null)
                {
                    continue;
                }

                page = Capture(document, window);

                // A page with fewer than 20 accessibility nodes is treated as still loading; 20 is a rough "has real content" cutoff.
                if (CountNodes(page.Document) >= 20)
                {
                    return page;
                }

                // That page is still loading: wait for it rather than read a window behind it.
                break;
            }

            // Wait 400 ms before trying again (8 tries, so up to about 3 seconds).
            Thread.Sleep(400);
        }

        return page;
    }

    // Copy the page's accessibility tree into our own PageNode tree, plus its title and address.
    private static BrowserPage Capture(AutomationElement document, AutomationElement window)
    {
        // One cross-process request for the whole tree instead of one per element: about 0.1 to 0.3 s even
        // for a long article.
        // Cache request: fetch these properties for the whole subtree in one go, as plain data (AutomationElementMode.None means no live element handles are kept).
        var cache = new CacheRequest
        {
            TreeScope = TreeScope.Subtree,
            AutomationElementMode = AutomationElementMode.None
        };
        cache.Add(AutomationElement.LocalizedControlTypeProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ClassNameProperty);

        if (HeadingLevelProperty is not null)
        {
            cache.Add(HeadingLevelProperty);
        }

        AutomationElement root;

        // Activating the cache makes GetUpdatedCache download the subtree with the properties listed above.
        using (cache.Activate())
        {
            root = document.GetUpdatedCache(cache);
        }

        return new BrowserPage(root.Cached.Name, ReadAddress(window), ToNode(root));
    }

    // Convert one cached UI Automation element, and all its children, into a PageNode.
    private static PageNode ToNode(AutomationElement element)
    {
        var children = new List<PageNode>();

        foreach (AutomationElement child in element.CachedChildren)
        {
            children.Add(ToNode(child));
        }

        // Heading level is 0 for "not a heading". UIA reports heading levels as 80051..80059 (H1..H9), so subtract 80050.
        var headingLevel = 0;

        if (HeadingLevelProperty is not null
            && element.GetCachedPropertyValue(HeadingLevelProperty, ignoreDefaultValue: true) is int level
            && level is > 80050 and <= 80059)
        {
            headingLevel = level - 80050;
        }

        var cached = element.Cached;
        return new PageNode(cached.LocalizedControlType ?? string.Empty, cached.Name ?? string.Empty, cached.ClassName ?? string.Empty, headingLevel, children);
    }

    // Total number of nodes in a tree: this node plus all descendants.
    private static int CountNodes(PageNode node) => 1 + node.Children.Sum(CountNodes);

    // The address bar is Chromium's omnibox; its class name is the same in Chrome, Edge, and Brave.
    private static string ReadAddress(AutomationElement window)
    {
        try
        {
            var omnibox = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, "OmniboxViewViews"));

            return omnibox?.GetCurrentPattern(ValuePattern.Pattern) is ValuePattern value ? value.Current.Value : string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    // Top-level windows come back front to back, so the order is the one on screen. Minimized windows go
    // last: they are used only if no browser window is showing.
    private static List<IntPtr> FindBrowserWindows()
    {
        var visible = new List<IntPtr>();
        var minimized = new List<IntPtr>();

        // EnumWindows calls this lambda once per top-level window, front to back; returning true means "keep going".
        EnumWindows((handle, parameter) =>
        {
            // Skip hidden windows, windows with no title, and anything that is not a Chromium-based window.
            if (!IsWindowVisible(handle) || GetWindowTextLength(handle) == 0 || !IsChromiumWindow(handle))
            {
                return true;
            }

            // Look up which process owns the window, to check it is a known browser (not, say, an Electron app that shares the window class).
            _ = GetWindowThreadProcessId(handle, out var processId);

            try
            {
                using var process = Process.GetProcessById((int)processId);

                if (!BrowserProcesses.Contains(process.ProcessName))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            // Minimized browsers go on the second list.
            (IsIconic(handle) ? minimized : visible).Add(handle);
            return true;
        }, IntPtr.Zero);

        // Visible windows first, then minimized ones.
        return [.. visible, .. minimized];
    }

    // Chrome, Edge, Brave and other Chromium browsers all use the window class "Chrome_WidgetWin_1" for their frames.
    private static bool IsChromiumWindow(IntPtr handle)
    {
        var className = new StringBuilder(64);
        _ = GetClassName(handle, className, className.Capacity);
        return className.ToString() == "Chrome_WidgetWin_1";
    }

    // Signature of the callback that EnumWindows expects.
    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    // Win32 calls from user32.dll (the Windows window manager): list windows, check visible/minimized, read title length and class name, find the owning process.
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}
