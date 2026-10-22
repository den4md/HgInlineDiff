using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.Text.Differencing;

namespace HgInlineDiff;

internal sealed class DiffHunk
{
    public int StartLine { get; }
    public int LineCount { get; }
    public string OriginalText { get; }
    public int OriginalStart { get; }
    public bool IsAddition { get; }
    public bool IsDeletion => LineCount == 0;

    public DiffHunk(int startLine, int lineCount, string originalText, bool isAddition, int originalStart = 0)
    {
        StartLine = startLine;
        LineCount = lineCount;
        OriginalText = originalText;
        OriginalStart = originalStart;
        IsAddition = isAddition;
    }
}

internal static class LineDiff
{
    public static List<DiffHunk> Calculate(ITextDifferencingService service,
        string original, string current, CancellationToken cancellationToken)
    {
        // Line-ending conversions are not code changes. Preserve all other whitespace.
        original = original.Replace("\r\n", "\n").Replace('\r', '\n');
        current = current.Replace("\r\n", "\n").Replace('\r', '\n');
        var options = new StringDifferenceOptions
        {
            DifferenceType = StringDifferenceTypes.Line,
            IgnoreTrimWhiteSpace = false,
            ContinueProcessingPredicate = (leftIndex, leftSequence, longestMatch) => !cancellationToken.IsCancellationRequested,
        };
        var differences = service.DiffStrings(original, current, options);
        cancellationToken.ThrowIfCancellationRequested();
        var hunks = new List<DiffHunk>();
        foreach (var difference in differences.Differences)
        {
            var oldSpan = differences.LeftDecomposition.GetSpanInOriginal(difference.Left);
            var newLineCount = difference.Right.Length;
            // The VS service includes a zero-width final line for empty/newline-terminated text.
            if (newLineCount > 0 && difference.Right.End == differences.RightDecomposition.Count
                && (current.Length == 0 || current.EndsWith("\n", System.StringComparison.Ordinal)))
                newLineCount--;
            hunks.Add(new DiffHunk(difference.Right.Start, newLineCount,
                original.Substring(oldSpan.Start, oldSpan.Length), oldSpan.Length == 0, oldSpan.Start));
        }
        return hunks;
    }
}
