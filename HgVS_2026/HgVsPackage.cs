using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;

namespace HgVS_2026;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[Guid(PackageGuid)]
public sealed class HgVsPackage : AsyncPackage
{
    public const string PackageGuid = "1df11092-851d-4ca9-a39c-84ecf2492afe";
    private static readonly Guid CommandSet = new Guid("b247faed-0622-43ee-9a24-a9636a89760f");

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService
            ?? throw new InvalidOperationException("Visual Studio command service is unavailable.");
        var components = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel
            ?? throw new InvalidOperationException("Visual Studio component model is unavailable.");
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        commands.AddCommand(new MenuCommand((sender, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            components.GetService<MercurialMarginProvider>().RefreshAll();
        }, new CommandID(CommandSet, 0x0100)));
    }
}
