# HgVS Mercurial Margin

Mercurial change markers and parent-text popups in the Visual Studio editor.
Targets Visual Studio 2022 and 2026 on Windows x64. This is the first working prototype.

## Use

1. Install Mercurial or TortoiseHg with `hg.exe` available on PATH, then restart Visual Studio.
2. Install the VSIX or press F5 in Visual Studio 2026 to run the Experimental Instance.
3. Open a tracked text file in a Mercurial working copy.
4. Edit the file. Green marks additions, blue marks modifications, and red triangles mark deletions.
5. Click a marker to view the parent revision's text. Select/copy text normally; press Escape to close.

Unsaved editor changes update after a 350 ms pause. Snapshot text and differences are calculated in the background;
each view runs one comparison at a time and discards superseded results. Marker layout uses only the editor's
existing visible lines, without formatting offscreen lines. The parent text is cached when a file is opened.
After an external commit, update, revert, add, or rename, use **Tools > Refresh Mercurial Changes**.
The same command is available by right-clicking the Mercurial margin.
It refreshes all open Mercurial margins, without saving or changing any files.
Assign a shortcut under Tools > Options > Environment > Keyboard using `HgVS.RefreshMercurialChanges`.

The baseline is the working copy's first parent, not the repository's newest changeset.
Added tracked files are compared with empty text. Untracked, ignored, and binary files are skipped.
Copied and renamed files use the source path from Mercurial's copy metadata.
Line-ending conversions are ignored; other whitespace changes are shown.

## Build

Use Visual Studio 2026 with the **Visual Studio extension development** workload and .NET Framework 4.7.2 targeting pack.
Open `HgVS_2026.slnx`, restore NuGet packages, and build. Building uses the VS 2026 compiler; the resulting extension targets VS 2022 APIs.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe' HgVS_2026.slnx /restore /p:Configuration=Release /p:DeployExtension=false
```

The package is under `HgVS_2026/bin/Release/net472/HgVS_2026.vsix`.

## Prototype limits

- Text up to 4 MiB from Mercurial and 2 million UTF-16 characters in the editor.
- Projection editors, such as embedded language views, are skipped until buffer mapping is implemented.
- Refresh reloads baselines for all open text views, including files whose repository was created after opening them.
- Colors are configurable under Fonts and Colors as `Mercurial added/modified/deleted lines`.
- Filename support follows the installed Mercurial client. Older Windows clients may require filenames representable in the Windows ANSI code page.

## Checks

Run `tests/Run-SmokeTests.ps1` to exercise the actual Visual Studio differencing service and a temporary Mercurial repository.
The script needs Visual Studio and TortoiseHg; it downloads no test framework.
Pass `-VisualStudioPath` to check another installed IDE version.
Fixtures and the compiled test executable stay in the ignored `work/` directory.

The checks cover line markers, old text, EOF deletion, whitespace, encodings, added and renamed files,
parent-versus-tip behavior, refresh after commit, and cancellation. WPF regressions also check marker reuse,
stable layout, large changes outside the viewport, Escape from popup/editor focus, and interception of clicks
before the built-in selection margin. Queue checks cover debounce, background snapshot reads, cancellation,
and serialization of a slow differencing engine. UI placement and popup interaction
still require an Experimental Instance check.

The editor integration is adapted from Git Diff Margin. See THIRD-PARTY-NOTICES.md and LICENSE.txt.
