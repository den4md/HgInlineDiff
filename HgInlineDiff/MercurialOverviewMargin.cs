using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace HgInlineDiff;

[Export(typeof(IWpfTextViewMarginProvider))]
[Name(MercurialOverviewMargin.MarginName)]
[MarginContainer(PredefinedMarginNames.VerticalScrollBarContainer)]
[Order(After = PredefinedMarginNames.VerticalScrollBar)]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class MercurialOverviewMarginProvider : IWpfTextViewMarginProvider
{
    [Import] internal IEditorFormatMapService Formats = null!;

    public IWpfTextViewMargin? CreateMargin(IWpfTextViewHost host, IWpfTextViewMargin container)
    {
        var scrollMargin = container.GetTextViewMargin(PredefinedMarginNames.VerticalScrollBar);
        return scrollMargin is IVerticalScrollBar scrollBar && scrollMargin is IWpfTextViewMargin wpfMargin
            ? new MercurialOverviewMargin(host.TextView, scrollBar, wpfMargin.VisualElement,
                Formats.GetEditorFormatMap(host.TextView)) : null;
    }
}

internal sealed class MercurialOverviewMargin : FrameworkElement, IWpfTextViewMargin
{
    public const string MarginName = "HgInlineDiff.MercurialOverviewMargin";
    private readonly IWpfTextView _view;
    private readonly IVerticalScrollBar _scroll;
    private readonly FrameworkElement _scrollElement;
    private readonly IEditorFormatMap _formats;
    private readonly MercurialChangeSet _changes;
    private bool _disposed;

    public MercurialOverviewMargin(IWpfTextView view, IVerticalScrollBar scroll, FrameworkElement scrollElement,
        IEditorFormatMap formats)
    {
        _view = view;
        _scroll = scroll;
        _scrollElement = scrollElement;
        _formats = formats;
        _changes = MercurialChangeSet.For(view);
        Width = 5;
        IsHitTestVisible = false;
        ClipToBounds = true;
        Visibility = _changes.Hunks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _changes.Changed += OnChanges;
        _scroll.TrackSpanChanged += OnTrackChanged;
        _scroll.Map.MappingChanged += OnTrackChanged;
        _formats.FormatMappingChanged += OnFormatsChanged;
        _view.Closed += OnClosed;
    }

    public FrameworkElement VisualElement => this;
    public double MarginSize => Width;
    public bool Enabled => !_disposed;
    public ITextViewMargin? GetTextViewMargin(string name) => name == MarginName ? this : null;

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var snapshot = _changes.Snapshot;
        if (_disposed || snapshot == null || _view.IsClosed)
            return;
        var current = _view.TextSnapshot;
        var offset = _scrollElement.TranslatePoint(new Point(), this).Y;
        var addition = GetBrush("HgVS.Addition");
        var modification = GetBrush("HgVS.Modification");
        var deletion = GetBrush("HgVS.Deletion");
        // Bound the drawing to scrollbar pixels even when a file contains thousands of tiny changes.
        var rows = new byte[(int)Math.Ceiling(ActualHeight)];
        foreach (var hunk in _changes.Hunks)
        {
            var first = hunk.StartLine >= snapshot.LineCount ? new SnapshotPoint(snapshot, snapshot.Length)
                : snapshot.GetLineFromLineNumber(hunk.StartLine).Start;
            var last = snapshot.GetLineFromLineNumber(Math.Min(hunk.StartLine + Math.Max(0, hunk.LineCount - 1),
                snapshot.LineCount - 1)).EndIncludingLineBreak;
            var start = first.TranslateTo(current, PointTrackingMode.Negative);
            var end = last.TranslateTo(current, PointTrackingMode.Positive);
            var top = _scroll.GetYCoordinateOfBufferPosition(start) + offset;
            var bottom = hunk.IsDeletion ? top : _scroll.GetYCoordinateOfBufferPosition(end) + offset;
            var firstRow = Math.Max(0, (int)Math.Floor(top - (hunk.IsDeletion ? 1 : 0)));
            var lastRow = Math.Min(rows.Length, (int)Math.Ceiling(Math.Max(top + 3, bottom)));
            var kind = (byte)(hunk.IsDeletion ? 4 : hunk.IsAddition ? 1 : 2);
            for (var row = firstRow; row < lastRow; row++)
                rows[row] |= kind;
        }
        for (var row = 0; row < rows.Length; row++)
        {
            if (rows[row] == 0) continue;
            var start = row;
            while (row + 1 < rows.Length && rows[row + 1] == rows[start]) row++;
            var brush = (rows[start] & 4) != 0 ? deletion : (rows[start] & 2) != 0 ? modification : addition;
            drawing.DrawRectangle(brush, null, new Rect(0, start, Width, row - start + 1));
        }
    }

    private Brush GetBrush(string name)
    {
        var properties = _formats.GetProperties(name);
        return properties[EditorFormatDefinition.BackgroundColorId] is Color color
            ? new SolidColorBrush(color) : properties[EditorFormatDefinition.BackgroundBrushId] as Brush ?? Brushes.Gray;
    }

    private void OnChanges(object? sender, EventArgs args)
    {
        Visibility = _changes.Hunks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        InvalidateVisual();
    }
    private void OnTrackChanged(object? sender, EventArgs args) => InvalidateVisual();
    private void OnFormatsChanged(object? sender, FormatItemsEventArgs args) => InvalidateVisual();
    private void OnClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _changes.Changed -= OnChanges;
        _scroll.TrackSpanChanged -= OnTrackChanged;
        _scroll.Map.MappingChanged -= OnTrackChanged;
        _formats.FormatMappingChanged -= OnFormatsChanged;
        _view.Closed -= OnClosed;
    }
}
