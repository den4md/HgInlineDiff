using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace HgInlineDiff;

[Guid(ServiceGuid)]
[ComVisible(true)]
public sealed class MercurialStatusService : IVsSccProvider, IVsSccManager2, IVsSccGlyphs,
    IVsSccManagerTooltip, IDisposable
{
    public const string ServiceGuid = "25898c71-47e2-4751-b8a7-606384b37722";
    public const string ProviderGuid = "67685710-c12a-4a24-8b27-3f279c7166a1";
    private const string States = "CMAR!?";
    private readonly MercurialStatusMonitor _monitor;
    private readonly IVsRegisterScciProvider _registration;
    private ImageList? _images;
    private uint _baseIndex;
    private bool _active;

    internal MercurialStatusService(MercurialStatusMonitor monitor, IVsRegisterScciProvider registration)
    {
        _monitor = monitor;
        _registration = registration;
        monitor.Changed += OnStatusChanged;
    }

    private void OnStatusChanged(object? sender, EventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_active && _monitor.Repositories.Count != 0)
            ErrorHandler.ThrowOnFailure(_registration.RegisterSourceControlProvider(new Guid(ProviderGuid)));
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

    public int GetCustomGlyphList(uint baseIndex, out IntPtr imageListHandle)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _baseIndex = baseIndex;
        if (_images == null)
        {
            _images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            foreach (var status in States)
            {
                using (var bitmap = new Bitmap(16, 16))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    if (status == 'C' || status == 'M')
                    {
                        using (var brush = new SolidBrush(status == 'C' ? Color.FromArgb(115, 209, 109) : Color.FromArgb(241, 91, 104)))
                            graphics.FillEllipse(brush, 5, 5, 6, 6);
                    }
                    else
                    {
                        using (var brush = new SolidBrush(status == 'A' ? Color.FromArgb(86, 156, 214) : Color.FromArgb(241, 91, 104)))
                        using (var font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            graphics.DrawString(status == 'A' ? "+" : status == 'R' ? "×" : status.ToString(),
                                font, brush, new RectangleF(0, 0, 16, 16), format);
                    }
                    _images.Images.Add(bitmap);
                    // ImageList otherwise defers copying the first bitmap until after it is disposed.
                    _ = _images.Handle;
                }
            }
        }
        imageListHandle = _images.Handle;
        return VSConstants.S_OK;
    }

    private VsStateIcon Icon(char status)
    {
        var index = status == '\0' ? -1 : States.IndexOf(status);
        return index < 0 || _images == null ? VsStateIcon.STATEICON_NOSTATEICON : (VsStateIcon)(_baseIndex + index);
    }

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

    public int SetActive() { _active = true; _monitor.RequestRefresh(); return VSConstants.S_OK; }
    public int SetInactive()
    {
        _active = false;
        if (_monitor.Repositories.Count != 0)
            _monitor.RequestRefresh();
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
        _images?.Dispose();
    }
}
