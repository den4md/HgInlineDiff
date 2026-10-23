using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace HgInlineDiff;

[Guid("baed0d33-d489-432a-a04b-edf953cdcf3e")]
public sealed class ChangesToolWindow : ToolWindowPane
{
    private readonly DataGrid _files;
    private readonly DataGridTextColumn _repositoryColumn;
    private readonly TextBlock _message;
    private MercurialStatusMonitor? _monitor;
    private HgInlineDiffPackage? _package;
    private bool _pinOnRelease;

    public ChangesToolWindow() : base(null)
    {
        Caption = "HgInlineDiff: Changes";
        var panel = new DockPanel();
        panel.SetResourceReference(Control.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        var refresh = new Button { Content = "Refresh", Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += (sender, args) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package?.RefreshChanges();
        };
        DockPanel.SetDock(refresh, Dock.Top);
        panel.Children.Add(refresh);
        _message = new TextBlock { Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap };
        _message.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        DockPanel.SetDock(_message, Dock.Bottom);
        panel.Children.Add(_message);
        _files = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = false,
            CanUserReorderColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            EnableRowVirtualization = true,
            RowBackground = Brushes.Transparent,
            AlternatingRowBackground = Brushes.Transparent,
        };
        _files.SetResourceReference(Control.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        _files.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        _files.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = 55 });
        _files.Columns.Add(new DataGridTextColumn { Header = "Path", Binding = new Binding("Path"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _repositoryColumn = new DataGridTextColumn { Header = "Repository", Binding = new Binding("Repository"), Width = DataGridLength.SizeToCells };
        _files.Columns.Add(_repositoryColumn);
        _files.PreviewMouseLeftButtonDown += (sender, args) => _pinOnRelease = args.ClickCount >= 2;
        _files.PreviewMouseLeftButtonUp += OnFileClick;
        _files.PreviewKeyDown += OnFileKey;
        panel.Children.Add(_files);
        Content = panel;
    }

    public override void OnToolWindowCreated()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        base.OnToolWindowCreated();
        _package = (HgInlineDiffPackage)Package;
        _monitor = _package.StatusMonitor;
        _monitor.Changed += OnStatusChanged;
        UpdateFiles();
        _monitor.RequestRefresh(true);
    }

    private void OnStatusChanged(object? sender, EventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        UpdateFiles();
    }

    private void UpdateFiles()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_monitor == null)
            return;
        var selected = _files.SelectedItem as MercurialFileChange;
        var changes = _monitor.Repositories.SelectMany(repository => repository.Changes)
            .GroupBy(change => change.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(change => change.Repository, StringComparer.OrdinalIgnoreCase).ToArray();
        _files.ItemsSource = changes;
        if (selected != null)
            _files.SelectedItem = changes.FirstOrDefault(change => change.FullPath.Equals(selected.FullPath, StringComparison.OrdinalIgnoreCase));
        _repositoryColumn.Visibility = _monitor.Repositories.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _message.Text = _monitor.Error.Length != 0 ? _monitor.Error
            : _monitor.Repositories.Count == 0 ? "No Mercurial repositories in the current solution."
            : changes.Length == 0 ? "No changes." : changes.Length + " changed files";
    }

    private void OnFileClick(object sender, MouseButtonEventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (args.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(_files, source) is DataGridRow row
            && row.Item is MercurialFileChange change)
            OpenFile(change, preview: !_pinOnRelease);
    }

    private void OnFileKey(object sender, KeyEventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (args.Key == Key.Enter && _files.SelectedItem is MercurialFileChange change)
        {
            OpenFile(change, preview: false);
            args.Handled = true;
        }
    }

    private void OpenFile(MercurialFileChange change, bool preview)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_package == null || !File.Exists(change.FullPath))
            return;
        try
        {
            using (new NewDocumentStateScope(preview ? __VSNEWDOCUMENTSTATE.NDS_Provisional : __VSNEWDOCUMENTSTATE.NDS_Permanent,
                new Guid(HgInlineDiffPackage.PackageGuid)))
            {
                VsShellUtilities.OpenDocument(_package, change.FullPath, VSConstants.LOGVIEWID_Primary,
                    out _, out _, out var frame);
                if (!preview)
                    ErrorHandler.ThrowOnFailure(frame.SetProperty((int)__VSFPROPID5.VSFPROPID_IsProvisional, false));
            }
        }
        catch (Exception error) when (error is COMException || error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
        {
            _message.Text = "Cannot open " + change.Path + ": " + error.Message;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _monitor != null)
            _monitor.Changed -= OnStatusChanged;
        base.Dispose(disposing);
    }
}
