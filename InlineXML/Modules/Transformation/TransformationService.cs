using System.Text;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.InlineXml;
using InlineXML.Modules.Roslyn;

namespace InlineXML.Modules.Transformation;

/// <summary>
/// Orchestrates the end-to-end transformation of <c>.xcs</c> files, surgically replacing 
/// inline XML blocks with generated C# while maintaining byte-perfect source mapping.
/// </summary>
/// <remarks>
/// This service acts as the "Middle Man" between the Roslyn parser and the Code Generator. 
/// it identifies XML-like expressions in standard C# files and swaps them for factory calls, 
/// ensuring that the IDE still knows which line of XML corresponds to which line of generated code.
/// </remarks>
public class TransformationService : AbstractService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransformationService"/> and 
    /// attaches event listeners for workspace and Roslyn lifecycle events.
    /// </summary>
    public TransformationService()
    {
        // Event: Cleanup
        // ELI5: When a user deletes an XML file, we also need to delete the hidden 
        // "Generated" C# file so the project stays clean.
        Events.Workspace.FileRemoved.AddEventListener(ev => {
            var (_, transformed) = ev;
            if (!string.IsNullOrEmpty(transformed) && File.Exists(transformed)) File.Delete(transformed);
            return ev;
        });

        // Event: The Core Transformation Logic
        Events.Roslyn.FileParsed.AddEventListener(ev => {
            var (file, syntaxTree) = ev;

            // ELI5: Only process actual source files (.xcs). 
            // We ignore files that are already in the "Generated" folder to avoid infinite loops.
            if (file.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}") || 
                !file.EndsWith(".xcs", StringComparison.OrdinalIgnoreCase)) return ev;

            // ELI5: Find all the spots in the file where the user wrote XML inside C#.
            var expressions = ExpressionLocator.FindExpressions(syntaxTree).ToList();
            if (!expressions.Any()) return ev;

            var originalContent = syntaxTree.GetText().ToString();
            var sortedExpressions = expressions.OrderBy(e => e.Start).ToList();

            var resultBuilder = new StringBuilder();
            var fileSourceMaps = new List<SourceMapEntry>();
            int lastPosition = 0;
            int currentTransformedOffset = 0;

            for (int i = 0; i < sortedExpressions.Count; i++) {
                var (expressionStart, expressionEnd) = sortedExpressions[i];
                if (expressionStart < lastPosition) continue;

                // --- 1. Map Lead C# ---
                // ELI5: Copy the normal C# code that exists BEFORE the XML block.
                int leadingLen = expressionStart - lastPosition;
                if (leadingLen > 0) {
                    string leadingCSharp = originalContent.Substring(lastPosition, leadingLen);
                    fileSourceMaps.Add(new SourceMapEntry {
                        OriginalStart = lastPosition, OriginalEnd = expressionStart,
                        TransformedStart = currentTransformedOffset, TransformedEnd = currentTransformedOffset + leadingCSharp.Length
                    });
                    resultBuilder.Append(leadingCSharp);
                    currentTransformedOffset += leadingCSharp.Length;
                }

                // --- 2. Map XML Fragment ---
                // ELI5: Isolate the "Raw" chunk of text that looks like ( <div /> ).
                var rawFragment = originalContent.Substring(expressionStart, expressionEnd - expressionStart);
                
                // Logic: Find the actual start of XML within the expression.
                // We handle leading whitespace and the optional opening parenthesis '('.
                int xmlStartOffset = 0;
                while (xmlStartOffset < rawFragment.Length && char.IsWhiteSpace(rawFragment[xmlStartOffset])) xmlStartOffset++;
                if (rawFragment.AsSpan(xmlStartOffset).StartsWith("(")) xmlStartOffset++;
                while (xmlStartOffset < rawFragment.Length && char.IsWhiteSpace(rawFragment[xmlStartOffset])) xmlStartOffset++;

                // Logic: Find the end of the XML by trimming trailing whitespace and ')'.
                int xmlEndOffset = rawFragment.Length - 1;
                while (xmlEndOffset > xmlStartOffset && char.IsWhiteSpace(rawFragment[xmlEndOffset])) xmlEndOffset--;
                if (rawFragment[xmlEndOffset] == ')') xmlEndOffset--;
                while (xmlEndOffset > xmlStartOffset && char.IsWhiteSpace(rawFragment[xmlEndOffset])) xmlEndOffset--;

                int xmlLength = (xmlEndOffset - xmlStartOffset) + 1;
                
                // Fallback: If it's not actually XML, just copy it as is.
                if (xmlLength <= 0) {
                    resultBuilder.Append(rawFragment);
                    currentTransformedOffset += rawFragment.Length;
                    lastPosition = expressionEnd;
                    continue;
                }

                // ELI5: Extract ONLY the XML (e.g., "<div></div>") and send it to our 
                // Parser and Code Generator to turn it into "UI.Create('div')".
                string xmlOnly = rawFragment.Substring(xmlStartOffset, xmlLength);
                var xmlSpan = xmlOnly.AsSpan();
                
                var parser = new Parser("Document", "CreateElement");
                var tokens = parser.Parse(ref xmlSpan);
                var builder = new AstBuilder();
                var ast = builder.Build(tokens, xmlOnly.AsSpan());
                var generator = new CodeGenerator("Document", "CreateElement");
                var generatedCode = generator.Generate(ast, out var localMaps);

                // ELI5: Keep the prefix (like whitespace or the '(') that came before the XML.
                string prefix = rawFragment.Substring(0, xmlStartOffset);
                resultBuilder.Append(prefix);
                currentTransformedOffset += prefix.Length;

                int transformedXmlStart = currentTransformedOffset;
                resultBuilder.Append(generatedCode);

                // ELI5: This is the most important part! We update the "Map" so the computer
                // knows that even though the XML changed into C# code, they are still linked.
                foreach (var local in localMaps) {
                    fileSourceMaps.Add(new SourceMapEntry {
                        OriginalStart = expressionStart + xmlStartOffset + local.OriginalStart,
                        OriginalEnd = expressionStart + xmlStartOffset + local.OriginalEnd,
                        TransformedStart = transformedXmlStart + local.TransformedStart,
                        TransformedEnd = transformedXmlStart + local.TransformedEnd
                    });
                }
                currentTransformedOffset += generatedCode.Length;

                // ELI5: Keep the suffix (like the final ')' and spaces) after the XML.
                string suffix = rawFragment.Substring(xmlStartOffset + xmlLength);
                resultBuilder.Append(suffix);
                currentTransformedOffset += suffix.Length;

                lastPosition = expressionEnd;
            }

            // ELI5: Finally, copy any remaining C# code at the end of the file.
            if (lastPosition < originalContent.Length) {
                string trailingCSharp = originalContent.Substring(lastPosition);
                fileSourceMaps.Add(new SourceMapEntry {
                    OriginalStart = lastPosition, OriginalEnd = originalContent.Length,
                    TransformedStart = currentTransformedOffset, TransformedEnd = currentTransformedOffset + trailingCSharp.Length
                });
                resultBuilder.Append(trailingCSharp);
            }

            // ELI5: Broadcast the final result so other services can save the file or check for errors.
            Events.Transformer.FileTransformed.Dispatch(new FileTransformedPayload {
                File = file, Content = resultBuilder.ToString(), SourceMaps = fileSourceMaps
            });
            return ev;
        });
    }
}