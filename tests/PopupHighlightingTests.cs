using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HgInlineDiff;
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
                var fragment = language == LanguageNames.CSharp
                    ? "        List<string> copy = argument;\n        copy.Add(local + \"parent\"); /* old comment */\n"
                    : "        Dim copy As List(Of String) = argument\n        copy.Add(local & \"parent\") ' old comment\n";
                var parent = language == LanguageNames.CSharp
                    ? "using System.Collections.Generic;\nclass Parent {\n    void Method(List<string> argument) {\n        var local = \"prefix\";\n"
                        + fragment + "    }\n}\n"
                    : "Imports System.Collections.Generic\nClass Parent\n    Sub Method(argument As List(Of String))\n        Dim local = \"prefix\"\n"
                        + fragment + "    End Sub\nEnd Class\n";
                var original = parent.Replace("parent", "current");
                var project = source.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
                    "Highlighting", "Highlighting", language,
                    metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }));
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
                        new TextSpan(parent.IndexOf(fragment, StringComparison.Ordinal), fragment.Length), CancellationToken.None)).ToList();
                    if (!spans.Any(span => span.ClassificationType == ClassificationTypeNames.Keyword)
                        || !spans.Any(span => span.ClassificationType == ClassificationTypeNames.StringLiteral)
                        || !spans.Any(span => span.ClassificationType == ClassificationTypeNames.Comment))
                        throw new Exception("FAILED: " + language + " preview needs keyword, string, and comment classifications");
                    if (!spans.Any(span => span.ClassificationType == ClassificationTypeNames.ClassName
                            && parent.Substring(span.TextSpan.Start, span.TextSpan.Length) == "List")
                        || !spans.Any(span => span.ClassificationType == ClassificationTypeNames.ParameterName
                            && parent.Substring(span.TextSpan.Start, span.TextSpan.Length) == "argument")
                        || !spans.Any(span => span.ClassificationType == ClassificationTypeNames.LocalName
                            && parent.Substring(span.TextSpan.Start, span.TextSpan.Length) == "local"))
                        throw new Exception("FAILED: " + language + " fragment needs imports, method parameters, and preceding local declarations");
                    if ((await source.CurrentSolution.GetDocument(document.Id)!.GetTextAsync()).ToString() != original
                        || (await parentDocument.GetTextAsync()).ToString() != parent)
                        throw new Exception("FAILED: preview must classify parent text without changing the source document");
                    checks += 4;
                }
                if (Workspace.TryGetWorkspace(container, out _))
                    throw new Exception("FAILED: closing preview must release its Roslyn buffer registration");
                checks++;
                Console.WriteLine("Popup highlighting: " + language + " syntax, imports, parameters, locals, source isolation, disposal passed.");
            }
        }
        return checks;
    }
}
