using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace HgInlineDiff;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 2)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideToolWindow(typeof(ChangesToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.Outputwindow)]
[ProvideService(typeof(MercurialStatusService), IsAsyncQueryable = true)]
[ProvideMercurialSourceControl]
[Guid(PackageGuid)]
public sealed class HgInlineDiffPackage : AsyncPackage, IVsSccProvider, IVsSccManager2, IVsSccManagerTooltip
{
    public const string PackageGuid = "1df11092-851d-4ca9-a39c-84ecf2492afe";
    internal const string ImageCatalogGuid = "378105bb-1d68-486b-815c-4a938f595a70";
    private static readonly Guid CommandSet = new Guid("b247faed-0622-43ee-9a24-a9636a89760f");
    private MercurialStatusMonitor? _monitor;
    private MercurialStatusService? _statusService;
    private MercurialMarginProvider? _margins;
    private JoinableTask? _showTask;
    internal MercurialStatusMonitor StatusMonitor => _monitor
        ?? throw new InvalidOperationException("Mercurial status monitor is not initialized.");

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService
            ?? throw new InvalidOperationException("Visual Studio command service is unavailable.");
        var components = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
        var solutionService = await GetServiceAsync(typeof(SVsSolution));
        var documentService = await GetServiceAsync(typeof(SVsRunningDocumentTable));
        var registrationService = await GetServiceAsync(typeof(IVsRegisterScciProvider));
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var solution = solutionService as IVsSolution
            ?? throw new InvalidOperationException("Visual Studio solution service is unavailable.");
        var documents = documentService as IVsRunningDocumentTable
            ?? throw new InvalidOperationException("Visual Studio document table is unavailable.");
        var registration = registrationService as IVsRegisterScciProvider
            ?? throw new InvalidOperationException("Visual Studio source control registration is unavailable.");
        var selection = registrationService as IVsGetScciProviderInterface
            ?? throw new InvalidOperationException("Visual Studio source control selection is unavailable.");
        var gitProvider = Guid.Empty;
        using (var configuration = ApplicationRegistryRoot)
        using (var providers = configuration.OpenSubKey("SourceControlProviders"))
        {
            if (providers != null)
            {
                foreach (var name in providers.GetSubKeyNames())
                {
                    using (var provider = providers.OpenSubKey(name))
                    {
                        if (provider?.GetValue(string.Empty) is string displayName
                            && displayName.IndexOf("Git", StringComparison.OrdinalIgnoreCase) >= 0
                            && Guid.TryParse(name, out var providerId))
                        {
                            gitProvider = providerId;
                            break;
                        }
                    }
                }
            }
        }
        _margins = components.GetService<MercurialMarginProvider>();
        _monitor = new MercurialStatusMonitor(solution, documents, JoinableTaskFactory);
        _margins.RefreshRequested += OnMarginRefresh;
        _statusService = new MercurialStatusService(_monitor, registration, selection, gitProvider);
        AddService(typeof(MercurialStatusService), (container, token, type) => Task.FromResult<object?>(_statusService), promote: true);
        commands.AddCommand(new MenuCommand((sender, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RefreshChanges();
        }, new CommandID(CommandSet, 0x0100)));
        commands.AddCommand(new MenuCommand((sender, args) =>
        {
            _showTask = JoinableTaskFactory.RunAsync(async () =>
            {
                try { await ShowToolWindowAsync(typeof(ChangesToolWindow), 0, true, DisposalToken); }
                catch (OperationCanceledException) when (DisposalToken.IsCancellationRequested) { }
                catch (Exception error) { ActivityLog.LogError(nameof(HgInlineDiff), error.ToString()); }
            });
        }, new CommandID(CommandSet, 0x0101)));
    }

    internal void RefreshChanges()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _margins?.RefreshAll();
    }

    private void OnMarginRefresh(object? sender, EventArgs args)
    {
        _monitor?.RequestRefresh(discover: true);
    }

    // The shell can query the package as well as its registered source control service.
    int IVsSccProvider.SetActive()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _statusService?.SetActive() ?? VSConstants.E_UNEXPECTED;
    }
    int IVsSccProvider.SetInactive()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _statusService?.SetInactive() ?? VSConstants.E_UNEXPECTED;
    }
    int IVsSccProvider.AnyItemsUnderSourceControl(out int result)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        result = 0;
        return _statusService?.AnyItemsUnderSourceControl(out result) ?? VSConstants.E_UNEXPECTED;
    }
    int IVsSccManager2.GetSccGlyph(int count, string[] paths, VsStateIcon[] glyphs, uint[] statuses)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _statusService?.GetSccGlyph(count, paths, glyphs, statuses) ?? VSConstants.E_UNEXPECTED;
    }
    int IVsSccManager2.GetSccGlyphFromStatus(uint status, VsStateIcon[] glyphs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _statusService?.GetSccGlyphFromStatus(status, glyphs) ?? VSConstants.E_UNEXPECTED;
    }
    int IVsSccManager2.RegisterSccProject(IVsSccProject2 project, string name, string auxiliary, string local, string provider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _statusService?.RegisterSccProject(project, name, auxiliary, local, provider) ?? VSConstants.E_UNEXPECTED;
    }
    int IVsSccManager2.UnregisterSccProject(IVsSccProject2 project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _statusService?.UnregisterSccProject(project) ?? VSConstants.E_UNEXPECTED;
    }
    int IVsSccManager2.IsInstalled(out int installed)
    {
        installed = 1;
        return VSConstants.S_OK;
    }
    int IVsSccManager2.BrowseForProject(out string directory, out int ok)
    {
        directory = string.Empty;
        ok = 0;
        return VSConstants.E_NOTIMPL;
    }
    int IVsSccManager2.CancelAfterBrowseForProject() => VSConstants.E_NOTIMPL;
    int IVsSccManagerTooltip.GetGlyphTipText(IVsHierarchy hierarchy, uint item, out string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        text = string.Empty;
        return _statusService?.GetGlyphTipText(hierarchy, item, out text) ?? VSConstants.E_UNEXPECTED;
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposing)
        {
            _showTask?.Join();
            if (_margins != null)
                _margins.RefreshRequested -= OnMarginRefresh;
            _monitor?.Dispose();
            _statusService?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class ProvideMercurialSourceControlAttribute : RegistrationAttribute
{
    private static string ProviderKey => "SourceControlProviders\\" + new Guid(MercurialStatusService.ProviderGuid).ToString("B");

    public override void Register(RegistrationContext context)
    {
        using (var key = context.CreateKey(ProviderKey))
        {
            key.SetValue(string.Empty, "HgInlineDiff");
            key.SetValue("Service", new Guid(MercurialStatusService.ServiceGuid).ToString("B"));
        }
        using (var key = context.CreateKey(ProviderKey + "\\Name"))
        {
            key.SetValue(string.Empty, "HgInlineDiff");
            key.SetValue("Package", new Guid(HgInlineDiffPackage.PackageGuid).ToString("B"));
        }
    }

    public override void Unregister(RegistrationContext context) => context.RemoveKey(ProviderKey);
}
