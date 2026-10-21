using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HgVS_2026;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;

internal static class DiffUpdateTests
{
    public static async Task<int> RunAsync(ITextDifferencingService service)
    {
        var callerThread = Thread.CurrentThread.ManagedThreadId;
        var textThread = callerThread;
        var textReads = 0;
        var snapshot = MarginUiTests.InterfaceProxy<ITextSnapshot>.Create(call => call.MethodName switch
        {
            "get_Length" => 8,
            "GetText" => ReadText(),
            _ => null,
        });
        string ReadText()
        {
            Interlocked.Increment(ref textReads);
            textThread = Thread.CurrentThread.ManagedThreadId;
            return "changed\n";
        }

        using (var updates = new DiffUpdates(service))
        {
            var burst = Enumerable.Range(0, 10).Select(_ => updates.CalculateAsync("old\n", snapshot)).ToArray();
            var results = await Task.WhenAll(burst);
            Require(results.Take(9).All(result => result == null) && results[9]?.Count == 1,
                "rapid edits publish only the latest comparison");
            Require(textReads == 1 && textThread != callerThread,
                "debounce materializes one snapshot off the caller's editor thread");
            var cancelled = updates.CalculateAsync("old\n", snapshot);
            updates.Cancel();
            Require(await cancelled == null, "refresh cancels a pending comparison");
        }

        // Deliberately ignore cancellation inside the first diff to test a slow/uncooperative engine.
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var calls = 0;
            var slowService = MarginUiTests.InterfaceProxy<ITextDifferencingService>.Create(call =>
            {
                if (call.MethodName != "DiffStrings")
                    throw new InvalidOperationException(call.MethodName);
                var number = Interlocked.Increment(ref calls);
                if (number == 1)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Slow diff was not released.");
                }
                return service.DiffStrings((string)call.Args[0], (string)call.Args[1],
                    (StringDifferenceOptions)call.Args[2]);
            });
            using (var updates = new DiffUpdates(slowService))
            {
                var first = updates.CalculateAsync("old\n", snapshot);
                Task<System.Collections.Generic.List<DiffHunk>?>? second = null;
                try
                {
                    Require(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "first comparison starts");
                    second = updates.CalculateAsync("old\n", snapshot);
                    await Task.Delay(500);
                    Require(Volatile.Read(ref calls) == 1, "new edits do not overlap a still-running diff");
                }
                finally { release.Set(); }
                Require(await first == null && second != null && (await second)?.Count == 1 && calls == 2,
                    "stale running result is discarded and the latest queued edit completes");
            }
        }

        var disposed = new DiffUpdates(service);
        var pending = disposed.CalculateAsync("old\n", snapshot);
        disposed.Dispose();
        Require(await pending == null && await disposed.CalculateAsync("old\n", snapshot) == null,
            "closing a view cancels pending work and rejects further updates");
        Console.WriteLine("Diff queue: latest edit wins; snapshot read off editor thread; slow diffs serialized.");
        return 7;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception("FAILED: " + message);
    }
}
