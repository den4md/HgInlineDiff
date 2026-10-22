using System;
using System.Windows.Controls;
using HgInlineDiff;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Utilities;

// Real WPF popup content; VS factories and the shell's command registry are substituted.
internal sealed class PopupTestServices : System.IServiceProvider
{
    public TextBox Text { get; } = new TextBox();
    public bool PreviewFocused { get; set; } = true;
    public int Copies { get; private set; }
    public int SelectAlls { get; private set; }
    public int Unregistrations { get; private set; }
    public int HostsClosed { get; private set; }
    public bool ReadOnly { get; private set; }
    public IContentType? ContentType { get; private set; }
    public string BufferText { get; private set; } = string.Empty;
    public Span? ExposedSpan { get; private set; }
    public bool UsesProjection { get; private set; }
    public IOleCommandTarget? Target { get; private set; }
    private readonly IVsRegisterPriorityCommandTarget _registry;

    public PopupTestServices(MercurialMarginProvider provider)
    {
        _registry = MarginUiTests.InterfaceProxy<IVsRegisterPriorityCommandTarget>.Create(call =>
        {
            if (call.MethodName == "RegisterPriorityCommandTarget")
            {
                Target = (IOleCommandTarget)call.Args[1];
                call.Args[2] = 42u;
            }
            if (call.MethodName == "UnregisterPriorityCommandTarget") Unregistrations++;
            return 0;
        });
        provider.Services = this;
        ITextSnapshot snapshot = null!;
        snapshot = MarginUiTests.InterfaceProxy<ITextSnapshot>.Create(call => call.MethodName switch
        {
            "get_Length" => Text.Text.Length,
            "get_LineCount" => Text.Text.Split('\n').Length,
            "GetText" => Text.Text,
            _ => null,
        });
        var sourceSnapshot = MarginUiTests.InterfaceProxy<ITextSnapshot>.Create(call => call.MethodName switch
        {
            "get_Length" => BufferText.Length,
            "get_LineCount" => BufferText.Split('\n').Length,
            "GetText" => BufferText,
            _ => null,
        });
        var buffer = MarginUiTests.InterfaceProxy<ITextBuffer>.Create(call => call.MethodName switch
        {
            "get_CurrentSnapshot" => sourceSnapshot,
            "get_ContentType" => ContentType,
            _ => null,
        });
        provider.Buffers = MarginUiTests.InterfaceProxy<ITextBufferFactoryService>.Create(call =>
        {
            Text.Text = BufferText = (string)call.Args[0];
            ExposedSpan = null;
            ContentType = (IContentType)call.Args[1];
            return buffer;
        });
        var projection = MarginUiTests.InterfaceProxy<IElisionBuffer>.Create(call =>
            call.MethodName == "get_CurrentSnapshot" ? snapshot : null);
        provider.Projections = MarginUiTests.InterfaceProxy<IProjectionBufferFactoryService>.Create(call =>
        {
            var spans = (NormalizedSnapshotSpanCollection)call.Args[1];
            if (spans.Count != 1) throw new Exception("Preview must expose exactly one parent fragment.");
            ExposedSpan = spans[0].Span;
            Text.Text = BufferText.Substring(ExposedSpan.Value.Start, ExposedSpan.Value.Length);
            return projection;
        });
        var options = MarginUiTests.InterfaceProxy<IEditorOptions>.Create(call =>
        {
            if (call.MethodName == "SetOptionValue" && call.Args[0] is EditorOptionKey<bool> key
                && key.Equals(DefaultTextViewOptions.ViewProhibitUserInputId))
                ReadOnly = (bool)call.Args[1];
            return null;
        });
        var preview = MarginUiTests.InterfaceProxy<IWpfTextView>.Create(call => call.MethodName switch
        {
            "get_VisualElement" => Text,
            "get_TextSnapshot" => snapshot,
            "get_TextBuffer" => buffer,
            "get_Options" => options,
            "get_HasAggregateFocus" => PreviewFocused,
            _ => null,
        });
        IWpfTextViewHost CreateHost()
        {
            var hostControl = new UserControl { Content = Text };
            return MarginUiTests.InterfaceProxy<IWpfTextViewHost>.Create(call =>
            {
                if (call.MethodName == "Close") { HostsClosed++; hostControl.Content = null; }
                return call.MethodName switch
                {
                    "get_HostControl" => hostControl,
                    "get_TextView" => preview,
                    _ => null,
                };
            });
        }
        provider.Editors = MarginUiTests.InterfaceProxy<ITextEditorFactoryService>.Create(call =>
        {
            if (call.MethodName == "CreateTextView") UsesProjection = ReferenceEquals(call.Args[0], projection);
            return call.MethodName switch
            {
                "CreateTextView" => preview,
                "CreateTextViewHost" => CreateHost(),
                "CreateTextViewRoleSet" => MarginUiTests.InterfaceProxy<ITextViewRoleSet>.Create(_ => null),
                _ => null,
            };
        });
        var operations = MarginUiTests.InterfaceProxy<IEditorOperations>.Create(call =>
        {
            if (call.MethodName == "CopySelection") { Copies++; return true; }
            if (call.MethodName == "SelectAll") SelectAlls++;
            return null;
        });
        provider.Operations = MarginUiTests.InterfaceProxy<IEditorOperationsFactoryService>.Create(_ => operations);
    }

    public object? GetService(Type type) => type == typeof(SVsRegisterPriorityCommandTarget) ? _registry : null;
}
