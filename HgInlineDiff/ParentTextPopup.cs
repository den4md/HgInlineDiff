using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Projection;

namespace HgInlineDiff;

internal sealed class ParentTextPopup : Popup, IOleCommandTarget, IDisposable
{
    private readonly IWpfTextView _owner;
    private readonly IWpfTextViewHost _host;
    private readonly ParentTextWorkspace? _workspace;
    private readonly IEditorOperations _operations;
    private readonly IVsRegisterPriorityCommandTarget? _commands;
    private readonly uint _cookie;
    private readonly bool _registered;
    private readonly Window? _window;
    private bool _disposed;
    internal IWpfTextView Preview { get; }

    public ParentTextPopup(IWpfTextView owner, MercurialMarginProvider provider, FrameworkElement marker,
        DiffHunk hunk, MercurialBaseline? baseline, IClassificationFormatMap classification)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _owner = owner;
        var text = hunk.IsAddition ? "No text in the parent revision. These lines were added." : hunk.OriginalText;
        var parent = hunk.IsAddition ? null : baseline?.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        var buffer = provider.Buffers.CreateTextBuffer(parent ?? text, owner.TextBuffer.ContentType);
        var source = owner.TextBuffer.AsTextContainer();
        if (Workspace.TryGetWorkspace(source, out var workspace))
        {
            var document = workspace.CurrentSolution.GetDocument(workspace.GetDocumentIdInCurrentContext(source));
            if (document != null)
                _workspace = new ParentTextWorkspace(document, buffer.AsTextContainer());
        }
        try
        {
            // Classify the complete parent file, but expose only the hunk to selection, copying, and scrolling.
            var previewBuffer = parent == null ? buffer : provider.Projections.CreateElisionBuffer(null,
                new NormalizedSnapshotSpanCollection(new SnapshotSpan(buffer.CurrentSnapshot, hunk.OriginalStart, text.Length)),
                ElisionBufferOptions.None);
            Preview = provider.Editors.CreateTextView(previewBuffer, provider.Editors.CreateTextViewRoleSet(
                PredefinedTextViewRoles.Analyzable, PredefinedTextViewRoles.Interactive));
            Preview.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);
            Preview.Options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginId, false);
            Preview.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, false);
            Preview.Options.SetOptionValue(DefaultTextViewHostOptions.OutliningMarginId, false);
            Preview.Options.SetOptionValue(DefaultTextViewHostOptions.ZoomControlId, false);
            Preview.Background = owner.Background;
            Preview.ZoomLevel = owner.ZoomLevel;
            _host = provider.Editors.CreateTextViewHost(Preview, false);
        }
        catch { Preview?.Close(); _workspace?.Dispose(); throw; }
        try
        {
            _window = Window.GetWindow(owner.VisualElement);
            _operations = provider.Operations.GetEditorOperations(Preview);
            var properties = classification.DefaultTextProperties;
            var foreground = properties.ForegroundBrushEmpty ? SystemColors.ControlTextBrush : properties.ForegroundBrush;
            var panel = new DockPanel();
            var heading = new TextBlock
            {
                Text = "Parent: " + (baseline?.Revision ?? string.Empty) + "  (Esc to close)",
                Padding = new Thickness(8, 4, 8, 4),
                Foreground = foreground,
            };
            DockPanel.SetDock(heading, Dock.Top);
            panel.Children.Add(heading);
            var width = Math.Min(900, Math.Max(240, owner.ViewportWidth * 0.8));
            var height = Math.Min(Math.Max(100, owner.ViewportHeight * 0.6),
                Math.Max(60, Preview.TextSnapshot.LineCount * owner.LineHeight + 30));
            _host.HostControl.Width = width;
            _host.HostControl.Height = height;
            panel.Children.Add(_host.HostControl);
            AutomationProperties.SetName(Preview.VisualElement, "Mercurial parent revision text");
            PlacementTarget = marker;
            Placement = PlacementMode.Right;
            // Do not capture the mouse: clicks on CodeLens and other editor UI must pass through.
            StaysOpen = true;
            Child = new Border
            {
                Background = owner.Background,
                BorderBrush = foreground,
                BorderThickness = new Thickness(1),
                Child = panel,
            };
            var menu = new ContextMenu();
            var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
            copy.Click += (sender, args) => _operations.CopySelection();
            var selectAll = new MenuItem { Header = "Select All", InputGestureText = "Ctrl+A" };
            selectAll.Click += (sender, args) => _operations.SelectAll();
            menu.Items.Add(copy);
            menu.Items.Add(selectAll);
            Preview.VisualElement.ContextMenu = menu;
            panel.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown), true);
            owner.VisualElement.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown), true);
            InputManager.Current.PreProcessInput += OnInput;
            if (_window != null) _window.Deactivated += OnWindowDeactivated;
            owner.VisualElement.IsVisibleChanged += OnOwnerVisibilityChanged;
            Closed += (sender, args) => Dispose();
            Opened += (sender, args) => Preview.VisualElement.Focus();
            var commandService = provider.Services.GetService(typeof(SVsRegisterPriorityCommandTarget));
            if (commandService is IVsRegisterPriorityCommandTarget commands)
            {
                _commands = commands;
                _registered = ErrorHandler.Succeeded(commands.RegisterPriorityCommandTarget(0, this, out _cookie));
            }
            IsOpen = true;
            Preview.VisualElement.Focus();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void CloseAndFocusEditor()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Dispose();
        _owner.VisualElement.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            CloseAndFocusEditor();
        }
        else if (Preview.VisualElement.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.C)
        {
            args.Handled = true;
            _operations.CopySelection();
        }
        else if (Preview.VisualElement.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.A)
        {
            args.Handled = true;
            _operations.SelectAll();
        }
    }

    private void OnInput(object sender, PreProcessInputEventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (args.StagingItem.Input is MouseButtonEventArgs mouse && mouse.ButtonState == MouseButtonState.Pressed
            && Preview.VisualElement.ContextMenu?.IsOpen != true)
        {
            var position = mouse.GetPosition(Child);
            if (!new Rect(Child.RenderSize).Contains(position))
                Dispose();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Dispose();
    }

    private void OnOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!(bool)args.NewValue) Dispose();
    }

    private Action? GetCommand(Guid group, uint id)
    {
        if (!IsOpen || _disposed)
            return null;
        var previewFocused = Preview.HasAggregateFocus || Preview.VisualElement.IsKeyboardFocusWithin;
        if (group == VSConstants.VSStd2K && id == (uint)VSConstants.VSStd2KCmdID.CANCEL
            && (previewFocused || _owner.HasAggregateFocus))
            return CloseAndFocusEditor;
        if (!previewFocused)
            return null;
        if (group == VSConstants.GUID_VSStandardCommandSet97)
        {
            switch ((VSConstants.VSStd97CmdID)id)
            {
                case VSConstants.VSStd97CmdID.Copy: return () => _operations.CopySelection();
                case VSConstants.VSStd97CmdID.SelectAll: return () => _operations.SelectAll();
                case VSConstants.VSStd97CmdID.Cut:
                case VSConstants.VSStd97CmdID.Paste:
                case VSConstants.VSStd97CmdID.Delete:
                case VSConstants.VSStd97CmdID.Undo:
                case VSConstants.VSStd97CmdID.Redo:
                case VSConstants.VSStd97CmdID.MultiLevelUndo:
                case VSConstants.VSStd97CmdID.MultiLevelRedo: return () => { };
            }
        }
        if (group != VSConstants.VSStd2K)
            return null;
        var command = (VSConstants.VSStd2KCmdID)id;
        switch (command)
        {
            case VSConstants.VSStd2KCmdID.COPY: return () => _operations.CopySelection();
            case VSConstants.VSStd2KCmdID.SELECTALL: return () => _operations.SelectAll();
            case VSConstants.VSStd2KCmdID.LEFT:
            case VSConstants.VSStd2KCmdID.LEFT_EXT: return () => _operations.MoveToPreviousCharacter(command == VSConstants.VSStd2KCmdID.LEFT_EXT);
            case VSConstants.VSStd2KCmdID.RIGHT:
            case VSConstants.VSStd2KCmdID.RIGHT_EXT: return () => _operations.MoveToNextCharacter(command == VSConstants.VSStd2KCmdID.RIGHT_EXT);
            case VSConstants.VSStd2KCmdID.UP:
            case VSConstants.VSStd2KCmdID.UP_EXT: return () => _operations.MoveLineUp(command == VSConstants.VSStd2KCmdID.UP_EXT);
            case VSConstants.VSStd2KCmdID.DOWN:
            case VSConstants.VSStd2KCmdID.DOWN_EXT: return () => _operations.MoveLineDown(command == VSConstants.VSStd2KCmdID.DOWN_EXT);
            case VSConstants.VSStd2KCmdID.HOME:
            case VSConstants.VSStd2KCmdID.HOME_EXT: return () => _operations.MoveToStartOfDocument(command == VSConstants.VSStd2KCmdID.HOME_EXT);
            case VSConstants.VSStd2KCmdID.END:
            case VSConstants.VSStd2KCmdID.END_EXT: return () => _operations.MoveToEndOfDocument(command == VSConstants.VSStd2KCmdID.END_EXT);
            case VSConstants.VSStd2KCmdID.BOL:
            case VSConstants.VSStd2KCmdID.BOL_EXT: return () => _operations.MoveToHome(command == VSConstants.VSStd2KCmdID.BOL_EXT);
            case VSConstants.VSStd2KCmdID.EOL:
            case VSConstants.VSStd2KCmdID.EOL_EXT: return () => _operations.MoveToEndOfLine(command == VSConstants.VSStd2KCmdID.EOL_EXT);
            case VSConstants.VSStd2KCmdID.PAGEUP:
            case VSConstants.VSStd2KCmdID.PAGEUP_EXT: return () => _operations.PageUp(command == VSConstants.VSStd2KCmdID.PAGEUP_EXT);
            case VSConstants.VSStd2KCmdID.PAGEDN:
            case VSConstants.VSStd2KCmdID.PAGEDN_EXT: return () => _operations.PageDown(command == VSConstants.VSStd2KCmdID.PAGEDN_EXT);
            case VSConstants.VSStd2KCmdID.UNDO:
            case VSConstants.VSStd2KCmdID.REDO:
            case VSConstants.VSStd2KCmdID.UNDONOMOVE:
            case VSConstants.VSStd2KCmdID.REDONOMOVE: return () => { };
            case VSConstants.VSStd2KCmdID.DELETEWORDLEFT:
            case VSConstants.VSStd2KCmdID.DELETEWORDRIGHT: return () => { };
        }
        // Read-only preview commands must never fall through and edit the active source document.
        if (id >= (uint)VSConstants.VSStd2KCmdID.TYPECHAR && id <= (uint)VSConstants.VSStd2KCmdID.DELETE
            || id >= (uint)VSConstants.VSStd2KCmdID.SELTABIFY && id <= (uint)VSConstants.VSStd2KCmdID.SELTITLECASE
            || id >= (uint)VSConstants.VSStd2KCmdID.CUT && id <= (uint)VSConstants.VSStd2KCmdID.UNINDENT)
            return () => { };
        return null;
    }

    public int QueryStatus(ref Guid commandGroup, uint count, OLECMD[] commands, IntPtr commandText)
    {
        var handled = false;
        for (var index = 0; index < count; index++)
            if (GetCommand(commandGroup, commands[index].cmdID) != null)
            {
                commands[index].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                handled = true;
            }
        return handled ? VSConstants.S_OK : (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    public int Exec(ref Guid commandGroup, uint command, uint options, IntPtr input, IntPtr output)
    {
        var action = GetCommand(commandGroup, command);
        if (action == null)
            return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        action();
        return VSConstants.S_OK;
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed)
            return;
        _disposed = true;
        if (_registered)
            _commands!.UnregisterPriorityCommandTarget(_cookie);
        InputManager.Current.PreProcessInput -= OnInput;
        if (_window != null) _window.Deactivated -= OnWindowDeactivated;
        _owner.VisualElement.IsVisibleChanged -= OnOwnerVisibilityChanged;
        _owner.VisualElement.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown));
        IsOpen = false;
        try { _host.Close(); }
        finally { _workspace?.Dispose(); }
    }
}

// Roslyn classifiers require an open document, even when the buffer has the correct content type.
internal sealed class ParentTextWorkspace : Workspace
{
    internal ParentTextWorkspace(Document document, SourceTextContainer text)
        : base(document.Project.Solution.Workspace.Services.HostServices, WorkspaceKind.Preview)
    {
        try
        {
            SetCurrentSolution(document.Project.Solution.WithDocumentText(document.Id, text.CurrentText));
            OnDocumentOpened(document.Id, text);
        }
        catch { Dispose(); throw; }
    }

    protected override void Dispose(bool finalize)
    {
        base.Dispose(finalize);
        ClearSolution();
    }
}
