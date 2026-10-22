# Third-party notices

HgInlineDiff adapts editor code from [Git Diff Margin](https://github.com/laurentkempe/GitDiffMargin),
commit `e3d5b8268a87527f44f4378297bf48a63ea80803`.

Copyright (c) 2012-2020 Laurent Kempé. Licensed under the MIT license reproduced in LICENSE.txt.

Adapted parts:

- `GitDiffMargin.Shared/EditorDiffMargin.cs`: marker positioning in `MarkerLayout.cs` and editor interaction in `MercurialMargin.cs`.
- `GitDiffMargin.Shared/EditorDiffMarginFactory.cs`: MEF registration and margin placement in `MercurialMarginProvider.cs`.
- `GitDiffMargin.Shared/Settings/*EditorFormatDefinition.cs`: editor color definitions in `DiffFormats.cs`.

The implementation uses Visual Studio's text differencing service and a new Mercurial command-line adapter.
