using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// An interactive line editor for console input on platforms whose console does
/// not provide one.
///
/// Why this exists:
///   On Windows, Console.ReadLine() is serviced by the Win32 console host, which
///   already supplies both line editing (arrow keys, Home/End, Insert/Delete)
///   and up/down recall of previously entered lines. Nothing is missing there,
///   so by default this editor stays out of the way on Windows.
///   On Linux/macOS, .NET does NOT link GNU readline; Console.ReadLine() simply
///   reads the terminal in canonical mode. That gives you Backspace and nothing
///   else -- arrow keys arrive as raw escape sequences (ESC [ D) and are
///   discarded, so the cursor cannot be moved and there is no history recall.
///
/// This class reads individual keys via Console.ReadKey(intercept: true) and
/// maintains the edit buffer, cursor position and history itself, bringing
/// Linux/macOS up to the behaviour Windows users already have.
///
/// History is kept in memory for the lifetime of the process only, matching the
/// Windows console host's behaviour: it is not written to disk and does not
/// survive a restart.
/// </summary>
public static class ConsoleLineEditor
{
    private const int MaxHistoryEntries = 500;

    private static readonly List<string> _history = new();

    /// <summary>
    /// Reads a line of input with full editing support and in-process history.
    /// Returns null on EOF (Ctrl+D on an empty line), matching Console.ReadLine().
    ///
    /// Platform behaviour:
    ///   Windows - the console host already provides line editing AND up/down
    ///             history recall for Console.ReadLine(), so we defer to it by
    ///             default and leave that working behaviour untouched.
    ///   Other   - .NET does not link GNU readline, so Console.ReadLine() offers
    ///             no editing or history at all; this editor supplies both.
    ///
    /// Set CYCOD_RICH_INPUT=1 to force this editor on, or =0 to force it off.
    /// </summary>
    public static string? ReadLine()
    {
        // Piped/redirected input has no terminal to edit on.
        if (Console.IsInputRedirected) return Console.ReadLine();

        if (!ShouldUseEditor()) return Console.ReadLine();

        try
        {
            return ReadLineInteractive();
        }
        catch (IOException)
        {
            // No usable terminal (e.g. no cursor positioning). Degrade gracefully.
            return Console.ReadLine();
        }
        catch (InvalidOperationException)
        {
            return Console.ReadLine();
        }
    }

    /// <summary>
    /// Decides whether to use this editor or defer to Console.ReadLine().
    /// Explicit configuration wins; otherwise the platform default applies.
    /// </summary>
    private static bool ShouldUseEditor()
    {
        var configured = Environment.GetEnvironmentVariable("CYCOD_RICH_INPUT");
        if (!string.IsNullOrEmpty(configured))
        {
            var on = configured.Equals("1", StringComparison.OrdinalIgnoreCase)
                || configured.Equals("true", StringComparison.OrdinalIgnoreCase)
                || configured.Equals("on", StringComparison.OrdinalIgnoreCase)
                || configured.Equals("yes", StringComparison.OrdinalIgnoreCase);
            return on;
        }

        // Default: only where the platform does not already provide editing.
        return !OperatingSystem.IsWindows();
    }

    private static string? ReadLineInteractive()
    {
        var buffer = new StringBuilder();
        var cursor = 0;              // insertion point within buffer
        var previousRenderLength = 0; // so we can erase leftovers on shrink

        // The prompt has already been written by the caller; whatever column we
        // start at is where the editable region begins.
        var startLeft = Console.CursorLeft;
        var startTop = Console.CursorTop;

        // Index into history while browsing with Up/Down. _history.Count means
        // "not browsing" (i.e. showing the live buffer).
        var historyIndex = _history.Count;
        var savedLiveBuffer = string.Empty;

        while (true)
        {
            var keyInfo = Console.ReadKey(intercept: true);
            var key = keyInfo.Key;
            var ctrl = (keyInfo.Modifiers & ConsoleModifiers.Control) != 0;

            if (key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                var result = buffer.ToString();
                AddToHistory(result);
                return result;
            }

            // Ctrl+D on an empty line is EOF, as on any Unix shell.
            if (ctrl && key == ConsoleKey.D && buffer.Length == 0)
            {
                Console.WriteLine();
                return null;
            }

            if (key == ConsoleKey.LeftArrow && !ctrl)
            {
                if (cursor > 0) cursor--;
            }
            else if (key == ConsoleKey.RightArrow && !ctrl)
            {
                if (cursor < buffer.Length) cursor++;
            }
            else if (key == ConsoleKey.LeftArrow && ctrl)
            {
                cursor = FindPreviousWordStart(buffer, cursor);
            }
            else if (key == ConsoleKey.RightArrow && ctrl)
            {
                cursor = FindNextWordStart(buffer, cursor);
            }
            else if (key == ConsoleKey.Home || (ctrl && key == ConsoleKey.A))
            {
                cursor = 0;
            }
            else if (key == ConsoleKey.End || (ctrl && key == ConsoleKey.E))
            {
                cursor = buffer.Length;
            }
            else if (key == ConsoleKey.Backspace)
            {
                if (cursor > 0)
                {
                    buffer.Remove(cursor - 1, 1);
                    cursor--;
                }
            }
            else if (key == ConsoleKey.Delete)
            {
                if (cursor < buffer.Length) buffer.Remove(cursor, 1);
            }
            else if (ctrl && key == ConsoleKey.U)
            {
                // Kill to start of line.
                buffer.Remove(0, cursor);
                cursor = 0;
            }
            else if (ctrl && key == ConsoleKey.K)
            {
                // Kill to end of line.
                buffer.Remove(cursor, buffer.Length - cursor);
            }
            else if (ctrl && key == ConsoleKey.W)
            {
                // Delete the word before the cursor.
                var wordStart = FindPreviousWordStart(buffer, cursor);
                buffer.Remove(wordStart, cursor - wordStart);
                cursor = wordStart;
            }
            else if (key == ConsoleKey.UpArrow || key == ConsoleKey.DownArrow)
            {
                if (_history.Count == 0) continue;

                if (historyIndex == _history.Count)
                {
                    // Entering history browse: remember what the user was typing.
                    savedLiveBuffer = buffer.ToString();
                }

                if (key == ConsoleKey.UpArrow && historyIndex > 0)
                {
                    historyIndex--;
                }
                else if (key == ConsoleKey.DownArrow && historyIndex < _history.Count)
                {
                    historyIndex++;
                }

                var replacement = historyIndex == _history.Count
                    ? savedLiveBuffer
                    : _history[historyIndex];

                buffer.Clear();
                buffer.Append(replacement);
                cursor = buffer.Length;
            }
            else if (!char.IsControl(keyInfo.KeyChar))
            {
                buffer.Insert(cursor, keyInfo.KeyChar);
                cursor++;
            }
            else
            {
                // Unhandled control key: ignore rather than corrupt the buffer.
                continue;
            }

            // Any edit (other than history navigation) leaves browse mode.
            if (key != ConsoleKey.UpArrow && key != ConsoleKey.DownArrow)
            {
                historyIndex = _history.Count;
            }

            previousRenderLength = Render(buffer, cursor, startLeft, ref startTop, previousRenderLength);
        }
    }

    /// <summary>
    /// Repaints the editable region and places the cursor at the insertion point.
    /// Handles line wrapping and terminal scrolling.
    ///
    /// Important: this deliberately never *reads* Console.CursorLeft/CursorTop.
    /// On Unix, reading the cursor position makes .NET emit an ESC[6n query and
    /// block until the terminal answers on stdin. Doing that per keystroke would
    /// be slow and would race with our own Console.ReadKey() calls. Instead the
    /// position is captured once by the caller and tracked by arithmetic here.
    /// </summary>
    private static int Render(StringBuilder buffer, int cursor, int startLeft, ref int startTop, int previousRenderLength)
    {
        var width = Console.BufferWidth;
        var height = Console.BufferHeight;
        if (width <= 0) return previousRenderLength;

        var text = buffer.ToString();

        // Pad with spaces to erase whatever the previous, longer render left behind.
        var padding = Math.Max(0, previousRenderLength - text.Length);
        var painted = text + new string(' ', padding);

        // How many rows does the painted region occupy, and would it run off the
        // bottom of the buffer? If so the terminal will scroll, which moves our
        // origin up by the overflow amount.
        var lastCell = startLeft + Math.Max(painted.Length - 1, 0);
        var rowsUsed = lastCell / width;
        var overflow = (startTop + rowsUsed) - (height - 1);
        if (overflow > 0)
        {
            startTop -= overflow;
            if (startTop < 0) startTop = 0;
        }

        Console.SetCursorPosition(startLeft, startTop);
        Console.Write(painted);

        var cursorCell = startLeft + cursor;
        var cursorTop = startTop + (cursorCell / width);
        var cursorLeft = cursorCell % width;

        if (cursorTop > height - 1) cursorTop = height - 1;
        Console.SetCursorPosition(cursorLeft, cursorTop);

        return text.Length;
    }

    private static int FindPreviousWordStart(StringBuilder buffer, int cursor)
    {
        var i = cursor;
        while (i > 0 && char.IsWhiteSpace(buffer[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(buffer[i - 1])) i--;
        return i;
    }

    private static int FindNextWordStart(StringBuilder buffer, int cursor)
    {
        var i = cursor;
        while (i < buffer.Length && !char.IsWhiteSpace(buffer[i])) i++;
        while (i < buffer.Length && char.IsWhiteSpace(buffer[i])) i++;
        return i;
    }

    private static void AddToHistory(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return;

        // Avoid consecutive duplicates, as shells do.
        if (_history.Count > 0 && _history[_history.Count - 1] == entry) return;

        _history.Add(entry);
        while (_history.Count > MaxHistoryEntries) _history.RemoveAt(0);
    }
}
