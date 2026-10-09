using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.ProcessExecution;

[TestClass]
public class ReplacementArgumentTests
{
    private static StrReplaceEditorHelperFunctions CreateEditor()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "cycodmd", "cycodmd.csproj"))) root = root.Parent;
        Assert.IsNotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = OperatingSystem.IsWindows() ? "cycodmd.exe" : "cycodmd";
        var cli = Path.Combine(root.FullName, "src", "cycodmd", "bin", configuration, "net9.0", executable);
        Assert.IsTrue(File.Exists(cli), cli);
        return new StrReplaceEditorHelperFunctions(new CycoDmdCliWrapper(cli));
    }

    [TestMethod]
    public async Task ReplaceAllInFiles_QuotedBackslashes_ArePreserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "replacement paths " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "paths.txt");
        var old = "path=\"C:\\folder with spaces\\input\\\"";
        var replacement = "path=\"D:\\another folder\\output\\\"";
        File.WriteAllText(file, old + Environment.NewLine);
        try
        {
            var editor = CreateEditor();
            var result = await editor.ReplaceAllInFiles(new[] { file }, old: old, @new: replacement, preview: false);
            Assert.AreEqual(replacement + Environment.NewLine, File.ReadAllText(file), result);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task WithArguments_AfterArgumentList_UsesRawArguments()
    {
        var process = new RunnableProcessBuilder()
            .WithFileName("dotnet")
            .WithArgumentList(new[] { "invalid-old-argument" })
            .WithArguments("--version")
            .Build();
        var result = await process.RunAsync();
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsNotNull(process.Process);
        Assert.AreEqual(0, process.Process.StartInfo.ArgumentList.Count);
        Assert.AreEqual("--version", process.Process.StartInfo.Arguments);
    }

    [TestMethod]
    public async Task ReplaceAllInFiles_QuotedLiteral_PreviewsExecutesAndUndoes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "replacement arguments " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "quoted statement.java");
        var old = "System.out.println(\"AuthToken:\" + r.getAuthorizationToken());";
        var replacement = "System.out.println(\"AuthToken:[REDACTED]\");";
        var original = string.Join(Environment.NewLine, Enumerable.Repeat(old, 3)) + Environment.NewLine;
        File.WriteAllText(file, original);
        var editor = CreateEditor();
        try
        {
            var preview = await editor.ReplaceAllInFiles(new[] { file }, old: old, @new: replacement);
            Assert.IsTrue(preview.Contains("Will replace 3 occurrence(s)"), preview);
            Assert.IsTrue(preview.Contains(replacement), preview);
            Assert.AreEqual(original, File.ReadAllText(file));

            var result = await editor.ReplaceAllInFiles(new[] { file }, fileContains: "\"AuthToken:\"", old: old, @new: replacement, preview: false);
            Assert.AreEqual(original.Replace(old, replacement), File.ReadAllText(file), result);
            editor.UndoEdit(file);
            Assert.AreEqual(original, File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
