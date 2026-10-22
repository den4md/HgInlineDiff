// Marker placement adapted from Git Diff Margin's EditorDiffMargin.cs.
// Copyright (c) 2012-2020 Laurent Kempé. See THIRD-PARTY-NOTICES.md and LICENSE.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace HgInlineDiff;

internal readonly struct MarkerPlacement
{
    public DiffHunk Hunk { get; }
    public double Top { get; }
    public double Height { get; }
    public MarkerPlacement(DiffHunk hunk, double top, double height)
    {
        Hunk = hunk;
        Top = top;
        Height = height;
    }
}

internal static class MarkerLayout
{
    public static List<MarkerPlacement> Calculate(IWpfTextView view, IReadOnlyList<DiffHunk> hunks)
    {
        var result = new List<MarkerPlacement>();
        var snapshot = view.TextSnapshot;
        // Never ask the view to format a line. Layout must consume its existing formatted lines only.
        var lines = view.TextViewLines.WpfTextViewLines.Where(line =>
            line.VisibilityState != VisibilityState.Unattached
            && line.Bottom > view.ViewportTop && line.Top < view.ViewportTop + view.ViewportHeight).ToList();
        if (lines.Count == 0)
            return result;

        var first = snapshot.GetLineNumberFromPosition(lines[0].Start.Position);
        var last = snapshot.GetLineNumberFromPosition(lines[lines.Count - 1].End.Position);
        var low = 0;
        var high = hunks.Count;
        // VS differences are sorted and non-overlapping. Skip changes above the viewport in O(log n).
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var end = hunks[middle].StartLine + Math.Max(0, hunks[middle].LineCount - 1);
            if (end < first) low = middle + 1;
            else high = middle;
        }

        for (var index = low; index < hunks.Count; index++)
        {
            var hunk = hunks[index];
            if (hunk.StartLine > last + 1)
                break;
            if (hunk.StartLine < 0 || hunk.StartLine > snapshot.LineCount)
                continue;
            if (hunk.IsDeletion)
            {
                var atEnd = hunk.StartLine == snapshot.LineCount;
                var anchor = atEnd ? snapshot.Length : snapshot.GetLineFromLineNumber(hunk.StartLine).Start.Position;
                var line = lines.FirstOrDefault(candidate => candidate.Start.Position <= anchor
                    && (candidate.EndIncludingLineBreak.Position > anchor
                        || anchor == snapshot.Length && candidate.EndIncludingLineBreak.Position == anchor));
                if (line != null)
                    result.Add(new MarkerPlacement(hunk,
                        (atEnd ? line.Bottom : line.Top) - view.ViewportTop - view.LineHeight / 2, view.LineHeight));
                continue;
            }

            var endNumber = hunk.StartLine + hunk.LineCount - 1;
            if (endNumber >= snapshot.LineCount)
                continue;
            var startPosition = snapshot.GetLineFromLineNumber(hunk.StartLine).Start.Position;
            var endPosition = snapshot.GetLineFromLineNumber(endNumber).End.Position;
            ITextViewLine? firstLine = null;
            ITextViewLine? lastLine = null;
            foreach (var line in lines)
            {
                if (line.End.Position < startPosition || line.Start.Position > endPosition)
                    continue;
                firstLine = firstLine ?? line;
                lastLine = line;
            }
            if (firstLine != null && lastLine != null)
                result.Add(new MarkerPlacement(hunk, firstLine.Top - view.ViewportTop,
                    Math.Max(2, lastLine.Bottom - firstLine.Top)));
        }
        return result;
    }
}
