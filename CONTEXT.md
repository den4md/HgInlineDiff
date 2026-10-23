# Domain glossary

- **Baseline**: a file's text at the working copy's first parent, or empty text for a newly added file. It stays fixed until refresh.
- **Comparison**: differences between the baseline and an immutable snapshot of the current editor text, including unsaved edits.
- **Hunk**: one contiguous addition, deletion, or modification in a comparison.
- **Marker**: a visual indication of a hunk beside the affected editor lines. A deletion marks a boundary between lines.
- **File status**: a file's Mercurial state on disk: clean, modified, added, removed, missing, or untracked. It excludes unsaved editor changes, which are included in the comparison.
- **File change**: a file with a status other than clean, including binary files. Unlike a hunk, it describes the whole file; ignored files are excluded.
- **Changes window**: the list of file changes across repositories containing the solution or its loaded projects, including files outside those projects.
- **Refresh**: the manual command reloads editor baselines and file statuses from Mercurial; automatic status refresh updates only file statuses. Both are distinct from recalculating the comparison after editing text.
- **Source control provider**: the source control integration selected in Visual Studio. HgInlineDiff supplies Mercurial integration and respects manual provider selection; Git solutions use the installed Git provider for Git integration.
