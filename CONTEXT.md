# Domain glossary

- **Baseline**: a file's text at the working copy's first parent, or empty text for a newly added file. It stays fixed until refresh.
- **Comparison**: differences between the baseline and an immutable snapshot of the current editor text, including unsaved edits.
- **Hunk**: one contiguous addition, deletion, or modification in a comparison.
- **Marker**: a visual indication of a hunk beside the affected editor lines. A deletion marks a boundary between lines.
- **Refresh**: reload the baseline from Mercurial. This is distinct from recalculating the comparison after editing text.
