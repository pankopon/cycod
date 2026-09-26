using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ConsoleLineEditorTests
{
    [TestMethod]
    public void ViewportBoundariesPreserveTextAcrossTerminalSizes()
    {
        foreach (var width in new[] { 3, 4, 10 })
        foreach (var height in new[] { 1, 2, 4 })
        {
            var terminal = new Terminal(width, height);
            var editor = Start(terminal, 0);
            var capacity = width * height - 3;
            var text = "";
            for (var length = 0; length <= width * height * 3; length++)
            {
                editor.Render(text, text.Length);
                var visible = text.Length <= capacity ? text : text[^capacity..];
                Assert.AreEqual("> " + visible.PadRight(capacity + 1), terminal.Screen());
                text += (char)('a' + length % 26);
            }

            for (var cursor = text.Length; cursor >= 0; cursor--)
            {
                editor.Render(text, cursor);
                var physicalCursor = terminal.Cursor.Top * width + terminal.Cursor.Left - 2;
                var offset = cursor - physicalCursor;
                Assert.IsTrue(offset >= 0 && offset <= cursor);
                var visible = text.Substring(offset, Math.Min(capacity, text.Length - offset));
                Assert.AreEqual("> " + visible.PadRight(capacity + 1), terminal.Screen());
            }

            editor.Render("", 0);
            Assert.AreEqual("> " + new string(' ', capacity + 1), terminal.Screen());
            Assert.AreEqual(0, terminal.Scrolls, "A viewport repaint must never cause implicit scrolling.");
        }
    }


    [TestMethod]
    public void SimulatorDelaysWrapAndScrollsToBlankRow()
    {
        var terminal = new Terminal(4, 2);
        terminal.Move(0, 1);
        terminal.Write("abcd");
        Assert.AreEqual(0, terminal.Scrolls);
        terminal.Write("e");
        Assert.AreEqual(1, terminal.Scrolls);
        Assert.AreEqual("abcd", terminal.Row(0));
        Assert.AreEqual("e   ", terminal.Row(1));
        terminal.Write("fgh");
        terminal.Move(0, 1);
        terminal.Write("x");
        Assert.AreEqual(1, terminal.Scrolls, "Positioning must cancel pending wrap.");
        Assert.AreEqual("xfgh", terminal.Row(1));
        terminal.NewLine();
        Assert.AreEqual(2, terminal.Scrolls);
        Assert.AreEqual("    ", terminal.Row(1));
    }

    [TestMethod]
    public void BottomWrapDoesNotRepeatPreviousLine()
    {
        var terminal = new Terminal(10, 3);
        var editor = Start(terminal, 2);
        editor.Render("abcdefg", 7);
        editor.Render("abcdefgh", 8);
        Assert.AreEqual(1, terminal.Scrolls);
        Assert.AreEqual("> abcdefgh", terminal.Row(1));
        Assert.AreEqual("          ", terminal.Row(2));
        Assert.AreEqual((0, 2), terminal.Cursor);
        editor.Render("abcdefghi", 9);
        Assert.AreEqual("i         ", terminal.Row(2));
        Assert.AreEqual(1, terminal.Scrolls);
    }

    [TestMethod]
    public void RepeatedWrapMovesPromptAndPreservesEarlierOutput()
    {
        var terminal = new Terminal(10, 5);
        terminal.Write("old output");
        var editor = Start(terminal, 3);
        var text = "abcdefghijklmnopqrstuvwxyz";
        for (var length = 1; length <= text.Length; length++)
            editor.Render(text[..length], length);
        Assert.AreEqual(1, terminal.Scrolls);
        Assert.AreEqual("> abcdefgh", terminal.Row(2));
        Assert.AreEqual("ijklmnopqr", terminal.Row(3));
        Assert.AreEqual("stuvwxyz  ", terminal.Row(4));
        editor.Render(text, 0);
        editor.Render(text, text.Length);
        Assert.AreEqual(1, terminal.Scrolls, "Cursor movement must not scroll repeatedly.");
    }

    [TestMethod]
    public void WrapAboveBottomDoesNotScrollOrErasePrompt()
    {
        var terminal = new Terminal(10, 5);
        terminal.Write("old output");
        var editor = Start(terminal, 1);
        editor.Render("abcdefghi", 9);
        Assert.AreEqual(0, terminal.Scrolls);
        Assert.AreEqual("old output", terminal.Row(0));
        Assert.AreEqual("> abcdefgh", terminal.Row(1));
        Assert.AreEqual("i         ", terminal.Row(2));
    }

    [TestMethod]
    public void ShorterHistoryAndDeletionEraseOldWrappedText()
    {
        var terminal = new Terminal(10, 3);
        var editor = Start(terminal, 2);
        editor.Render("abcdefghijklmnopqrs", 19);
        editor.Render("abc", 3);
        Assert.AreEqual("> abc     ", terminal.Row(0));
        Assert.AreEqual("          ", terminal.Row(1));
        Assert.AreEqual("          ", terminal.Row(2));
        editor.Render("", 0);
        Assert.AreEqual(">         ", terminal.Row(0));
        Assert.AreEqual((2, 0), terminal.Cursor);
    }

    [TestMethod]
    public void OversizedInputKeepsCursorVisibleAndCanReturnHome()
    {
        var terminal = new Terminal(10, 3);
        var editor = Start(terminal, 2);
        var text = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        editor.Render(text, text.Length);
        Assert.AreEqual(2, terminal.Scrolls);
        Assert.AreEqual("> ", terminal.Row(0)[..2]);
        Assert.AreEqual(text[^27..], terminal.Screen()[2..29]);
        Assert.AreEqual((9, 2), terminal.Cursor);
        editor.Render(text, 0);
        Assert.AreEqual(text[..27], terminal.Screen()[2..29]);
        Assert.AreEqual((2, 0), terminal.Cursor);
        editor.Render(text, 31);
        Assert.IsTrue(terminal.Cursor.Left >= 0 && terminal.Cursor.Left < 10);
        editor.Render(text, text.Length);
        Assert.AreEqual(text[^27..], terminal.Screen()[2..29]);
        Assert.AreEqual(2, terminal.Scrolls);
        editor.Render("short", 5);
        Assert.AreEqual("> short   ", terminal.Row(0));
        Assert.AreEqual("          ", terminal.Row(1));
        Assert.AreEqual("          ", terminal.Row(2));
    }

    [TestMethod]
    public void SubmitFromMiddleMovesAfterEntireInput()
    {
        var terminal = new Terminal(10, 5);
        var editor = Start(terminal, 1);
        const string text = "abcdefghijklmnopqrs";
        editor.Render(text, 2);
        editor.Finish(text);
        Assert.AreEqual((0, 4), terminal.Cursor);
        Assert.AreEqual("s         ", terminal.Row(3));
    }

    [TestMethod]
    public void OversizedSubmitFromHomeShowsEndBeforeNewline()
    {
        var terminal = new Terminal(10, 3);
        var editor = Start(terminal, 2);
        const string text = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        editor.Render(text, 0);
        editor.Finish(text);
        Assert.AreEqual((0, 2), terminal.Cursor);
        Assert.AreEqual(text[^9..] + " ", terminal.Row(1));
        Assert.AreEqual("          ", terminal.Row(2));
    }

    [TestMethod]
    public void OneRowTerminalKeepsPromptAndCursorVisible()
    {
        var terminal = new Terminal(10, 1);
        var editor = Start(terminal, 0);
        editor.Render("abcdefghijklmnop", 16);
        Assert.AreEqual("> jklmnop ", terminal.Row(0));
        Assert.AreEqual((9, 0), terminal.Cursor);
        editor.Render("abcdefghijklmnop", 0);
        Assert.AreEqual("> abcdefg ", terminal.Row(0));
        Assert.AreEqual(0, terminal.Scrolls);
    }

    private static Editor Start(Terminal terminal, int top)
    {
        terminal.Move(0, top);
        terminal.Write("> ");
        return new Editor(terminal, 2, top);
    }

    private sealed class Editor
    {
        private readonly object _renderer;
        private readonly MethodInfo _render;
        private readonly MethodInfo _finish;

        public Editor(Terminal terminal, int left, int top)
        {
            var type = typeof(ConsoleLineEditor).GetNestedType("LineRenderer", BindingFlags.NonPublic)!;
            _renderer = Activator.CreateInstance(type,
                [left, top, (Func<int>)(() => terminal.Width), (Func<int>)(() => terminal.Height),
                 (Action<int, int>)terminal.Move, (Action<string>)terminal.Write, (Action)terminal.NewLine])!;
            _render = type.GetMethod("Render")!;
            _finish = type.GetMethod("Finish")!;
        }

        public void Render(string text, int cursor) => _render.Invoke(_renderer, [new StringBuilder(text), cursor]);
        public void Finish(string text) => _finish.Invoke(_renderer, [new StringBuilder(text)]);
    }

    private sealed class Terminal(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Scrolls { get; private set; }
        public (int Left, int Top) Cursor => (_left, _top);
        private readonly List<char[]> _rows = Enumerable.Range(0, height).Select(_ => new string(' ', width).ToCharArray()).ToList();
        private int _left;
        private int _top;
        private bool _wrapPending;

        public string Row(int row) => new(_rows[row]);
        public string Screen() => string.Concat(_rows.Select(row => new string(row)));

        public void Move(int left, int top)
        {
            Assert.IsTrue(left >= 0 && left < Width && top >= 0 && top < Height, $"Invalid cursor: {left},{top}");
            _left = left;
            _top = top;
            _wrapPending = false;
        }

        public void Write(string text)
        {
            foreach (var ch in text)
            {
                if (_wrapPending) NewLine();
                _rows[_top][_left] = ch;
                if (_left == Width - 1) _wrapPending = true;
                else _left++;
            }
        }

        public void NewLine()
        {
            _wrapPending = false;
            _left = 0;
            if (_top < Height - 1) _top++;
            else
            {
                _rows.RemoveAt(0);
                _rows.Add(new string(' ', Width).ToCharArray());
                Scrolls++;
            }
        }
    }
}
