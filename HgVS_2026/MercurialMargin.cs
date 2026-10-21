// Marker placement adapted from Git Diff Margin's EditorDiffMargin.cs.
// Copyright (c) 2012-2020 Laurent Kempé. See THIRD-PARTY-NOTICES.md and LICENSE.txt.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace HgVS_2026;

internal sealed class MercurialMargin : Canvas, IWpfTextViewMargin
{
    public const string MarginName = "HgVS.MercurialMargin";
    private readonly IWpfTextView _view;
    private readonly ITextDocument _document;
    private readonly MercurialMarginProvider _provider;
    private readonly ITextDifferencingService _differencing;
    private readonly IEditorFormatMap _formats;
    private readonly IClassificationFormatMap _classification;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _calculating;
    private MercurialBaseline? _baseline;
    private ITextSnapshot? _snapshot;
    private List<DiffHunk> _hunks = new List<DiffHunk>();
    private Popup? _popup;
    private string _loadedPath = string.Empty;
    private bool _disposed;
    private int _generation;

    public MercurialMargin(IWpfTextView view, ITextDocument document, MercurialMarginProvider provider,
        ITextDifferencingService differencing, IEditorFormatMap formats, IClassificationFormatMap classification)
    {
        _view = view;
        _document = document;
        _provider = provider;
        _differencing = differencing;
        _formats = formats;
        _classification = classification;
        Width = 10;
        ClipToBounds = true;
        Background = Brushes.Transparent;

        var refresh = new MenuItem { Header = "Refresh Mercurial Changes" };
        refresh.Click += (sender, args) => _provider.RefreshAll();
        ContextMenu = new ContextMenu();
        ContextMenu.Items.Add(refresh);

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _timer.Tick += OnTimer;
        _provider.RefreshRequested += OnRefreshRequested;
        _view.TextBuffer.Changed += OnTextChanged;
        _view.LayoutChanged += OnLayoutChanged;
        _view.Closed += OnClosed;
        _document.FileActionOccurred += OnFileAction;
        _formats.FormatMappingChanged += OnFormatsChanged;
        _ = ReloadAsync();
    }

    public FrameworkElement VisualElement => this;
    public double MarginSize => Width;
    public bool Enabled => !_disposed;
    public ITextViewMargin? GetTextViewMargin(string marginName) =>
        string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

    private async Task ReloadAsync()
    {
        if (_disposed)
            return;
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();
        var token = _loading.Token;
        var generation = ++_generation;
        _calculating?.Cancel();
        _baseline = null;
        _hunks.Clear();
        ClosePopup();
        Children.Clear();
        _loadedPath = _document.FilePath;
        ToolTip = "Loading Mercurial parent revision...";

        try
        {
            var baseline = await MercurialRepository.LoadAsync(_loadedPath, _document.Encoding, token);
            if (_disposed || generation != _generation || token.IsCancellationRequested)
                return;
            _baseline = baseline;
            ToolTip = baseline == null
                ? "Mercurial: untracked, ignored, or binary file. Right-click to refresh."
                : "Mercurial base: " + baseline.Revision + ". Right-click to refresh.";
            ScheduleComparison();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed && generation == _generation)
            {
                ToolTip = "Mercurial: " + error.Message;
                Trace.TraceError("HgVS: {0}", error);
            }
        }
    }

    private void ScheduleComparison()
    {
        _calculating?.Cancel();
        _timer.Stop();
        ClosePopup();
        Children.Clear();
        if (!_disposed && _baseline != null)
            _timer.Start();
    }

    private void OnTimer(object? sender, EventArgs args) => _ = CompareAsync();

    private async Task CompareAsync()
    {
        _timer.Stop();
        if (_disposed || _baseline == null)
            return;
        _calculating?.Dispose();
        _calculating = new CancellationTokenSource();
        var token = _calculating.Token;
        var generation = _generation;
        var snapshot = _view.TextSnapshot;
        var baseline = _baseline;

        try
        {
            if (snapshot.Length > MercurialRepository.MaximumFileBytes / sizeof(char))
                throw new InvalidOperationException("Mercurial margin supports text up to 2 million characters.");
            var hunks = await Task.Run(() => LineDiff.Calculate(_differencing, baseline.Text, snapshot.GetText(), token), token);
            if (_disposed || token.IsCancellationRequested || generation != _generation || snapshot != _view.TextSnapshot)
                return;
            _snapshot = snapshot;
            _hunks = hunks;
            RenderMarkers();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed && generation == _generation && !token.IsCancellationRequested)
            {
                _hunks.Clear();
                Children.Clear();
                ToolTip = "Mercurial: " + error.Message;
                Trace.TraceError("HgVS: {0}", error);
            }
        }
    }

    private void RenderMarkers()
    {
        Children.Clear();
        if (_disposed || _view.IsClosed || _snapshot != _view.TextSnapshot || _view.TextViewLines == null)
            return;

        foreach (var hunk in _hunks)
        {
            if (!GetDimensions(hunk, out var top, out var height))
                continue;

            var kind = hunk.IsDeletion ? "deleted" : hunk.IsAddition ? "added" : "modified";
            var brush = GetBrush(hunk.IsDeletion ? "HgVS.Deletion" : hunk.IsAddition ? "HgVS.Addition" : "HgVS.Modification");
            Shape marker;
            if (hunk.IsDeletion)
            {
                marker = new Polygon
                {
                    Points = new PointCollection { new Point(0, 0), new Point(8, height / 2), new Point(0, height) },
                    Width = 8,
                };
            }
            else
            {
                marker = new Rectangle { Width = 5 };
            }
            marker.Height = Math.Max(2, height);
            marker.Fill = brush;
            marker.Cursor = Cursors.Hand;
            marker.Focusable = true;
            marker.ToolTip = "Mercurial " + kind + " lines. Click to view the parent text.";
            AutomationProperties.SetName(marker, "Mercurial " + kind + " lines");
            marker.MouseLeftButtonUp += (sender, args) =>
            {
                ShowPopup(hunk, marker);
                args.Handled = true;
            };
            marker.KeyDown += (sender, args) =>
            {
                if (args.Key == Key.Enter || args.Key == Key.Space)
                {
                    ShowPopup(hunk, marker);
                    args.Handled = true;
                }
            };
            SetLeft(marker, 2.5);
            SetTop(marker, top);
            Children.Add(marker);
        }
    }

    private bool GetDimensions(DiffHunk hunk, out double top, out double height)
    {
        top = height = 0;
        var snapshot = _view.TextSnapshot;
        if (hunk.StartLine < 0 || hunk.StartLine > snapshot.LineCount)
            return false;

        if (hunk.IsDeletion)
        {
            // A deletion is a boundary: before the following line, or after the last line at EOF.
            var atEnd = hunk.StartLine == snapshot.LineCount;
            var line = snapshot.GetLineFromLineNumber(Math.Min(hunk.StartLine, snapshot.LineCount - 1));
            var viewLine = _view.GetTextViewLineContainingBufferPosition(line.Start);
            if (viewLine == null || viewLine.VisibilityState == VisibilityState.Unattached)
                return false;
            height = _view.LineHeight;
            top = (atEnd ? viewLine.Bottom : viewLine.Top) - _view.ViewportTop - height / 2;
            return top + height >= 0 && top <= _view.ViewportHeight;
        }

        var endNumber = hunk.StartLine + hunk.LineCount - 1;
        if (hunk.StartLine >= snapshot.LineCount || endNumber >= snapshot.LineCount)
            return false;
        var startLine = snapshot.GetLineFromLineNumber(hunk.StartLine);
        var endLine = snapshot.GetLineFromLineNumber(endNumber);
        var span = new SnapshotSpan(startLine.Start, endLine.End);
        if (!_view.TextViewLines.FormattedSpan.IntersectsWith(span))
            return false;
        var startView = _view.GetTextViewLineContainingBufferPosition(startLine.Start);
        var endView = _view.GetTextViewLineContainingBufferPosition(endLine.Start);
        if (startView == null || endView == null
            || _view.TextViewLines.LastVisibleLine.EndIncludingLineBreak < startView.Start
            || _view.TextViewLines.FirstVisibleLine.Start > endView.EndIncludingLineBreak)
            return false;

        top = startView.VisibilityState == VisibilityState.Unattached ? 0 : startView.Top - _view.ViewportTop;
        var bottom = endView.VisibilityState == VisibilityState.Unattached ? _view.ViewportHeight : endView.Bottom - _view.ViewportTop;
        height = Math.Max(2, bottom - top);
        return true;
    }

    private Brush GetBrush(string name)
    {
        var properties = _formats.GetProperties(name);
        if (properties[EditorFormatDefinition.BackgroundColorId] is Color color)
            return new SolidColorBrush(color);
        return properties[EditorFormatDefinition.BackgroundBrushId] as Brush ?? Brushes.Gray;
    }

    private void ShowPopup(DiffHunk hunk, FrameworkElement marker)
    {
        ClosePopup();
        var properties = _classification.DefaultTextProperties;
        var text = new TextBox
        {
            IsReadOnly = true,
            Text = hunk.IsAddition ? "No text in the parent revision. These lines were added." : hunk.OriginalText,
            FontFamily = properties.TypefaceEmpty ? new FontFamily("Consolas") : properties.Typeface.FontFamily,
            FontSize = properties.FontRenderingEmSizeEmpty ? 12 : properties.FontRenderingEmSize,
            Background = _view.Background,
            Foreground = properties.ForegroundBrushEmpty ? SystemColors.ControlTextBrush : properties.ForegroundBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
            MaxWidth = Math.Max(240, _view.ViewportWidth * 0.8),
            MaxHeight = Math.Max(120, _view.ViewportHeight * 0.6),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetName(text, "Mercurial parent revision text");
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Parent: " + _baseline?.Revision + "  (Esc to close)",
            Padding = new Thickness(8, 4, 8, 4),
            Foreground = text.Foreground,
        });
        panel.Children.Add(text);
        _popup = new Popup
        {
            PlacementTarget = marker,
            Placement = PlacementMode.Right,
            StaysOpen = false,
            Child = new Border
            {
                Background = text.Background,
                BorderBrush = text.Foreground,
                BorderThickness = new Thickness(1),
                Child = panel,
            },
        };
        _popup.PreviewKeyDown += (sender, args) =>
        {
            if (args.Key == Key.Escape)
            {
                ClosePopup();
                _view.VisualElement.Focus();
                args.Handled = true;
            }
        };
        _popup.IsOpen = true;
        text.Focus();
    }

    private void ClosePopup()
    {
        if (_popup != null)
        {
            _popup.IsOpen = false;
            _popup = null;
        }
    }

    private void OnTextChanged(object? sender, TextContentChangedEventArgs args) => ScheduleComparison();
    private void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs args)
    {
        // Focusing a popup may trigger layout without scrolling. Keep its placement target alive.
        if (_popup?.IsOpen == true && _snapshot == _view.TextSnapshot
            && args.OldViewState.ViewportTop == args.NewViewState.ViewportTop
            && args.OldViewState.ViewportLeft == args.NewViewState.ViewportLeft
            && args.OldViewState.ViewportHeight == args.NewViewState.ViewportHeight
            && args.OldViewState.ViewportWidth == args.NewViewState.ViewportWidth)
            return;
        ClosePopup();
        RenderMarkers();
    }
    private void OnFormatsChanged(object? sender, FormatItemsEventArgs args) => RenderMarkers();
    private void OnRefreshRequested(object? sender, EventArgs args) => _ = ReloadAsync();
    private void OnFileAction(object? sender, TextDocumentFileActionEventArgs args)
    {
        if (!string.Equals(_loadedPath, _document.FilePath, StringComparison.OrdinalIgnoreCase))
            _ = ReloadAsync();
    }
    private void OnClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _generation++;
        _timer.Stop();
        _timer.Tick -= OnTimer;
        _loading?.Cancel();
        _loading?.Dispose();
        _calculating?.Cancel();
        _calculating?.Dispose();
        _provider.RefreshRequested -= OnRefreshRequested;
        _view.TextBuffer.Changed -= OnTextChanged;
        _view.LayoutChanged -= OnLayoutChanged;
        _view.Closed -= OnClosed;
        _document.FileActionOccurred -= OnFileAction;
        _formats.FormatMappingChanged -= OnFormatsChanged;
        ClosePopup();
        Children.Clear();
    }
}
