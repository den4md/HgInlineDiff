using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace HgInlineDiff;

// Both margins consume the same completed comparison, including while its replacement is pending.
internal sealed class MercurialChangeSet
{
    public ITextSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<DiffHunk> Hunks { get; private set; } = Array.Empty<DiffHunk>();
    public event EventHandler? Changed;

    public static MercurialChangeSet For(ITextView view) =>
        view.Properties.GetOrCreateSingletonProperty(() => new MercurialChangeSet());

    public void Publish(ITextSnapshot? snapshot, IReadOnlyList<DiffHunk> hunks)
    {
        Snapshot = snapshot;
        Hunks = hunks;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
