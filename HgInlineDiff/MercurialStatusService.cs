using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace HgInlineDiff;

[Guid(ServiceGuid)]
[ComVisible(true)]
public sealed class MercurialStatusService : IVsSccProvider, IVsSccManager2,
    IVsSccManagerTooltip, IDisposable
{
    public const string ServiceGuid = "25898c71-47e2-4751-b8a7-606384b37722";
    public const string ProviderGuid = "67685710-c12a-4a24-8b27-3f279c7166a1";
    private readonly MercurialStatusMonitor _monitor;
    private readonly IVsRegisterScciProvider _registration;
    private readonly IVsGetScciProviderInterface _selection;
    private readonly Guid _gitProvider;
    private Guid? _previousProvider;
    private string _solutionFile = string.Empty;
    private bool _manuallyDisabled;
    private bool _active;

    internal MercurialStatusService(MercurialStatusMonitor monitor, IVsRegisterScciProvider registration,
        IVsGetScciProviderInterface selection, Guid gitProvider)
    {
        _monitor = monitor;
        _registration = registration;
        _selection = selection;
        _gitProvider = gitProvider;
        monitor.Changed += OnStatusChanged;
    }

    private void OnStatusChanged(object? sender, EventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solutionChanged = !_solutionFile.Equals(_monitor.SolutionFile, StringComparison.OrdinalIgnoreCase);
        if (solutionChanged)
        {
            _solutionFile = _monitor.SolutionFile;
            _manuallyDisabled = false;
        }
        _selection.GetSourceControlProviderID(out var selectedProvider);
        if (_monitor.IsGitSolution || _monitor.Repositories.Count == 0)
        {
            // VS can restore this provider from a previous session before opening a Git solution.
            var restore = _monitor.IsGitSolution && _gitProvider != Guid.Empty ? _gitProvider : _previousProvider;
            if (solutionChanged && selectedProvider == new Guid(ProviderGuid) && restore.HasValue)
                ErrorHandler.ThrowOnFailure(_registration.RegisterSourceControlProvider(restore.Value));
            return;
        }
        if (!_active && !_manuallyDisabled)
        {
            if (selectedProvider != new Guid(ProviderGuid))
                _previousProvider = selectedProvider;
            ErrorHandler.ThrowOnFailure(_registration.RegisterSourceControlProvider(new Guid(ProviderGuid)));
        }
        if (!_active)
            return;
        foreach (var hierarchy in _monitor.Projects())
        {
            if (hierarchy is IVsSccProject2 project)
            {
                var result = project.SccGlyphChanged(0, null, null, null);
                if (ErrorHandler.Failed(result))
                    ActivityLog.LogWarning(nameof(HgInlineDiff), "Project refused Mercurial glyph refresh: " + result);
            }
        }
    }

    private static VsStateIcon Icon(char status) => status switch
    {
        'C' => VsStateIcon.STATEICON_CHECKEDIN,
        'M' or 'A' or 'R' or '!' => VsStateIcon.STATEICON_CHECKEDOUT,
        '?' => VsStateIcon.STATEICON_ORPHANED,
        _ => VsStateIcon.STATEICON_NOSTATEICON
    };

    public int GetSccGlyph(int count, string[] paths, VsStateIcon[] glyphs, uint[] statuses)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        for (var i = 0; i < count && i < paths.Length && i < glyphs.Length; i++)
        {
            var status = _active && !string.IsNullOrEmpty(paths[i]) ? _monitor.GetStatus(paths[i]) : '\0';
            glyphs[i] = Icon(status);
            if (statuses != null && i < statuses.Length)
                statuses[i] = status == '\0' || status == '?' ? 0u : status == 'C' ? 1u : 3u;
        }
        return VSConstants.S_OK;
    }

    public int GetSccGlyphFromStatus(uint status, VsStateIcon[] glyphs)
    {
        if (glyphs.Length != 0)
            glyphs[0] = Icon((status & 1) == 0 ? '\0' : (status & 2) != 0 ? 'M' : 'C');
        return VSConstants.S_OK;
    }

    public int GetGlyphTipText(IVsHierarchy hierarchy, uint item, out string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        text = string.Empty;
        if (ErrorHandler.Succeeded(hierarchy.GetCanonicalName(item, out var path)) && path != null)
        {
            switch (_monitor.GetStatus(path))
            {
                case 'C': text = "Mercurial: clean"; break;
                case 'M': text = "Mercurial: modified"; break;
                case 'A': text = "Mercurial: added"; break;
                case 'R': text = "Mercurial: removed"; break;
                case '!': text = "Mercurial: missing"; break;
                case '?': text = "Mercurial: untracked"; break;
            }
        }
        return VSConstants.S_OK;
    }

    public int SetActive()
    {
        _active = true;
        _manuallyDisabled = false;
        _monitor.RequestRefresh();
        return VSConstants.S_OK;
    }
    public int SetInactive()
    {
        _active = false;
        _manuallyDisabled = true;
        return VSConstants.S_OK;
    }
    public int AnyItemsUnderSourceControl(out int result) { result = _monitor.Repositories.Count == 0 ? 0 : 1; return VSConstants.S_OK; }
    public int IsInstalled(out int installed) { installed = 1; return VSConstants.S_OK; }
    public int RegisterSccProject(IVsSccProject2 project, string name, string auxiliary, string local, string provider)
    { _monitor.RequestRefresh(true); return VSConstants.S_OK; }
    public int UnregisterSccProject(IVsSccProject2 project) { _monitor.RequestRefresh(true); return VSConstants.S_OK; }
    public int BrowseForProject(out string directory, out int ok) { directory = string.Empty; ok = 0; return VSConstants.E_NOTIMPL; }
    public int CancelAfterBrowseForProject() => VSConstants.E_NOTIMPL;

    public void Dispose()
    {
        _monitor.Changed -= OnStatusChanged;
    }
}
