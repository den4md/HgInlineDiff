# HgInlineDiff

<img src="assets/logo.svg" width="96" height="96" alt="HgInlineDiff logo">

Mercurial editor change markers, Solution Explorer status icons, and a changes window.
For Visual Studio 2022 and 2026 on Windows x64.

[Download the VSIX](https://github.com/den4md/HgInlineDiff/releases/latest) · [Report an issue](https://github.com/den4md/HgInlineDiff/issues) · [MIT license](LICENSE.txt)

## Install

1. Download and install [TortoiseHg for Windows](https://tortoisehg.bitbucket.io/download/).
   It includes the Mercurial command-line client. A standalone Mercurial installation also works.
2. Open a new terminal and run `hg --version`. If the command is not found, add the directory containing
   `hg.exe` to PATH, usually `C:\Program Files\TortoiseHg`, and check again in a new terminal.
3. Close all Visual Studio instances. Download `HgInlineDiff.vsix` from
   [GitHub Releases](https://github.com/den4md/HgInlineDiff/releases/latest), open it, and select the installed Visual Studio versions.
4. Start Visual Studio and open a tracked text file in a Mercurial working copy.
   HgInlineDiff activates its Source Control provider automatically for Mercurial solutions.
5. Open **View > Other Windows > HgInlineDiff: Changes** to see changed files and click a file to open it in the editor.


## Use

### Editor markers

Changes are compared with the working copy's first parent revision.
Markers beside the code and in the scrollbar show:

- Green for added lines.
- Blue for modified lines.
- Red for deleted lines.

Click a marker to preview the previous text. Copy with Ctrl+C; press Escape to close.
Markers include unsaved edits. Change their colors under **Fonts and Colors** using
`Mercurial added/modified/deleted lines`.

### Solution Explorer

Standard VS icons show clean, changed, and untracked files. A folder shows changes anywhere below it,
including files outside the project. Hover over an icon for the exact status.
Git solutions keep their Git provider. A manual provider choice lasts until the solution changes.

### Changes window

Open **View > Other Windows > HgInlineDiff: Changes**.
The list shows modified, added, removed, missing, and untracked files, sorted by path.
It covers repositories containing the solution or its projects, including binary and non-project files.
Ignored files are excluded.

- Click a file to preview it in the editor.
- Double-click or press Enter to keep it open.
- Files absent from disk do not open.

Unlike editor markers, the list and Solution Explorer reflect saved files on disk.
They update automatically after saves, filesystem changes, and returning to Visual Studio.

## Refresh and troubleshooting

- After an external commit, update, or revert, use **Tools > Refresh Mercurial Changes**.
  The margin's right-click menu and the Changes window's refresh button do the same.
- If Mercurial cannot start, check `hg --version` and PATH, restart VS, then refresh.
  Hover over the margin for the error.
- If another Mercurial extension competes for source control, remove VisualHgMod2.

## Limits

- Editor markers skip untracked, ignored, and binary files. Some embedded language editors are unsupported.
- Editor comparisons are limited to 4 MiB from Mercurial and 2 million UTF-16 characters in the editor.
- Older Windows Mercurial clients may not support all Unicode filenames.

## Build and checks

Use a VS 2026 Developer PowerShell with the **Visual Studio extension development** workload
and .NET Framework 4.7.2 targeting pack installed:

```powershell
msbuild HgInlineDiff.slnx /restore /p:Configuration=Release /p:DeployExtension=false
.\tests\Run-SmokeTests.ps1
```

The VSIX is in `HgInlineDiff/bin/Release/net472/`. Checks require VS and TortoiseHg;
use `-VisualStudioPath` to select another installed IDE.
Open the solution and press F5 to test in the VS Experimental Instance.

Editor integration is adapted from Git Diff Margin. See [third-party notices](THIRD-PARTY-NOTICES.md)
and [license](LICENSE.txt).
