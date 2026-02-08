using InlineXML.Configuration;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.InlineXml;
using InlineXML.Modules.Roslyn;
using InlineXML.Modules.Transformation;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;

namespace InlineXML.Modules.Development;

public class AppDevService : AbstractService
{
    public AppDevService()
    {
       Events.AfterAllServicesReady.AddEventListener(mode =>
       {
           if (mode is ExecutionMode.DeveloperMode) Main();
           return mode;
       });
    }

    private void Main()
    {
        var xcsCode = @"
namespace SevenUI.ExtendedSharp.Examples;
using XMLTest;
public static class Component
{
    public static FunctionalComponent<NodeProps> Loading = (props, children) => {
        return (
           <div className=""loading"">
                <p>Loading...</p>
            </div>
        );
    };

    public static FunctionalComponent<UserComponentProps> UserComponent = (props, children) => {
        var id = 1;
        return (
            <div id={""user-"" + id}>
                <p>Hello</p>
            </div>
        );
    };
}";

        var syntaxTree = CSharpSyntaxTree.ParseText(xcsCode);
        var xmlExpressions = ExpressionLocator.FindExpressions(syntaxTree).ToList();
    
        var resultBuilder = new StringBuilder();
        var globalSourceMaps = new List<SourceMapEntry>();
        int lastPosition = 0;
        int currentTransformedOffset = 0;

        foreach (var (start, end) in xmlExpressions)
        {
            // 1. MAP THE LEADING C# (The Identity Chunk)
            // This maps the code between XML blocks (including namespaces, class defs, etc.)
            string leadingCs = xcsCode.Substring(lastPosition, start - lastPosition);
            if (!string.IsNullOrEmpty(leadingCs))
            {
                globalSourceMaps.Add(new SourceMapEntry
                {
                    OriginalStart = lastPosition,
                    OriginalEnd = start,
                    TransformedStart = currentTransformedOffset,
                    TransformedEnd = currentTransformedOffset + leadingCs.Length
                });
                resultBuilder.Append(leadingCs);
                currentTransformedOffset += leadingCs.Length;
            }

            // 2. TRANSFORM XML
            var rawFragment = xcsCode.Substring(start, end - start);
            var trimmedFragment = rawFragment.Trim();
            
            // Calculate internal shift caused by .Trim() and the opening '('
            int internalXmlOffset = rawFragment.IndexOf(trimmedFragment) + 1; 
            
            var xmlOnly = trimmedFragment.Substring(1, trimmedFragment.Length - 2);
            var xmlSpan = xmlOnly.AsSpan();

            var parser = new Parser("Document", "CreateElement");
            var tokens = parser.Parse(ref xmlSpan);

            var builder = new AstBuilder();
            var ast = builder.Build(tokens, xmlOnly.AsSpan());

            var generator = new CodeGenerator("Document", "CreateElement");
            var generatedCode = generator.Generate(ast, out var localMaps);

            // 3. WEAVE AND MAP GENERATED CODE
            resultBuilder.Append("(");
            int codeStartInFile = currentTransformedOffset + 1;
            resultBuilder.Append(generatedCode);
            resultBuilder.Append(")");

            foreach (var local in localMaps)
            {
                globalSourceMaps.Add(new SourceMapEntry
                {
                    OriginalStart = start + internalXmlOffset + local.OriginalStart,
                    OriginalEnd = start + internalXmlOffset + local.OriginalEnd,
                    TransformedStart = codeStartInFile + local.TransformedStart,
                    TransformedEnd = codeStartInFile + local.TransformedEnd
                });
            }

            // Update trackers (+2 for the added parentheses)
            currentTransformedOffset += (generatedCode.Length + 2);
            lastPosition = end;
        }

        // 4. MAP THE TRAILING C#
        if (lastPosition < xcsCode.Length)
        {
            string trailingCs = xcsCode.Substring(lastPosition);
            globalSourceMaps.Add(new SourceMapEntry
            {
                OriginalStart = lastPosition,
                OriginalEnd = xcsCode.Length,
                TransformedStart = currentTransformedOffset,
                TransformedEnd = currentTransformedOffset + trailingCs.Length
            });
            resultBuilder.Append(trailingCs);
        }

        // --- OUTPUT FOR VERIFICATION ---
        System.Console.WriteLine("--- TRANSFORMED CODE ---");
        System.Console.WriteLine(resultBuilder.ToString());
        
        System.Console.WriteLine("\n--- FULL SOURCE MAPS (TOTAL COVERAGE) ---");
        foreach (var map in globalSourceMaps.OrderBy(m => m.TransformedStart))
        {
            // Calculate a snippet of the original content to verify the range
            int len = Math.Min(20, map.OriginalEnd - map.OriginalStart);
            string snippet = xcsCode.Substring(map.OriginalStart, len).Replace("\r", "").Replace("\n", "\\n");
            
            System.Console.WriteLine($"Trans: {map.TransformedStart,4}..{map.TransformedEnd,-4} | Orig: {map.OriginalStart,4}..{map.OriginalEnd,-4} | Snippet: [{snippet}]");
        }
        
        System.Console.WriteLine($"\nOriginal Length: {xcsCode.Length}");
        System.Console.WriteLine($"Transformed Length: {resultBuilder.Length}");
        System.Console.WriteLine($"Total Map Count: {globalSourceMaps.Count}");
    }
}