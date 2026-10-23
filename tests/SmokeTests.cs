using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HgInlineDiff;
using Microsoft.VisualStudio.Text.Differencing;

internal static class SmokeTests
{
    private static string _visualStudio = string.Empty;
    private static string _hg = string.Empty;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            _visualStudio = args[0];
            _hg = args[1];
            AppDomain.CurrentDomain.AssemblyResolve += ResolveVisualStudioAssembly;
            InitializeShellThread();
            _checks += StatusTests.Run(args[2], Hg);
            _checks += MarginUiTests.Run();
            RunAsync(args[2]).GetAwaiter().GetResult();
            Console.WriteLine("Passed " + _checks + " checks with " + Path.GetFileName(_visualStudio) + " editor assemblies.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializeShellThread()
    {
        // Resolve VS assemblies before the JIT loads this method; the test STA substitutes for devenv's UI thread.
        typeof(Microsoft.VisualStudio.Shell.ThreadHelper).GetMethod("SetUIThread",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, null);
    }

    private static Assembly? ResolveVisualStudioAssembly(object? sender, ResolveEventArgs args)
    {
        var filename = new AssemblyName(args.Name).Name + ".dll";
        var ide = Path.Combine(_visualStudio, "Common7", "IDE");
        foreach (var directory in new[] { Path.Combine(ide, "CommonExtensions", "Microsoft", "Editor"),
            Path.Combine(ide, "PublicAssemblies"), Path.Combine(ide, "PrivateAssemblies"), ide })
        {
            var path = Path.Combine(directory, filename);
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
        }
        var fallback = Directory.EnumerateFiles(ide, filename, SearchOption.AllDirectories).FirstOrDefault();
        return fallback == null ? null : Assembly.LoadFrom(fallback);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunAsync(string work)
    {
        // Test the same real VS differencing service used by the extension, without starting the IDE.
        // Reflection is test-only because the implementation is internal; production imports the public service.
        var assembly = Assembly.LoadFrom(Path.Combine(_visualStudio, "Common7", "IDE", "CommonExtensions",
            "Microsoft", "Editor", "Microsoft.VisualStudio.Platform.VSEditor.dll"));
        var type = assembly.GetType("Microsoft.VisualStudio.Text.Differencing.Implementation.DefaultTextDifferencingService", throwOnError: true)!;
        var service = (ITextDifferencingService)Activator.CreateInstance(type, nonPublic: true)!;
        var token = CancellationToken.None;

        _checks += await DiffUpdateTests.RunAsync(service);
        _checks += await PopupHighlightingTests.RunAsync();

        Check(LineDiff.Calculate(service, "same\n", "same\n", token).Count == 0, "unchanged file");
        Check(LineDiff.Calculate(service, "a\r\nb\r\n", "a\nb\n", token).Count == 0, "line endings only");
        var changed = LineDiff.Calculate(service, "a\nb\nc\n", "a\nchanged\nc\n", token).Single();
        Check(changed.StartLine == 1 && changed.LineCount == 1 && !changed.IsAddition && !changed.IsDeletion, "modified line coordinates");
        Check(changed.OriginalText == "b\n", "modified original text");
        var repeatedParent = "header\r\nold\r\nkeep\r\nold";
        var repeated = LineDiff.Calculate(service, repeatedParent, "header\nnew\nkeep\n", token);
        Check(repeated.Count == 2 && repeated[0].OriginalStart == 7 && repeated[1].OriginalStart == 16
            && repeated.All(hunk => repeatedParent.Replace("\r\n", "\n").Substring(hunk.OriginalStart, hunk.OriginalText.Length) == hunk.OriginalText),
            "parent offsets identify repeated fragments after CRLF normalization, including deletion at EOF");
        var added = LineDiff.Calculate(service, "a\nc\n", "a\nb\nc\n", token).Single();
        Check(added.StartLine == 1 && added.LineCount == 1 && added.IsAddition, "added line coordinates");
        var deleted = LineDiff.Calculate(service, "a\nb\nc\n", "a\nc\n", token).Single();
        Check(deleted.StartLine == 1 && deleted.IsDeletion && deleted.OriginalText == "b\n", "deleted line coordinates");
        deleted = LineDiff.Calculate(service, "a\nb", "a\n", token).Single();
        Check(deleted.IsDeletion && deleted.StartLine == 1 && deleted.OriginalText == "b", "deletion at EOF: start=" + deleted.StartLine + ", count=" + deleted.LineCount + ", old=" + deleted.OriginalText.Replace("\n", "\\n"));
        deleted = LineDiff.Calculate(service, "a", string.Empty, token).Single();
        Check(deleted.IsDeletion && deleted.StartLine == 0 && deleted.OriginalText == "a", "delete whole file contents");
        added = LineDiff.Calculate(service, string.Empty, "new\ntext\n", token).Single();
        Check(added.IsAddition && added.StartLine == 0 && added.LineCount == 2, "new file contents");
        Check(LineDiff.Calculate(service, " a\n", "a\n", token).Count != 0, "whitespace is preserved");
        Check(LineDiff.Calculate(service, string.Empty, string.Empty, token).Count == 0, "empty file");
        added = LineDiff.Calculate(service, "a\n", "a\n\n", token).Single();
        Check(added.IsAddition && added.StartLine == 1 && added.LineCount == 1, "append empty line");
        Check(LineDiff.Calculate(service, "a\nb\nc\nd\n", "A\nb\nc\nD\n", token).Count == 2, "separate hunks");
        changed = LineDiff.Calculate(service, "привет\n", "hello\n", token).Single();
        Check(changed.OriginalText == "привет\n", "Unicode original text");

        var root = Path.Combine(work, "mercurial-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Hg(root, "init");
        var filename = "space [ä].txt";
        var path = Path.Combine(root, filename);
        var utf8 = new UTF8Encoding(false);
        const string original = "alpha\nbeta\ngamma\n";
        File.WriteAllText(path, original, utf8);
        File.WriteAllText(Path.Combine(root, "utf16.txt"), "Привет\r\n", Encoding.Unicode);
        File.WriteAllBytes(Path.Combine(root, "binary.bin"), new byte[] { 65, 0, 66 });
        Hg(root, "add");
        Hg(root, "commit --user smoke-test --message initial --date \"0 0\"");
        Check(MercurialRepository.FindRoot(path) == root, "find repository root");
        var baseline = await MercurialRepository.LoadAsync(path, utf8, token);
        Check(baseline != null && baseline.Text == original && baseline.Revision.Length == 12, "read tracked parent with spaces, accents, and literal brackets in path");
        File.WriteAllText(path, "committed change\n", utf8);
        Check((await MercurialRepository.LoadAsync(path, utf8, token))?.Text == original, "disk edits do not change the baseline");
        Hg(root, "commit --user smoke-test --message second --date \"1 0\"");
        Check((await MercurialRepository.LoadAsync(path, utf8, token))?.Text == "committed change\n", "manual refresh reads new parent after commit");
        Hg(root, "update --rev 0");
        baseline = await MercurialRepository.LoadAsync(path, utf8, token);
        Check(baseline?.Text == original, "compare with working parent instead of repository tip");
        Check((await MercurialRepository.LoadAsync(Path.Combine(root, "utf16.txt"), Encoding.Unicode, token))?.Text == "Привет\r\n", "UTF-16 BOM decoding");
        Check(await MercurialRepository.LoadAsync(Path.Combine(root, "binary.bin"), utf8, token) == null, "binary file is skipped");

        var newPath = Path.Combine(root, "new.txt");
        File.WriteAllText(newPath, "new contents\n", utf8);
        Check(await MercurialRepository.LoadAsync(newPath, utf8, token) == null, "untracked file is skipped");
        Hg(root, "add new.txt");
        Check((await MercurialRepository.LoadAsync(newPath, utf8, token))?.Text == string.Empty, "added file uses empty baseline");
        Hg(root, "rename \"" + filename + "\" renamed.txt");
        Check((await MercurialRepository.LoadAsync(Path.Combine(root, "renamed.txt"), utf8, token))?.Text == original, "rename uses copy source in parent");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await MercurialRepository.LoadAsync(newPath, utf8, cancelled.Token);
                throw new Exception("Expected cancellation.");
            }
            catch (OperationCanceledException)
            {
                Check(true, "cancel repository load");
            }
        }
        Check(LineDiff.Calculate(service, baseline!.Text, "unsaved edit\n", token).Count != 0,
            "compare cached baseline with unsaved buffer text");
        Console.WriteLine("Mercurial fixture: " + root);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception("FAILED: " + name);
        _checks++;
    }

    private static void Hg(string root, string arguments)
    {
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo(_hg, "--noninteractive " + arguments)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            process.StartInfo.EnvironmentVariables["HGPLAIN"] = "1";
            process.StartInfo.EnvironmentVariables["HGENCODING"] = "utf-8";
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("hg test setup timed out.");
            }
            if (process.ExitCode != 0)
                throw new Exception("hg " + arguments + ": " + stderr.GetAwaiter().GetResult());
            stdout.GetAwaiter().GetResult();
        }
    }
}
