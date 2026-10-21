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
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;

namespace HgVS_2026;

internal sealed class MercurialMargin : Canvas, IWpfTextViewMargin
{
    public const string MarginName = "HgVS.MercurialMargin";
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
        _updates = new DiffUpdates(differencing);
        _formats = formats;
        _classification = classification;
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
        _updates.Cancel();
        _baseline = null;
        _hunks.Clear();
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
            if (_disposed || generation != _generation || token.IsCancellationRequested)
                return;
            Visibility = root == null ? Visibility.Collapsed : Visibility.Visible;
            var baseline = root == null ? null
                : await Task.Run(() => MercurialRepository.LoadAsync(filePath, encoding, token), token);
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
        _updates.Cancel();
        ClosePopup();
        if (_disposed || _baseline == null)
            return;
        // Hide stale coordinates without invalidating measurement or rebuilding the visual tree per key.
        Opacity = 0;
        IsHitTestVisible = false;
        _ = CompareAsync();
    }

    private async Task CompareAsync()
    {
        if (_disposed || _baseline == null)
            return;
        var generation = _generation;
        var snapshot = _view.TextBuffer.CurrentSnapshot;
        var baseline = _baseline;

        try
        {
            var hunks = await _updates.CalculateAsync(baseline.Text, snapshot);
            if (hunks == null || _disposed || generation != _generation || snapshot != _view.TextSnapshot)
                return;
            _snapshot = snapshot;
            _hunks = hunks;
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
                ClearMarkers();
                Opacity = 1;
                IsHitTestVisible = true;
                ToolTip = "Mercurial: " + error.Message;
                Trace.TraceError("HgVS: {0}", error);
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
            marker.PreviewMouseLeftButtonDown += (sender, args) => args.Handled = true;
            marker.PreviewMouseLeftButtonUp += (sender, args) =>
            {
                ShowPopup(hunk, marker);
                args.Handled = true;
            };
            marker.ContextMenu = ContextMenu;
            marker.PreviewMouseRightButtonDown += (sender, args) => args.Handled = true;
            marker.PreviewMouseRightButtonUp += (sender, args) =>
            {
                args.Handled = true;
                ContextMenu.PlacementTarget = marker;
                ContextMenu.Placement = PlacementMode.MousePoint;
                ContextMenu.IsOpen = true;
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
        // Popup content lives in another visual tree: keys do not route through the Popup object.
        panel.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPopupKeyDown), true);
        _view.VisualElement.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPopupKeyDown), true);
        var openedPopup = _popup;
        openedPopup.Closed += (sender, args) =>
        {
            if (ReferenceEquals(_popup, openedPopup))
                ClosePopup();
        };
        openedPopup.Opened += (sender, args) => text.Focus();
        _popup.IsOpen = true;
        text.Focus();
    }

    private void OnPopupKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || _popup == null)
            return;
        args.Handled = true;
        ClosePopup();
        _view.VisualElement.Focus();
    }

    private void ClosePopup()
    {
        if (_popup != null)
        {
            var popup = _popup;
            _popup = null;
            _view.VisualElement.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPopupKeyDown));
            popup.IsOpen = false;
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
    private void OnFormatsChanged(object? sender, FormatItemsEventArgs args)
    {
        foreach (var marker in _markers)
            marker.Value.Fill = GetBrush(marker.Key.IsDeletion ? "HgVS.Deletion" : marker.Key.IsAddition ? "HgVS.Addition" : "HgVS.Modification");
    }
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
        _loading?.Cancel();
        _loading?.Dispose();
        _updates.Dispose();
        _provider.RefreshRequested -= OnRefreshRequested;
        _view.TextBuffer.Changed -= OnTextChanged;
        _view.LayoutChanged -= OnLayoutChanged;
        _view.Closed -= OnClosed;
        _document.FileActionOccurred -= OnFileAction;
        _formats.FormatMappingChanged -= OnFormatsChanged;
        ClosePopup();
        ClearMarkers();
    }
}
