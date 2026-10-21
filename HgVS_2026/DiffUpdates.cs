using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;

namespace HgVS_2026;

// Owns debounce, cancellation and serialization; callers only submit immutable editor snapshots.
internal sealed class DiffUpdates : IDisposable
{
    private readonly ITextDifferencingService _differencing;
    private readonly SemaphoreSlim _worker = new SemaphoreSlim(1, 1);
    private readonly object _gate = new object();
    private CancellationTokenSource? _current;
    private bool _disposed;

    public DiffUpdates(ITextDifferencingService differencing) => _differencing = differencing;

    public async Task<List<DiffHunk>?> CalculateAsync(string original, ITextSnapshot snapshot)
    {
        var request = new CancellationTokenSource();
        lock (_gate)
        {
            if (_disposed)
            {
                request.Dispose();
                return null;
            }
            _current?.Cancel();
            _current = request;
        }
        var token = request.Token;
        try
        {
            await Task.Delay(350, token).ConfigureAwait(false);
            await _worker.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (snapshot.Length > MercurialRepository.MaximumFileBytes / sizeof(char))
                        throw new InvalidOperationException("Mercurial margin supports text up to 2 million characters.");
                    var result = LineDiff.Calculate(_differencing, original, snapshot.GetText(), token);
                    token.ThrowIfCancellationRequested();
                    return result;
                }, token).ConfigureAwait(false);
            }
            finally { _worker.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, request))
                    _current = null;
                request.Dispose();
            }
        }
    }

    public void Cancel()
    {
        lock (_gate) { _current?.Cancel(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _current?.Cancel();
        }
        // Pending operations still release the semaphore; it owns no OS wait handle.
    }
}
