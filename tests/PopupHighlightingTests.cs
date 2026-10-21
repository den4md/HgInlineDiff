using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HgVS_2026;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

internal static class PopupHighlightingTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        using (var source = new AdhocWorkspace())
        {
            foreach (var language in new[] { LanguageNames.CSharp, LanguageNames.VisualBasic })
            {
                var original = language == LanguageNames.CSharp
                    ? "class Current { string value = \"current\"; }"
                    : "Class Current\nDim value As String = \"current\"\nEnd Class";
                var parent = language == LanguageNames.CSharp
                    ? "Logger.SystemC.Info(oids.Count + \" endcustomers deleted: \" + string.Join(\", \", names.ToArray())); /* old comment */"
                    : "Dim value As String = \"parent\" ' old comment";
                var project = source.AddProject("Highlighting", language);
                var document = source.AddDocument(project.Id, language == LanguageNames.CSharp ? "test.cs" : "test.vb",
                    SourceText.From(original));
                var container = SourceText.From(parent).Container;
                using (var preview = new ParentTextWorkspace(document, container))
                {
                    if (!Workspace.TryGetWorkspace(container, out var registered) || registered != preview
                        || !preview.IsDocumentOpen(document.Id))
                        throw new Exception("FAILED: preview text must be registered as an open Roslyn document");
                    var parentDocument = preview.CurrentSolution.GetDocument(document.Id)!;
                    var spans = (await Classifier.GetClassifiedSpansAsync(parentDocument,
                        new TextSpan(0, parent.Length), CancellationToken.None)).ToList();
                    if (!spans.Any(span => span.ClassificationType == ClassificationTypeNames.Keyword)
                        || !spans.Any(span => span.ClassificationType == ClassificationTypeNames.StringLiteral)
                        || !spans.Any(span => span.ClassificationType == ClassificationTypeNames.Comment))
                        throw new Exception("FAILED: " + language + " preview needs keyword, string, and comment classifications");
                    if ((await source.CurrentSolution.GetDocument(document.Id)!.GetTextAsync()).ToString() != original
                        || (await parentDocument.GetTextAsync()).ToString() != parent)
                        throw new Exception("FAILED: preview must classify parent text without changing the source document");
                    checks += 3;
                }
                if (Workspace.TryGetWorkspace(container, out _))
                    throw new Exception("FAILED: closing preview must release its Roslyn buffer registration");
                checks++;
                Console.WriteLine("Popup highlighting: " + language + " keywords, strings, comments, source isolation, disposal passed.");
            }
        }
        return checks;
    }
}
