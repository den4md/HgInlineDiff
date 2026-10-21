using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HgVS_2026;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

internal static class MarginUiTests
{
    public static int Run()
    {
        // Real WPF elements and routed events; only the VS view/document interfaces are substituted.
        // No IDE, windows, repositories, or physical mouse input are needed for these regressions.
        ITextSnapshot snapshot = null!;
        var lineCount = 5;
        snapshot = InterfaceProxy<ITextSnapshot>.Create(call => call.MethodName switch
        {
            "get_Length" => lineCount * 30,
            "get_LineCount" => lineCount,
            "GetLineFromLineNumber" => MakeLine(snapshot, (int)call.Args[0]),
            "GetLineNumberFromPosition" => Math.Min(lineCount - 1, (int)call.Args[0] / 30),
            _ => null,
        });
        var buffer = InterfaceProxy<ITextBuffer>.Create(call => call.MethodName == "get_CurrentSnapshot" ? snapshot : null);
        var lineVisible = true;
        var firstVisible = 0;
        var lines = InterfaceProxy<IWpfTextViewLineCollection>.Create(call => call.MethodName switch
        {
            "get_FormattedSpan" => lineVisible ? new SnapshotSpan(snapshot, 0, 150) : new SnapshotSpan(snapshot, 120, 30),
            "get_FirstVisibleLine" => MakeViewLine(snapshot, 0),
            "get_LastVisibleLine" => MakeViewLine(snapshot, 4),
            "get_WpfTextViewLines" => new System.Collections.ObjectModel.ReadOnlyCollection<IWpfTextViewLine>(
                Enumerable.Range(lineVisible ? firstVisible : 4, lineVisible ? 5 : 1)
                    .Select(number => MakeViewLine(snapshot, number)).ToList()),
            _ => null,
        });
        var viewElement = new Canvas();
        var positionedLines = 0;
        var view = InterfaceProxy<IWpfTextView>.Create(call =>
        {
            if (call.MethodName == "GetTextViewLineContainingBufferPosition")
            {
                positionedLines++;
                return MakeViewLine(snapshot, ((SnapshotPoint)call.Args[0]).Position / 30);
            }
            return call.MethodName switch
            {
            "get_TextBuffer" => buffer,
            "get_TextSnapshot" => snapshot,
            "get_TextViewLines" => lines,
            "get_ViewportTop" => firstVisible * 20.0,
            "get_ViewportLeft" => 0.0,
            "get_ViewportWidth" => 800.0,
            "get_ViewportHeight" => 100.0,
            "get_LineHeight" => 20.0,
            "get_IsClosed" => false,
            "get_VisualElement" => viewElement,
            _ => null,
            };
        });
        var document = InterfaceProxy<ITextDocument>.Create(call => call.MethodName switch
        {
            "get_FilePath" => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "not-a-mercurial-file.txt"),
            "get_Encoding" => Encoding.UTF8,
            _ => null,
        });
        var formats = InterfaceProxy<IEditorFormatMap>.Create(call => call.MethodName == "GetProperties"
            ? new ResourceDictionary { [EditorFormatDefinition.BackgroundColorId] = Colors.Blue } : null);

        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            using (var margin = new MercurialMargin(view, document, new MercurialMarginProvider(),
                InterfaceProxy<ITextDifferencingService>.Create(call => null), formats,
                InterfaceProxy<IClassificationFormatMap>.Create(call => call.MethodName == "get_DefaultTextProperties"
                    ? TextFormattingRunProperties.CreateTextFormattingRunProperties() : null)))
            {
                margin.Visibility = Visibility.Visible;
                SetField(margin, "_snapshot", snapshot);
                SetField(margin, "_hunks", new List<DiffHunk> { new DiffHunk(1, 1, "parent\n", false) });
                var render = typeof(MercurialMargin).GetMethod("RenderMarkers", BindingFlags.Instance | BindingFlags.NonPublic)!;
                render.Invoke(margin, null);
                var marker = (FrameworkElement)margin.Children[0];
                var host = new Grid();
                host.Children.Add(margin);
                host.Measure(new Size(10, 100));
                host.Arrange(new Rect(0, 0, 10, 100));

                var parentSelection = 0;
                host.MouseDown += (sender, args) => parentSelection++;
                var left = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                };
                marker.RaiseEvent(left);
                var leftIntercepted = left.Handled;
                left.RoutedEvent = UIElement.MouseDownEvent;
                marker.RaiseEvent(left);
                var right = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
                };
                marker.RaiseEvent(right);

                var watch = Stopwatch.StartNew();
                for (var index = 0; index < 1000; index++)
                    render.Invoke(margin, null);
                watch.Stop();
                var reused = ReferenceEquals(marker, margin.Children[0]);
                var measureStable = margin.IsMeasureValid;
                Console.WriteLine("WPF margin: 1000 unchanged renders = " + watch.ElapsedMilliseconds
                    + " ms; marker reused=" + reused + "; measure stable=" + measureStable
                    + "; left intercepted=" + leftIntercepted + "; parent selection=" + parentSelection
                    + "; right intercepted=" + right.Handled);

                Require(leftIntercepted && parentSelection == 0, "marker press must not reach VS line selection");
                Require(right.Handled && marker.ContextMenu == margin.ContextMenu, "marker uses Mercurial context menu");
                Require(reused, "layout must reuse the existing marker");
                Require(measureStable, "unchanged marker layout must not invalidate WPF measurement");

                lineVisible = false;
                render.Invoke(margin, null);
                Require(marker.Visibility == Visibility.Collapsed, "offscreen marker is hidden");
                lineVisible = true;
                render.Invoke(margin, null);
                Require(marker.Visibility == Visibility.Visible && ReferenceEquals(marker, margin.Children[0]), "returning onscreen reuses marker");

                var leftUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
                };
                marker.RaiseEvent(leftUp);
                var popup = (Popup)typeof(MercurialMargin).GetField("_popup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(margin)!;
                var text = (TextBox)((StackPanel)((Border)popup.Child).Child).Children[1];
                Require(leftUp.Handled && popup.IsOpen && text.Text == "parent\n", "left release opens the original-text popup");
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, new TestKeySource(), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                text.RaiseEvent(escape);
                Require(escape.Handled && !popup.IsOpen, "Escape closes popup when its text has focus");

                marker.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                popup = (Popup)typeof(MercurialMargin).GetField("_popup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(margin)!;
                escape = new KeyEventArgs(Keyboard.PrimaryDevice, new TestKeySource(), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                viewElement.RaiseEvent(escape);
                Require(escape.Handled && !popup.IsOpen, "Escape closes popup when focus remains in editor");

                var rightUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
                };
                marker.RaiseEvent(rightUp);
                Require(rightUp.Handled && margin.ContextMenu.IsOpen, "right release opens Mercurial menu");
                margin.ContextMenu.IsOpen = false;

                var hunks = (List<DiffHunk>)typeof(MercurialMargin).GetField("_hunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(margin)!;
                hunks.Add(new DiffHunk(3, 0, "deleted line\n", false));
                lineVisible = false;
                var requestsBefore = positionedLines;
                render.Invoke(margin, null);
                Require(positionedLines == requestsBefore, "offscreen deletions must not request line formatting");

                lineCount = 150000;
                firstVisible = 90000;
                lineVisible = true;
                var manyHunks = new CountingHunks(Enumerable.Range(0, 50000)
                    .Select(index => new DiffHunk(index * 3, 1, "old", false)).ToList());
                var placements = MarkerLayout.Calculate(view, manyHunks);
                Require(placements.Count == 2 && manyHunks.Reads < 40, "layout indexes visible changes instead of scanning 50000 hunks");
                placements = MarkerLayout.Calculate(view, new[] { new DiffHunk(0, lineCount, "old", false) });
                Require(placements.Count == 1 && placements[0].Top == 0 && placements[0].Height == 100
                    && positionedLines == requestsBefore, "whole-file change uses visible lines without forcing offscreen formatting");
                return 13;
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static ITextSnapshotLine MakeLine(ITextSnapshot snapshot, int number) =>
        InterfaceProxy<ITextSnapshotLine>.Create(call => call.MethodName switch
        {
            "get_Start" => new SnapshotPoint(snapshot, number * 30),
            "get_End" => new SnapshotPoint(snapshot, number * 30 + 29),
            "get_EndIncludingLineBreak" => new SnapshotPoint(snapshot, (number + 1) * 30),
            _ => null,
        });

    private static IWpfTextViewLine MakeViewLine(ITextSnapshot snapshot, int number) =>
        InterfaceProxy<IWpfTextViewLine>.Create(call => call.MethodName switch
        {
            "get_Start" => new SnapshotPoint(snapshot, number * 30),
            "get_End" => new SnapshotPoint(snapshot, number * 30 + 29),
            "get_EndIncludingLineBreak" => new SnapshotPoint(snapshot, (number + 1) * 30),
            "get_Top" => number * 20.0,
            "get_Bottom" => (number + 1) * 20.0,
            "get_VisibilityState" => VisibilityState.FullyVisible,
            _ => null,
        });

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception("FAILED: " + message);
    }

    private sealed class TestKeySource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private sealed class CountingHunks : IReadOnlyList<DiffHunk>
    {
        private readonly List<DiffHunk> _hunks;
        public int Reads { get; private set; }
        public CountingHunks(List<DiffHunk> hunks) => _hunks = hunks;
        public int Count => _hunks.Count;
        public DiffHunk this[int index] { get { Reads++; return _hunks[index]; } }
        public IEnumerator<DiffHunk> GetEnumerator() => _hunks.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class InterfaceProxy<T> : RealProxy where T : class
    {
        private readonly Func<IMethodCallMessage, object?> _call;
        private InterfaceProxy(Func<IMethodCallMessage, object?> call) : base(typeof(T)) => _call = call;
        public static T Create(Func<IMethodCallMessage, object?> call) => (T)new InterfaceProxy<T>(call).GetTransparentProxy();
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            try { return new ReturnMessage(_call(call), null, 0, call.LogicalCallContext, call); }
            catch (Exception error) { return new ReturnMessage(error, call); }
        }
    }
}
