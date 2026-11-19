// TutorPointerForm.cs: the on-screen "pointer" for the screen tutor. When the tutor says "click this", this
// borderless transparent window draws a ring around the control and a short text bubble. It is created and
// driven by WindowsTutorHost.cs (Arm to show, Disarm to hide). The user can click straight through it, and
// it reports clicks back to the host via the onPress / onRelease callbacks.
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Jarvis.Core;

namespace Jarvis.App;

/// <summary>
/// The tutor's pointer: a ring around one control and a label bubble, drawn on a borderless, click-through
/// window over the target's monitor. It never takes focus and is left out of screen captures (Jarvis's own
/// screenshots would otherwise see it). It also polls the left mouse button, so the lesson can tell when
/// the pointed control was clicked without installing a hook.
/// </summary>
internal sealed class TutorPointerForm : Form
{
    // Extended window style flags (Win32 WS_EX_*) used in CreateParams below: tool window (not in Alt+Tab), click-through, layered (supports transparency), and never takes focus.
    internal const int WsExToolWindow = 0x80;
    internal const int WsExTransparent = 0x20;
    internal const int WsExLayered = 0x80000;
    internal const int WsExNoActivate = 0x08000000;
    // Value for SetWindowDisplayAffinity (WDA_EXCLUDEFROMCAPTURE): hides this window from screenshots and screen recordings.
    internal const uint DisplayAffinityExcludeFromCapture = 0x11;

    // Magenta is treated as fully transparent (TransparencyKey), so only the ring and bubble are visible.
    private static readonly Color KeyColor = Color.FromArgb(255, 0, 255);
    // The teal accent colour of the ring and bubble border.
    private static readonly Color Accent = Color.FromArgb(106, 220, 197);

    // Checks the mouse button every 30 ms (see PollMouse).
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 30 };
    // The control to circle, in screen pixels; null when the step has nothing to click.
    private TutorRect? _ring;
    // Text shown in the bubble.
    private string _label = string.Empty;
    // The tutored window's bounds; used to place the bubble when there is no ring.
    private Rectangle _anchor;
    // Callbacks for mouse press and release (screen x, y) on something other than Jarvis itself.
    private Action<int, int>? _onPress;
    private Action<int, int>? _onRelease;
    // Was the left button down at the previous poll? Used to spot the moment of a press or release.
    private bool _wasDown;
    // Did the current press start outside Jarvis's own windows? Only those releases are reported.
    private bool _pressSeen;

    public TutorPointerForm()
    {
        // Window set-up: no border, no taskbar button, above other windows, positioned by us, with magenta as the see-through colour.
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        DoubleBuffered = true;
        _poll.Tick += (_, _) => PollMouse();
    }

    // Showing this window must not steal keyboard focus from the app being tutored.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // Layered + transparent (clicks pass through) + tool window + no-activate.
            parameters.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Once the window exists, exclude it from screen captures so Jarvis's own screenshots do not contain the pointer.
        _ = SetWindowDisplayAffinity(Handle, DisplayAffinityExcludeFromCapture);
    }

    /// <summary>Shows the pointer over <paramref name="monitor"/> (physical pixels). A null ring means a step with
    /// nothing to click: only the label is drawn, near the top of the target window.</summary>
    public void Arm(Rectangle monitor, TutorRect? ring, string label, Rectangle anchor, Action<int, int>? onPress, Action<int, int>? onRelease)
    {
        _ring = ring;
        _label = label;
        _anchor = anchor;
        _onPress = onPress;
        _onRelease = onRelease;
        _pressSeen = false;
        // GetAsyncKeyState(0x01) reads the left mouse button (VK_LBUTTON); the top bit means it is currently down.
        _wasDown = (GetAsyncKeyState(0x01) & 0x8000) != 0; // a button already held belongs to the last step
        Bounds = monitor;

        if (!Visible)
        {
            Show();
            Bounds = monitor; // a mixed-DPI monitor can resize it on WM_DPICHANGED while showing
        }

        Invalidate();
        _poll.Enabled = ring is not null;
    }

    // Hide the pointer and stop watching the mouse.
    public void Disarm()
    {
        _poll.Stop();
        _onPress = null;
        _onRelease = null;
        Hide();
    }

    // Called every 30 ms. Detects a left-button press and release by comparing with the previous state; clicks on Jarvis's own windows are ignored.
    private void PollMouse()
    {
        var down = (GetAsyncKeyState(0x01) & 0x8000) != 0;

        if (GetCursorPos(out var point))
        {
            if (down && !_wasDown)
            {
                _pressSeen = !IsOwnWindow(point);

                if (_pressSeen)
                {
                    _onPress?.Invoke(point.X, point.Y);
                }
            }
            else if (!down && _wasDown && _pressSeen)
            {
                _pressSeen = false;

                if (!IsOwnWindow(point))
                {
                    _onRelease?.Invoke(point.X, point.Y);
                }
            }
        }

        _wasDown = down;
    }

    // Is this screen point over one of Jarvis's own windows?
    private static bool IsOwnWindow(POINT point)
    {
        var window = WindowFromPoint(point);

        if (window == IntPtr.Zero)
        {
            return false;
        }

        // Find the top-level window (GA_ROOT = 2) containing that point and compare its process with ours.
        _ = GetWindowThreadProcessId(GetAncestor(window, 2 /* GA_ROOT */), out var processId);
        return processId == Environment.ProcessId;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        Rectangle? ring = null;

        if (_ring is { } r)
        {
            // Window coordinates: the form sits on the monitor, the ring is in screen pixels.
            ring = new Rectangle(r.Left - Left - 4, r.Top - Top - 4, r.Width + 8, r.Height + 8);
            // No anti-aliasing: the form is colour-keyed, and smoothed edges would keep a magenta fringe.
            using var outer = new Pen(Color.FromArgb(20, 28, 38), 8);
            using var inner = new Pen(Accent, 4);
            g.DrawRectangle(outer, ring.Value);
            g.DrawRectangle(inner, ring.Value);
        }

        if (_label.Length == 0)
        {
            return;
        }

        using var font = new Font("Segoe UI", 11f, FontStyle.Bold);
        // Measure the label (wrapping at 420 pixels wide) to size the bubble.
        var size = TextRenderer.MeasureText(_label, font, new Size(420, 0), TextFormatFlags.WordBreak);
        var bubble = new Rectangle(0, 0, size.Width + 20, size.Height + 12);

        // With a ring: put the bubble under it, or above it if it would run off the screen.
        if (ring is { } box)
        {
            bubble.X = box.Left;
            bubble.Y = box.Bottom + 10 + bubble.Height <= Height ? box.Bottom + 10 : box.Top - 10 - bubble.Height;
        }
        else
        {
            bubble.X = _anchor.Left - Left + Math.Max(20, (_anchor.Width - bubble.Width) / 2);
            bubble.Y = _anchor.Top - Top + 20;
        }

        // Keep the bubble fully on the screen.
        bubble.X = Math.Clamp(bubble.X, 0, Math.Max(0, Width - bubble.Width));
        bubble.Y = Math.Clamp(bubble.Y, 0, Math.Max(0, Height - bubble.Height));

        using var fill = new SolidBrush(Color.FromArgb(20, 28, 38));
        using var border = new Pen(Accent, 2);
        g.FillRectangle(fill, bubble);
        g.DrawRectangle(border, bubble);
        TextRenderer.DrawText(g, _label, font, new Rectangle(bubble.X + 10, bubble.Y + 6, size.Width, size.Height), Color.White, TextFormatFlags.WordBreak);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
        }

        base.Dispose(disposing);
    }

    // Win32 POINT: a screen coordinate pair.
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // Win32 calls from user32.dll: mouse button state, cursor position, which window is under a point, its root window and owning process, and the screenshot-hiding flag.
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
