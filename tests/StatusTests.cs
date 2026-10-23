using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HgInlineDiff;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

internal interface IStatusTestHierarchy : IVsHierarchy, IVsSccProject2 { }

internal static class StatusTests
{
    public static int Run(string work, Action<string, string> hg)
    {
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("FAILED status: " + description);
            checks++;
        }

        var root = Path.Combine(work, "status-smoke-" + Guid.NewGuid().ToString("N"));
        var secondRoot = root + "-second";
        Directory.CreateDirectory(Path.Combine(root, "src", "nested"));
        Directory.CreateDirectory(secondRoot);
        hg(root, "init");
        hg(secondRoot, "init");
        foreach (var name in new[] { "outside.txt", "src/clean.txt", "src/nested/missing.txt", "src/removed.txt", "src/old.txt", "src/source.txt", "sample.slnx", "sample.csproj" })
            File.WriteAllText(Path.Combine(root, name), "original\n");
        File.WriteAllText(Path.Combine(root, ".hgignore"), "syntax: glob\nignored.tmp\n");
        File.WriteAllBytes(Path.Combine(root, "binary.bin"), new byte[] { 1, 0, 2 });
        File.WriteAllText(Path.Combine(secondRoot, "second.csproj"), "original\n");
        hg(root, "add");
        hg(root, "commit --user smoke-test --message initial --date \"0 0\"");
        hg(secondRoot, "add");
        hg(secondRoot, "commit --user smoke-test --message initial --date \"0 0\"");
        File.WriteAllText(Path.Combine(root, "outside.txt"), "modified\n");
        File.Delete(Path.Combine(root, "src/nested/missing.txt"));
        hg(root, "remove src/removed.txt");
        hg(root, "rename src/old.txt \"src/new [ä].txt\"");
        hg(root, "copy src/source.txt src/copy.txt");
        File.WriteAllText(Path.Combine(root, "src/added.txt"), "added\n");
        hg(root, "add src/added.txt");
        File.WriteAllText(Path.Combine(root, "untracked.txt"), "unknown\n");
        File.WriteAllText(Path.Combine(root, "ignored.tmp"), "ignored\n");
        File.WriteAllBytes(Path.Combine(root, "binary.bin"), new byte[] { 2, 0, 3 });

        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var context = new JoinableTaskContext();
            var tasks = context.Factory;
            var includeSecond = true;
            var gitOnly = false;
            var gitRoot = Path.Combine(work, "git-status-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(gitRoot, ".git"));
            var currentSolution = Path.Combine(root, "sample.slnx");
            var gitProvider = Guid.NewGuid();
            var selectedProvider = gitProvider;
            var advised = 0;
            var unadvised = 0;
            var glyphRefreshes = 0;
            var activations = 0;
            IVsRunningDocTableEvents? documentEvents = null;
            IVsHierarchy Project(string filename) => MarginUiTests.InterfaceProxy<IStatusTestHierarchy>.Create(call =>
            {
                switch (call.MethodName)
                {
                    case "GetCanonicalName": call.Args[1] = filename; break;
                    case "GetProperty": call.Args[2] = Path.GetDirectoryName(filename); break;
                    case "SccGlyphChanged": glyphRefreshes++; break;
                }
                return VSConstants.S_OK;
            });
            var solution = MarginUiTests.InterfaceProxy<IVsSolution>.Create(call =>
            {
                switch (call.MethodName)
                {
                    case "GetSolutionInfo":
                        call.Args[0] = Path.GetDirectoryName(currentSolution); call.Args[1] = currentSolution; call.Args[2] = string.Empty;
                        break;
                    case "GetProjectEnum":
                        var index = 0;
                        var projects = gitOnly ? new[] { Project(Path.Combine(gitRoot, "git.csproj")) }
                            : includeSecond ? new[] { Project(Path.Combine(root, "sample.csproj")), Project(Path.Combine(secondRoot, "second.csproj")) }
                            : new[] { Project(Path.Combine(root, "sample.csproj")) };
                        call.Args[2] = MarginUiTests.InterfaceProxy<IEnumHierarchies>.Create(next =>
                        {
                            if (next.MethodName != "Next") return VSConstants.E_NOTIMPL;
                            if (index == projects.Length) { next.Args[2] = 0u; return VSConstants.S_FALSE; }
                            ((IVsHierarchy[])next.Args[1])[0] = projects[index++]; next.Args[2] = 1u;
                            return VSConstants.S_OK;
                        });
                        break;
                    case "AdviseSolutionEvents": call.Args[1] = 1u; advised++; break;
                    case "UnadviseSolutionEvents": unadvised++; break;
                }
                return VSConstants.S_OK;
            });
            var documents = MarginUiTests.InterfaceProxy<IVsRunningDocumentTable>.Create(call =>
            {
                if (call.MethodName == "AdviseRunningDocTableEvents")
                { documentEvents = (IVsRunningDocTableEvents)call.Args[0]; call.Args[1] = 2u; advised++; }
                if (call.MethodName == "UnadviseRunningDocTableEvents") unadvised++;
                return VSConstants.S_OK;
            });
            using (var monitor = new MercurialStatusMonitor(solution, documents, tasks))
            {
                MercurialStatusService provider = null!;
                var registration = MarginUiTests.InterfaceProxy<IVsRegisterScciProvider>.Create(call =>
                {
                    selectedProvider = (Guid)call.Args[0];
                    if (selectedProvider == new Guid(MercurialStatusService.ProviderGuid))
                    {
                        activations++;
                        provider.SetActive();
                    }
                    else
                        provider.SetInactive();
                    return VSConstants.S_OK;
                });
                var selection = MarginUiTests.InterfaceProxy<IVsGetScciProviderInterface>.Create(call =>
                {
                    if (call.MethodName != "GetSourceControlProviderID") return VSConstants.E_NOTIMPL;
                    call.Args[0] = selectedProvider;
                    return null;
                });
                using (provider = new MercurialStatusService(monitor, registration, selection, gitProvider))
                {
                    tasks.Run(monitor.RefreshAsync);
                    Check(monitor.Repositories.Count == 2 && monitor.Error == string.Empty,
                        "discover solution and external project repositories: " + monitor.Repositories.Count + "; " + monitor.Error);
                    Check(activations == 1 && glyphRefreshes >= 2, "activate provider and notify all projects");
                    var status = monitor.Repositories.Single(repository => repository.Root == root);
                    var records = status.Changes.ToDictionary(change => change.Path, change => change.Status);
                    Check(records["outside.txt"] == "M" && records["binary.bin"] == "M", "include non-project and binary changes");
                    Check(records["src/added.txt"] == "A" && records["src/removed.txt"] == "R"
                        && records["src/nested/missing.txt"] == "!" && records["untracked.txt"] == "?", "all five disk statuses");
                    Check(records["src/old.txt"] == "R" && records["src/new [ä].txt"] == "A"
                        && records["src/copy.txt"] == "A", "rename and copy with Unicode and literal brackets");
                    Check(!records.ContainsKey("ignored.tmp") && !records.ContainsKey("src/clean.txt"), "exclude ignored and clean files from list");
                    Check(status.GetStatus(Path.Combine(root, "src/clean.txt")) == 'C'
                        && status.GetStatus(Path.Combine(root, "src/nested")) == 'M'
                        && status.GetStatus(root) == 'M', "clean files and recursively modified folders");
                    Check(monitor.GetStatus(root + "extra\\outside.txt") == '\0', "repository path boundary");
                    var paths = new[] { "src/clean.txt", "outside.txt", "src/added.txt", "src/removed.txt", "src/nested/missing.txt", "untracked.txt", "ignored.tmp" }
                        .Select(name => Path.Combine(root, name)).ToArray();
                    var glyphs = new VsStateIcon[paths.Length];
                    var flags = new uint[paths.Length];
                    provider.GetSccGlyph(paths.Length, paths, glyphs, flags);
                    Check(glyphs.SequenceEqual(new[]
                    {
                        VsStateIcon.STATEICON_CHECKEDIN,
                        VsStateIcon.STATEICON_CHECKEDOUT, VsStateIcon.STATEICON_CHECKEDOUT,
                        VsStateIcon.STATEICON_CHECKEDOUT, VsStateIcon.STATEICON_CHECKEDOUT,
                        VsStateIcon.STATEICON_ORPHANED, VsStateIcon.STATEICON_NOSTATEICON
                    }), "use standard Visual Studio glyphs for every item in a batched request");
                    Check(flags[0] == 1 && flags[1] == 3 && flags[5] == 0, "controlled, modified, and untracked shell flags");
                    foreach (var shellStatus in new[] { 0u, 1u, 3u })
                    {
                        provider.GetSccGlyphFromStatus(shellStatus, glyphs);
                        Check(glyphs[0] == (shellStatus == 0 ? VsStateIcon.STATEICON_NOSTATEICON
                            : shellStatus == 1 ? VsStateIcon.STATEICON_CHECKEDIN : VsStateIcon.STATEICON_CHECKEDOUT),
                            "standard glyph from shell status " + shellStatus);
                    }
                    selectedProvider = gitProvider;
                    provider.SetInactive();
                    monitor.RequestRefresh();
                    tasks.Run(monitor.RefreshAsync);
                    Check(activations == 1 && selectedProvider == gitProvider, "respect a manual provider change during refresh");
                    selectedProvider = new Guid(MercurialStatusService.ProviderGuid);
                    provider.SetActive();

                    // AsyncPackage construction requires devenv; exercise its COM adapters without creating shell state.
                    var package = (HgInlineDiffPackage)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(HgInlineDiffPackage));
                    GC.SuppressFinalize(package);
                    {
                        var serviceField = typeof(HgInlineDiffPackage).GetField("_statusService", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        serviceField.SetValue(package, provider);
                        try
                        {
                            ((IVsSccProvider)package).SetInactive();
                            ((IVsSccManager2)package).GetSccGlyph(paths.Length, paths, glyphs, flags);
                            Check(glyphs.All(icon => icon == VsStateIcon.STATEICON_NOSTATEICON), "package forwards provider deactivation");
                            ((IVsSccProvider)package).SetActive();
                            ((IVsSccManager2)package).GetSccGlyph(paths.Length, paths, glyphs, flags);
                            Check(glyphs[0] == VsStateIcon.STATEICON_CHECKEDIN && glyphs[1] == VsStateIcon.STATEICON_CHECKEDOUT,
                                "package exposes the same standard glyphs as the registered service");
                        }
                        finally
                        {
                            serviceField.SetValue(package, null);
                        }
                    }

                    using (var window = new ChangesToolWindow())
                    {
                        typeof(ChangesToolWindow).GetField("_monitor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, monitor);
                        var update = typeof(ChangesToolWindow).GetMethod("UpdateFiles", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        update.Invoke(window, null);
                        var grid = ((DockPanel)window.Content).Children.OfType<DataGrid>().Single();
                        Check(window.Caption == "HgInlineDiff: Changes" && grid.Columns.Count == 3
                            && grid.Columns[2].Visibility == Visibility.Visible, "window caption and repository column");
                        var rows = grid.Items.Cast<MercurialFileChange>().ToArray();
                        var refresh = ((DockPanel)window.Content).Children.OfType<Button>().Single();
                        refresh.Measure(new Size(500, 100));
                        Check(refresh.DesiredSize.Width <= 40, "compact refresh button overrides themed minimum width");
                        grid.Measure(new Size(500, 300));
                        grid.Arrange(new Rect(0, 0, 500, 300));
                        grid.UpdateLayout();
                        Check(grid.Columns[0].GetCellContent(rows[0]) is TextBlock cellText && cellText.Margin == new Thickness(3),
                            "table text has spacing on every side");
                        Check(rows.Select(row => row.Path).SequenceEqual(rows.Select(row => row.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)), "path sorting");
                        grid.SelectedItem = rows.First(row => row.Path == "outside.txt");
                        update.Invoke(window, null);
                        Check(((MercurialFileChange)grid.SelectedItem).Path == "outside.txt", "retain selection through refresh without opening another document");

                        File.WriteAllText(Path.Combine(root, "src/clean.txt"), "saved\n");
                        provider.GetSccGlyph(1, paths, glyphs, flags);
                        Check(glyphs[0] == VsStateIcon.STATEICON_CHECKEDIN, "glyph requests use cached disk status without running a refresh");
                        PumpUntil(() => monitor.GetStatus(Path.Combine(root, "src/clean.txt")) == 'M');
                        Check(true, "file watcher refreshes without a manual command");
                        File.WriteAllText(Path.Combine(root, "outside.txt"), "original\n");
                        documentEvents!.OnAfterSave(1);
                        tasks.Run(monitor.RefreshAsync);
                        Check(monitor.GetStatus(Path.Combine(root, "outside.txt")) == 'C', "saved content refresh");
                        hg(root, "commit --user smoke-test --message changes --date \"1 0\"");
                        typeof(Application).GetMethod("OnActivated", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(application, new object[] { EventArgs.Empty });
                        tasks.Run(monitor.RefreshAsync);
                        Check(!monitor.Repositories.Single(repository => repository.Root == root).Changes.Any(change => change.Status == "M"), "refresh parent state after external commit and activation");

                        Directory.Move(Path.Combine(root, ".hg"), Path.Combine(root, ".hg-hidden"));
                        try
                        {
                            monitor.RequestRefresh();
                            tasks.Run(monitor.RefreshAsync);
                            Check(monitor.Error.Length != 0 && monitor.GetStatus(Path.Combine(root, "outside.txt")) == 'C', "failed refresh preserves last successful snapshot and reports error");
                        }
                        finally { Directory.Move(Path.Combine(root, ".hg-hidden"), Path.Combine(root, ".hg")); }

                        includeSecond = false;
                        monitor.RequestRefresh(true);
                        tasks.Run(monitor.RefreshAsync);
                        update.Invoke(window, null);
                        Check(monitor.Error.Length == 0 && monitor.Repositories.Count == 1
                            && grid.Columns[2].Visibility == Visibility.Collapsed, "rediscover repositories and recover from an error");
                        gitOnly = true;
                        currentSolution = Path.Combine(gitRoot, "git.slnx");
                        monitor.RequestRefresh(true);
                        tasks.Run(monitor.RefreshAsync);
                        Check(monitor.IsGitSolution && monitor.Repositories.Count == 0 && selectedProvider == gitProvider,
                            "switch from a Mercurial solution to Git without retaining the Mercurial provider");
                        monitor.OnAfterCloseSolution(null!);
                        selectedProvider = new Guid(MercurialStatusService.ProviderGuid);
                        provider.SetActive();
                        monitor.RequestRefresh(true);
                        tasks.Run(monitor.RefreshAsync);
                        Check(selectedProvider == gitProvider, "restore Git when VS reopens a Git solution with a persisted Mercurial provider");
                        monitor.RequestRefresh();
                        var pending = tasks.RunAsync(monitor.RefreshAsync);
                        monitor.OnAfterCloseSolution(null!);
                        pending.Join();
                        Check(monitor.Repositories.Count == 0 && monitor.Error.Length == 0, "solution close cancels pending scan and rejects stale results");
                    }
                }
            }
            Check(advised == 2 && unadvised == 2, "unsubscribe all shell events on disposal");

            try
            {
                MercurialStatus.Parse(root, Encoding.UTF8.GetBytes("? ../escape.txt\0"));
                throw new Exception("Expected invalid status path rejection.");
            }
            catch (InvalidOperationException) { Check(true, "reject paths outside repository"); }
            var clean = MercurialStatus.Parse(root, Encoding.UTF8.GetBytes("C src/clean.txt\0"));
            Check(clean.GetStatus(Path.Combine(root, "src")) == 'C' && clean.Changes.Count == 0, "clean folder aggregation");
            var nestedGit = Path.Combine(root, "nested-git");
            Directory.CreateDirectory(Path.Combine(nestedGit, ".git"));
            Check(MercurialRepository.FindRoot(Path.Combine(nestedGit, "file.cs")) == null
                && MercurialRepository.FindRoot(Path.Combine(nestedGit, "file.cs"), ".git") == nestedGit,
                "a nested Git repository hides the parent Mercurial repository");
            var gitWorktree = Path.Combine(root, "git-worktree");
            Directory.CreateDirectory(gitWorktree);
            File.WriteAllText(Path.Combine(gitWorktree, ".git"), "gitdir: ../nested-git/.git/worktrees/test\n");
            Check(MercurialRepository.FindRoot(Path.Combine(gitWorktree, "file.cs")) == null
                && MercurialRepository.FindRoot(Path.Combine(gitWorktree, "file.cs"), ".git") == gitWorktree,
                "recognize a Git worktree marker file as a repository boundary");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        Console.WriteLine("Status fixture: " + root);
        return checks;
    }

    private static void PumpUntil(Func<bool> done)
    {
        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (sender, args) =>
        {
            if (done() || elapsed.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!done()) throw new Exception("Automatic status refresh timed out.");
    }
}
