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

        // The prompt has already been written by the caller; whatever column we
        // start at is where the editable region begins.
        var startLeft = Console.CursorLeft;
        var startTop = Console.CursorTop;
        var renderer = new LineRenderer(startLeft, startTop,
            () => Console.BufferWidth, () => Console.BufferHeight,
            Console.SetCursorPosition, Console.Write, Console.WriteLine);

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
                renderer.Finish(buffer);
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

            renderer.Render(buffer, cursor);
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
    private sealed class LineRenderer(
        int startLeft, int startTop, Func<int> getWidth, Func<int> getHeight,
        Action<int, int> setCursorPosition, Action<string> write, Action writeLine)
    {
        private int _startTop = startTop;
        private int _previousRenderLength;
        private int _viewOffset;

        public void Render(StringBuilder buffer, int cursor)
        {
            var width = getWidth();
            var height = getHeight();
            if (width <= 0 || height <= 0) return;

            var text = buffer.ToString();
            ScrollToMakeRoom(text.Length, width, height);

            // Reserve a cell after the repaint, even at an exact wrap boundary.
            // Never write the bottom-right cell and depend on delayed autowrap.
            var capacity = (height - _startTop) * width - startLeft - 1;
            UpdateViewport(text.Length, cursor, capacity);
            var visibleLength = Math.Min(text.Length - _viewOffset, capacity);
            var visible = text.Substring(_viewOffset, visibleLength);
            var padding = Math.Max(0, _previousRenderLength - visibleLength);

            setCursorPosition(startLeft, _startTop);
            write(visible + new string(' ', padding));
            _previousRenderLength = visibleLength;

            var cursorCell = startLeft + cursor - _viewOffset;
            setCursorPosition(cursorCell % width, _startTop + cursorCell / width);
        }

        public void Finish(StringBuilder buffer)
        {
            Render(buffer, buffer.Length);
            writeLine();
        }

        private void ScrollToMakeRoom(int textLength, int width, int height)
        {
            var endCell = startLeft + Math.Max(textLength, _previousRenderLength);
            var overflow = Math.Max(0, _startTop + endCell / width - (height - 1));
            var scroll = Math.Min(_startTop, overflow);
            if (scroll == 0) return;

            // Move the actual screen contents (including the prompt) first.
            setCursorPosition(0, height - 1);
            for (var i = 0; i < scroll; i++) writeLine();
            _startTop -= scroll;
        }

        private void UpdateViewport(int textLength, int cursor, int capacity)
        {
            // Once input outgrows the screen, keep a bounded slice visible rather
            // than clamping its cursor onto unrelated text. The prompt prefix
            // stays in place; only the editable region changes with the slice.
            _viewOffset = Math.Min(_viewOffset, Math.Max(0, textLength - capacity));
            if (cursor < _viewOffset) _viewOffset = cursor;
            if (cursor > _viewOffset + capacity) _viewOffset = cursor - capacity;
        }
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
