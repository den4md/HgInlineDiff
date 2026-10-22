// Editor registration adapted from Git Diff Margin. See THIRD-PARTY-NOTICES.md.
using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities;

namespace HgInlineDiff;

[Export(typeof(IWpfTextViewMarginProvider))]
[Export(typeof(MercurialMarginProvider))]
[Name(MercurialMargin.MarginName)]
[Order(After = PredefinedMarginNames.Spacer, Before = PredefinedMarginNames.Outlining)]
[MarginContainer(PredefinedMarginNames.LeftSelection)]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class MercurialMarginProvider : IWpfTextViewMarginProvider
{
    [Import] internal ITextDocumentFactoryService Documents = null!;
    [Import] internal ITextDifferencingSelectorService Differencing = null!;
    [Import] internal IEditorFormatMapService EditorFormats = null!;
    [Import] internal IClassificationFormatMapService ClassificationFormats = null!;
    [Import] internal ITextBufferFactoryService Buffers = null!;
    [Import] internal IProjectionBufferFactoryService Projections = null!;
    [Import] internal ITextEditorFactoryService Editors = null!;
    [Import] internal IEditorOperationsFactoryService Operations = null!;
    [Import(typeof(SVsServiceProvider))] internal IServiceProvider Services = null!;

    internal event EventHandler? RefreshRequested;

    internal void RefreshAll() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    public IWpfTextViewMargin? CreateMargin(IWpfTextViewHost textViewHost, IWpfTextViewMargin containerMargin)
    {
        var view = textViewHost.TextView;
        if (!Documents.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var document))
            return null;

        // shortcut: projection editors need buffer-to-view mapping before they can show correct markers.
        if (view.TextBuffer != document.TextBuffer)
            return null;

        return new MercurialMargin(view, document, this,
            Differencing.DefaultTextDifferencingService, EditorFormats.GetEditorFormatMap(view),
            ClassificationFormats.GetClassificationFormatMap(view));
    }
}
