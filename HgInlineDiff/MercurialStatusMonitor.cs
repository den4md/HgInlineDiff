using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace HgInlineDiff;

internal sealed class MercurialStatusMonitor : IDisposable, IVsSolutionEvents, IVsRunningDocTableEvents
{
    private readonly IVsSolution _solution;
    private readonly IVsRunningDocumentTable _documents;
    private readonly Application? _application;
    private readonly JoinableTaskFactory _tasks;
    private readonly DispatcherTimer _timer;
    private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
    private readonly uint _solutionCookie;
    private readonly uint _documentCookie;
    private CancellationTokenSource? _scan;
    private JoinableTask? _refreshTask;
    private string[] _roots = Array.Empty<string>();
    private int _requested;
    private int _discover;
    private int _generation;
    private long _lastRequest;
    private int _resetWatchers;
    private string _watcherError = string.Empty;
    private bool _scanning;
    private bool _disposed;

    public IReadOnlyList<MercurialStatus> Repositories { get; private set; } = Array.Empty<MercurialStatus>();
    public string Error { get; private set; } = string.Empty;
    public event EventHandler? Changed;

    public MercurialStatusMonitor(IVsSolution solution, IVsRunningDocumentTable documents, JoinableTaskFactory tasks)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _solution = solution;
        _documents = documents;
        _tasks = tasks;
        _application = Application.Current;
        ErrorHandler.ThrowOnFailure(solution.AdviseSolutionEvents(this, out _solutionCookie));
        ErrorHandler.ThrowOnFailure(documents.AdviseRunningDocTableEvents(this, out _documentCookie));
        if (_application != null)
            _application.Activated += OnApplicationActivated;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnTick;
        _timer.Start();
        RequestRefresh(discover: true);
    }

    public void RequestRefresh(bool discover = false)
    {
        Interlocked.Exchange(ref _lastRequest, Stopwatch.GetTimestamp());
        if (discover)
            Interlocked.Exchange(ref _discover, 1);
        Interlocked.Exchange(ref _requested, 1);
    }

    public char GetStatus(string path)
    {
        path = path.Replace('/', '\\');
        foreach (var repository in Repositories)
        {
            if (path.Equals(repository.Root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(repository.Root + "\\", StringComparison.OrdinalIgnoreCase))
                return repository.GetStatus(path);
        }
        return '\0';
    }

    internal IEnumerable<IVsHierarchy> Projects()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var projectType = Guid.Empty;
        if (ErrorHandler.Failed(_solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION,
            ref projectType, out var projects)))
            yield break;
        var item = new IVsHierarchy[1];
        while (projects.Next(1, item, out var fetched) == VSConstants.S_OK && fetched == 1)
            yield return item[0];
    }

    private void OnTick(object? sender, EventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed || _scanning || Volatile.Read(ref _requested) == 0
            || (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastRequest)) / (double)Stopwatch.Frequency < 0.5)
            return;
        _refreshTask = _tasks.RunAsync(RefreshAsync);
    }

    internal async Task RefreshAsync()
    {
        await _tasks.SwitchToMainThreadAsync();
        if (_disposed || _scanning || Interlocked.Exchange(ref _requested, 0) == 0)
            return;
        _scanning = true;
        var generation = _generation;
        var request = new CancellationTokenSource();
        _scan = request;
        try
        {
            if (Interlocked.Exchange(ref _discover, 0) != 0)
            {
                var candidates = new List<string>();
                if (ErrorHandler.Succeeded(_solution.GetSolutionInfo(out _, out var solutionFile, out _))
                    && !string.IsNullOrEmpty(solutionFile))
                    candidates.Add(solutionFile);
                foreach (var project in Projects())
                {
                    if (ErrorHandler.Succeeded(project.GetCanonicalName(VSConstants.VSITEMID_ROOT, out var path))
                        && !string.IsNullOrEmpty(path))
                        candidates.Add(path);
                    if (ErrorHandler.Succeeded(project.GetProperty(VSConstants.VSITEMID_ROOT,
                        (int)__VSHPROPID.VSHPROPID_ProjectDir, out var directory)) && directory is string folder)
                        candidates.Add(Path.Combine(folder, "__repository_probe__"));
                }
                var roots = await Task.Run(() => candidates.Select(MercurialRepository.FindRoot)
                    .Where(root => root != null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(root => root.Length).ToArray(), request.Token);
                await _tasks.SwitchToMainThreadAsync(request.Token);
                if (_disposed || generation != _generation)
                    return;
                if (!_roots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase)
                    || _watchers.Count != roots.Length || Interlocked.Exchange(ref _resetWatchers, 0) != 0)
                {
                    _roots = roots;
                    ReplaceWatchers();
                }
            }

            var rootsToScan = _roots;
            var previous = Repositories;
            var result = await Task.Run(async () =>
            {
                var statuses = new List<MercurialStatus>();
                var errors = new List<string>();
                foreach (var root in rootsToScan)
                {
                    try { statuses.Add(await MercurialRepository.LoadStatusAsync(root, request.Token).ConfigureAwait(false)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                        || error is InvalidOperationException || error is TimeoutException || error is ArgumentException)
                    {
                        errors.Add(root + ": " + error.Message);
                        var cached = previous.FirstOrDefault(status => status.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
                        if (cached != null)
                            statuses.Add(cached);
                    }
                }
                return (Statuses: statuses.ToArray(), Error: string.Join(Environment.NewLine, errors));
            }, request.Token);
            await _tasks.SwitchToMainThreadAsync(request.Token);
            if (_disposed || generation != _generation)
                return;
            Repositories = result.Statuses;
            Error = result.Error.Length == 0 ? _watcherError
                : "Refresh failed; previous results may be out of date. " + result.Error;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _tasks.SwitchToMainThreadAsync();
            if (!_disposed && generation == _generation)
            {
                ActivityLog.LogError(nameof(HgInlineDiff), error.ToString());
                Error = "Refresh failed: " + error.Message;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            await _tasks.SwitchToMainThreadAsync();
            _scan = null;
            request.Dispose();
            _scanning = false;
        }
    }

    private void ReplaceWatchers()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
        var errors = new List<string>();
        foreach (var root in _roots)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                watcher.Renamed += (sender, args) =>
                {
                    OnFileChanged(sender, args);
                    OnFileChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.Renamed,
                        ((FileSystemWatcher)sender).Path, args.OldName));
                };
                watcher.Error += (sender, args) =>
                {
                    Interlocked.Exchange(ref _resetWatchers, 1);
                    RequestRefresh(discover: true);
                };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException)
            {
                watcher?.Dispose();
                errors.Add("File watcher unavailable for " + root + ": " + error.Message);
            }
        }
        _watcherError = errors.Count == 0 ? string.Empty
            : string.Join(Environment.NewLine, errors) + " Use Refresh or return focus to Visual Studio to update.";
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        var name = (args.Name ?? string.Empty).Replace('/', '\\');
        if (name.StartsWith(".hg\\", StringComparison.OrdinalIgnoreCase)
            && !name.Equals(".hg\\dirstate", StringComparison.OrdinalIgnoreCase)
            && !name.Equals(".hg\\branch", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith(".hg\\merge\\", StringComparison.OrdinalIgnoreCase))
            return;
        RequestRefresh();
    }

    private void Clear()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _generation++;
        _scan?.Cancel();
        _roots = Array.Empty<string>();
        ReplaceWatchers();
        Repositories = Array.Empty<MercurialStatus>();
        Error = string.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _scan?.Cancel();
        _refreshTask?.Join();
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
        _solution.UnadviseSolutionEvents(_solutionCookie);
        _documents.UnadviseRunningDocTableEvents(_documentCookie);
        if (_application != null)
            _application.Activated -= OnApplicationActivated;
    }

    private void OnApplicationActivated(object? sender, EventArgs args) => RequestRefresh(discover: true);
    public int OnAfterSave(uint docCookie) { RequestRefresh(); return VSConstants.S_OK; }
    public int OnAfterOpenProject(IVsHierarchy hierarchy, int added) { RequestRefresh(true); return VSConstants.S_OK; }
    public int OnAfterLoadProject(IVsHierarchy stub, IVsHierarchy real) { RequestRefresh(true); return VSConstants.S_OK; }
    public int OnAfterOpenSolution(object reserved, int newSolution) { RequestRefresh(true); return VSConstants.S_OK; }
    public int OnAfterCloseSolution(object reserved)
    { ThreadHelper.ThrowIfNotOnUIThread(); Clear(); return VSConstants.S_OK; }
    public int OnBeforeCloseProject(IVsHierarchy hierarchy, int removed) { RequestRefresh(true); return VSConstants.S_OK; }
    public int OnBeforeUnloadProject(IVsHierarchy real, IVsHierarchy stub) { RequestRefresh(true); return VSConstants.S_OK; }
    public int OnQueryCloseProject(IVsHierarchy hierarchy, int removed, ref int cancel) => VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy hierarchy, ref int cancel) => VSConstants.S_OK;
    public int OnQueryCloseSolution(object reserved, ref int cancel) => VSConstants.S_OK;
    public int OnBeforeCloseSolution(object reserved) => VSConstants.S_OK;
    public int OnAfterFirstDocumentLock(uint cookie, uint lockType, uint readLocks, uint editLocks) => VSConstants.S_OK;
    public int OnBeforeLastDocumentUnlock(uint cookie, uint lockType, uint readLocks, uint editLocks) => VSConstants.S_OK;
    public int OnAfterAttributeChange(uint cookie, uint attributes) => VSConstants.S_OK;
    public int OnBeforeDocumentWindowShow(uint cookie, int firstShow, IVsWindowFrame frame) => VSConstants.S_OK;
    public int OnAfterDocumentWindowHide(uint cookie, IVsWindowFrame frame) => VSConstants.S_OK;
}
