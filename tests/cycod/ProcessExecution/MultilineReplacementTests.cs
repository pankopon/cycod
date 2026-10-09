using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Tests.ProcessExecution;

[TestClass]
public class MultilineReplacementTests
{
    [TestMethod]
    public async Task Wrapper_MultilineLfInCrLfFile_PreviewsExecutesAndUndoes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "multiline undo " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "input.txt");
        var old = "first\nsecond";
        var replacement = "changed\nreplacement\nthird";
        var original = "prefix\r\nfirst\r\nsecond\r\nmiddle\r\nfirst\r\nsecond\r\nsuffix\r\n";
        File.WriteAllText(file, original);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "cycodmd", "cycodmd.csproj"))) root = root.Parent;
        Assert.IsNotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = OperatingSystem.IsWindows() ? "cycodmd.exe" : "cycodmd";
        var cli = Path.Combine(root.FullName, "src", "cycodmd", "bin", configuration, "net9.0", executable);
        Assert.IsTrue(File.Exists(cli), cli);
        var editor = new StrReplaceEditorHelperFunctions(new CycoDmdCliWrapper(cli));
        try
        {
            var preview = await editor.ReplaceAllInFiles(new[] { file }, old: old, @new: replacement);
            Assert.IsTrue(preview.Contains("Will replace 2 occurrence(s)"), preview);
            Assert.IsTrue(preview.Contains("third"), preview);
            Assert.AreEqual(original, File.ReadAllText(file));

            var result = await editor.ReplaceAllInFiles(new[] { file }, old: old, @new: replacement, preview: false);
            Assert.AreEqual(original.Replace("first\r\nsecond", replacement.Replace("\n", "\r\n")), File.ReadAllText(file), result);
            editor.UndoEdit(file);
            Assert.AreEqual(original, File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task Cli_MultilineCrLf_PreviewsAndExecutesWithoutMixedEndings()
    {
        await VerifyCliReplacement("\r\n", "first\r\nsecond", "changed\nreplacement\nthird");
    }

    [TestMethod]
    public async Task Cli_MultilineLf_PreviewsAndExecutesWithLfEndings()
    {
        await VerifyCliReplacement("\n", "first\nsecond", "changed\r\nreplacement");
    }

    [TestMethod]
    public void LiteralPattern_MatchesLfAndCrLfWithoutChangingOtherEscapes()
    {
        var method = typeof(StrReplaceEditorHelperFunctions).GetMethod("BuildLiteralSearchPattern", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        var old = "quoted \"C:\\path\\\" $1\nnext";
        var pattern = (string)method.Invoke(null, new object[] { old })!;
        Assert.IsTrue(Regex.IsMatch(old, pattern));
        Assert.IsTrue(Regex.IsMatch(old.Replace("\n", "\r\n"), pattern));
        Assert.IsFalse(Regex.IsMatch("quoted X $1\nnext", pattern));
        var crlfPattern = (string)method.Invoke(null, new object[] { old.Replace("\n", "\r\n") })!;
        Assert.IsTrue(Regex.IsMatch(old, crlfPattern));
    }

    private static async Task VerifyCliReplacement(string newline, string old, string replacement)
    {
        var directory = Path.Combine(Path.GetTempPath(), "multiline replacement " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "input.txt");
        var original = "prefix" + newline + old + newline + "middle" + newline + old + newline + "suffix" + newline;
        File.WriteAllText(file, original);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "cycodmd", "cycodmd.csproj"))) root = root.Parent;
        Assert.IsNotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(root.FullName, "src", "cycodmd", "bin", configuration, "net9.0", "cycodmd.dll");
        Assert.IsTrue(File.Exists(cli), cli);
        var args = new[] { cli, "find-files", file, "--contains", Regex.Escape(old), "--replace-with", replacement };
        try
        {
            var preview = await new RunnableProcessBuilder().WithFileName("dotnet").WithArgumentList(args).Build().RunAsync();
            Assert.AreEqual(0, preview.ExitCode, preview.StandardError);
            Assert.IsTrue(preview.StandardOutput.Contains("Will replace 2 occurrence(s)"), preview.StandardOutput);
            Assert.IsTrue(preview.StandardOutput.Contains("replacement"), preview.StandardOutput);
            Assert.AreEqual(original, File.ReadAllText(file));

            var result = await new RunnableProcessBuilder().WithFileName("dotnet").WithArgumentList(args.Append("--execute").ToArray()).Build().RunAsync();
            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            var expected = original.Replace(old, replacement.Replace("\r\n", "\n").Replace("\n", newline));
            Assert.AreEqual(expected, File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
