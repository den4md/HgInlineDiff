// Marker placement adapted from Git Diff Margin's EditorDiffMargin.cs.
// Copyright (c) 2012-2020 Laurent Kempé. See THIRD-PARTY-NOTICES.md and LICENSE.txt.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;

namespace HgInlineDiff;

internal sealed class MercurialMargin : Canvas, IWpfTextViewMargin
{
    public const string MarginName = "HgInlineDiff.MercurialMargin";
    private readonly IWpfTextView _view;
    private readonly ITextDocument _document;
    private readonly MercurialMarginProvider _provider;
    private readonly DiffUpdates _updates;
    private readonly IEditorFormatMap _formats;
    private readonly IClassificationFormatMap _classification;
    private CancellationTokenSource? _loading;
    private MercurialBaseline? _baseline;
    private ITextSnapshot? _snapshot;
    private List<DiffHunk> _hunks = new List<DiffHunk>();
    private readonly Dictionary<DiffHunk, Shape> _markers = new Dictionary<DiffHunk, Shape>();
    private ParentTextPopup? _popup;
    private readonly MercurialChangeSet _changes;
    private string _loadedPath = string.Empty;
    private bool _disposed;
    private int _generation;

    public MercurialMargin(IWpfTextView view, ITextDocument document, MercurialMarginProvider provider,
        ITextDifferencingService differencing, IEditorFormatMap formats, IClassificationFormatMap classification)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _view = view;
        _document = document;
        _provider = provider;
        _updates = new DiffUpdates(differencing);
        _formats = formats;
        _classification = classification;
        _changes = MercurialChangeSet.For(view);
        Width = 10;
        Visibility = Visibility.Collapsed;
        ClipToBounds = true;
        Background = Brushes.Transparent;

        var refresh = new MenuItem { Header = "Refresh Mercurial Changes" };
        refresh.Click += (sender, args) => _provider.RefreshAll();
        ContextMenu = new ContextMenu();
        ContextMenu.Items.Add(refresh);

        _provider.RefreshRequested += OnRefreshRequested;
        _view.TextBuffer.Changed += OnTextChanged;
        _view.LayoutChanged += OnLayoutChanged;
        _view.Closed += OnClosed;
        _document.FileActionOccurred += OnFileAction;
        _formats.FormatMappingChanged += OnFormatsChanged;
        InputManager.Current.PreProcessInput += OnCapturedMouseInput;
        _ = ReloadAsync();
    }

    public FrameworkElement VisualElement => this;
    public double MarginSize => Width;
    public bool Enabled => !_disposed;
    public ITextViewMargin? GetTextViewMargin(string marginName) =>
        string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

    private async Task ReloadAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_disposed)
            return;
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();
        var token = _loading.Token;
        var generation = ++_generation;
        _updates.Cancel();
        _baseline = null;
        _hunks.Clear();
        _changes.Publish(null, _hunks);
        ClosePopup();
        ClearMarkers();
        Opacity = 1;
        IsHitTestVisible = true;
        _loadedPath = _document.FilePath;
        var filePath = _loadedPath;
        var encoding = _document.Encoding;
        ToolTip = "Loading Mercurial parent revision...";

        try
        {
            // Filesystem checks and Process.Start can block too; keep the entire load off the UI thread.
            var root = await Task.Run(() => MercurialRepository.FindRoot(filePath), token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (_disposed || generation != _generation || token.IsCancellationRequested)
                return;
            Visibility = root == null ? Visibility.Collapsed : Visibility.Visible;
            var baseline = root == null ? null
                : await Task.Run(() => MercurialRepository.LoadAsync(filePath, encoding, token), token);
            if (_disposed || generation != _generation || token.IsCancellationRequested)
                return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
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
                Trace.TraceError("HgInlineDiff: {0}", error);
            }
        }
    }

    private void ScheduleComparison()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _updates.Cancel();
        ClosePopup();
        if (_disposed || _baseline == null)
            return;
        // Keep the last completed markers visible until the replacement is ready.
        _ = CompareAsync();
    }

    private async Task CompareAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_disposed || _baseline == null)
            return;
        var generation = _generation;
        var snapshot = _view.TextBuffer.CurrentSnapshot;
        var baseline = _baseline;

        try
        {
            var hunks = await _updates.CalculateAsync(baseline.Text, snapshot);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (hunks == null || _disposed || generation != _generation || snapshot != _view.TextSnapshot)
                return;
            _snapshot = snapshot;
            _hunks = hunks;
            _changes.Publish(snapshot, hunks);
            ClearMarkers();
            RenderMarkers();
            Opacity = 1;
            IsHitTestVisible = true;
        }
        catch (Exception error)
        {
            if (!_disposed && generation == _generation && snapshot == _view.TextSnapshot)
            {
                _hunks.Clear();
                _changes.Publish(null, _hunks);
                ClearMarkers();
                Opacity = 1;
                IsHitTestVisible = true;
                ToolTip = "Mercurial: " + error.Message;
                Trace.TraceError("HgInlineDiff: {0}", error);
            }
        }
    }

    private void RenderMarkers()
    {
        if (_disposed || _view.IsClosed || _snapshot != _view.TextSnapshot || _view.TextViewLines == null)
            return;

        var placements = MarkerLayout.Calculate(_view, _hunks);
        var visible = new HashSet<DiffHunk>(placements.Select(placement => placement.Hunk));
        foreach (var previous in _markers)
            if (!visible.Contains(previous.Key))
                previous.Value.Visibility = Visibility.Collapsed;
        foreach (var placement in placements)
        {
            var hunk = placement.Hunk;
            var top = placement.Top;
            var height = placement.Height;

            if (_markers.TryGetValue(hunk, out var existing))
            {
                UpdateMarkerPosition(existing, hunk, top, height);
                continue;
            }

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
            // LeftSelectionMargin captures the mouse on button-down unless we handle it first.
            marker.AddHandler(PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler((sender, args) => args.Handled = true), true);
            marker.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((sender, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                ShowPopup(hunk, marker);
                args.Handled = true;
            }), true);
            marker.ContextMenu = ContextMenu;
            marker.AddHandler(PreviewMouseRightButtonDownEvent,
                new MouseButtonEventHandler((sender, args) => args.Handled = true), true);
            marker.AddHandler(PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler((sender, args) =>
            {
                args.Handled = true;
                ContextMenu.PlacementTarget = marker;
                ContextMenu.Placement = PlacementMode.MousePoint;
                ContextMenu.IsOpen = true;
            }), true);
            marker.KeyDown += (sender, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (args.Key == Key.Enter || args.Key == Key.Space)
                {
                    ShowPopup(hunk, marker);
                    args.Handled = true;
                }
            };
            SetLeft(marker, 2.5);
            SetTop(marker, top);
            Children.Add(marker);
            _markers.Add(hunk, marker);
        }
    }

    private void ClearMarkers()
    {
        _markers.Clear();
        Children.Clear();
    }

    private static void UpdateMarkerPosition(Shape marker, DiffHunk hunk, double top, double height)
    {
        marker.Visibility = Visibility.Visible;
        if (GetTop(marker) != top)
            SetTop(marker, top);
        height = Math.Max(2, height);
        if (marker.Height != height)
        {
            marker.Height = height;
            if (hunk.IsDeletion)
                ((Polygon)marker).Points = new PointCollection { new Point(0, 0), new Point(8, height / 2), new Point(0, height) };
        }
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
        ThreadHelper.ThrowIfNotOnUIThread();
        ClosePopup();
        try
        {
            _popup = new ParentTextPopup(_view, _provider, marker, hunk, _baseline, _classification);
        }
        catch (Exception error)
        {
            ToolTip = "Mercurial preview: " + error.Message;
            Trace.TraceError("HgInlineDiff preview: {0}", error);
            return;
        }
        var openedPopup = _popup;
        openedPopup.Closed += (sender, args) =>
        {
            if (ReferenceEquals(_popup, openedPopup))
                _popup = null;
        };
    }

    private void ClosePopup()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var popup = _popup;
        _popup = null;
        popup?.Dispose();
    }

    private void OnCapturedMouseInput(object sender, PreProcessInputEventArgs args)
    {
        if (_disposed || !IsVisible || Mouse.Captured == null
            || !(args.StagingItem.Input is MouseButtonEventArgs mouse)
            || mouse.ButtonState != MouseButtonState.Pressed
            || Window.GetWindow(this)?.IsActive != true)
            return;
        if (Mouse.Captured is Visual captured && _view.VisualElement.IsAncestorOf(captured))
            return;
        var point = mouse.GetPosition(this);
        if (!new Rect(RenderSize).Contains(point) || !(InputHitTest(point) is Shape marker)
            || !_markers.ContainsValue(marker))
            return;
        // Another VS popup owns the press. Release its capture so the release reaches our marker.
        Mouse.Capture(null);
        mouse.Handled = true;
        args.Cancel();
    }
    private void OnTextChanged(object? sender, TextContentChangedEventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ScheduleComparison();
    }
    private void OnLayoutChanged(object? sender, TextViewLayoutChangedEventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
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
    private void OnFormatsChanged(object? sender, FormatItemsEventArgs args)
    {
        foreach (var marker in _markers)
            marker.Value.Fill = GetBrush(marker.Key.IsDeletion ? "HgVS.Deletion" : marker.Key.IsAddition ? "HgVS.Addition" : "HgVS.Modification");
    }
    private void OnRefreshRequested(object? sender, EventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _ = ReloadAsync();
    }
    private void OnFileAction(object? sender, TextDocumentFileActionEventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!string.Equals(_loadedPath, _document.FilePath, StringComparison.OrdinalIgnoreCase))
            _ = ReloadAsync();
    }
    private void OnClosed(object? sender, EventArgs args)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Dispose();
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed)
            return;
        _disposed = true;
        _generation++;
        _loading?.Cancel();
        _loading?.Dispose();
        _updates.Dispose();
        _provider.RefreshRequested -= OnRefreshRequested;
        _view.TextBuffer.Changed -= OnTextChanged;
        _view.LayoutChanged -= OnLayoutChanged;
        _view.Closed -= OnClosed;
        _document.FileActionOccurred -= OnFileAction;
        _formats.FormatMappingChanged -= OnFormatsChanged;
        InputManager.Current.PreProcessInput -= OnCapturedMouseInput;
        ClosePopup();
        ClearMarkers();
    }
}
