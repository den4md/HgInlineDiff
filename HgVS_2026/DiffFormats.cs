// Adapted from Git Diff Margin's editor format definitions. See THIRD-PARTY-NOTICES.md.
using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace HgVS_2026;

[Export(typeof(EditorFormatDefinition)), Name("HgVS.Addition"), UserVisible(true)]
internal sealed class AdditionFormat : EditorFormatDefinition
{
    public AdditionFormat()
    {
        DisplayName = "Mercurial added lines";
        BackgroundColor = Color.FromRgb(70, 170, 90);
        ForegroundCustomizable = false;
    }
}

[Export(typeof(EditorFormatDefinition)), Name("HgVS.Modification"), UserVisible(true)]
internal sealed class ModificationFormat : EditorFormatDefinition
{
    public ModificationFormat()
    {
        DisplayName = "Mercurial modified lines";
        BackgroundColor = Color.FromRgb(65, 145, 220);
        ForegroundCustomizable = false;
    }
}

[Export(typeof(EditorFormatDefinition)), Name("HgVS.Deletion"), UserVisible(true)]
internal sealed class DeletionFormat : EditorFormatDefinition
{
    public DeletionFormat()
    {
        DisplayName = "Mercurial deleted lines";
        BackgroundColor = Color.FromRgb(225, 85, 85);
        ForegroundCustomizable = false;
    }
}
