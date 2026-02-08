using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.InlineXml;
using InlineXML.Modules.Roslyn;
using Microsoft.CodeAnalysis.Text;

namespace InlineXML.Modules.Transformation;

/// <summary>
/// Transforms XML-embedded C# code (.xcs files) into pure C# code and generates source maps for error reporting.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What This Does (ELI5):</strong>
/// Imagine you write C# code with special XML tags mixed in (like HTML inside JavaScript). This service:
/// <list type="number">
/// <item><description>Finds all the XML parts in your code</description></item>
/// <item><description>Converts the XML into regular C# code that does the same thing</description></item>
/// <item><description>Keeps track of where each piece came from (source maps)</description></item>
/// <item><description>Produces a pure C# file that the compiler can understand</description></item>
/// </list>
/// But here's the tricky part: when the compiler finds errors in the generated C# code, we need to tell the IDE
/// where those errors came from in the ORIGINAL XML-mixed file. That's what source maps do—they map positions
/// backward from generated code to original code.
/// </para>
/// <para>
/// <strong>Key Responsibilities:</strong>
/// <list type="bullet">
/// <item><description>Listens for when Roslyn parses a .xcs file</description></item>
/// <item><description>Locates all XML expressions embedded in the C# code</description></item>
/// <item><description>Parses each XML block and generates equivalent C# code</description></item>
/// <item><description>Creates detailed source maps linking generated positions back to original positions</description></item>
/// <item><description>Cleans up generated files when source files are deleted</description></item>
/// <item><description>Dispatches events so other services can process the transformed code</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>The Source Map Problem (Why This Matters):</strong>
/// When you have this in your .xcs file (line 10):
/// <code>var element = &lt;root&gt;Hello&lt;/root&gt;;</code>
/// It gets transformed into this in the .cs file (line 42):
/// <code>var element = Document.CreateElement("root", "Hello");</code>
/// If the compiler finds an error on line 42 of the .cs file, we need to report it on line 10 of the .xcs file.
/// That's what source maps do—they record the mapping between these positions.
/// </para>
/// </remarks>
public class TransformationService : AbstractService
{
    /// <summary>
    /// Initializes the TransformationService and sets up event listeners for file parsing and deletion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What Happens Here (ELI5):</strong>
    /// The constructor sets up two event listeners:
    /// <list type="number">
    /// <item><description><strong>FileRemoved listener:</strong> When a .xcs source file is deleted, clean up its generated .cs file</description></item>
    /// <item><description><strong>FileParsed listener:</strong> When Roslyn parses a .xcs file, transform it and notify other services</description></item>
    /// </list>
    /// The FileParsed listener does the heavy lifting: it finds all XML expressions, generates C# code,
    /// creates source maps, and dispatches a FileTransformed event with all the results.
    /// </para>
    /// </remarks>
    public TransformationService()
    {
       // ========================================
       // Event 1: File Deletion Handler
       // ========================================
       Events.Workspace.FileRemoved.AddEventListener(ev =>
       {
          var (_, transformed) = ev;
          
          // If there's no generated file path, nothing to clean up
          if (string.IsNullOrEmpty(transformed)) return ev;
          
          // Delete the generated .cs file if it still exists
          // This keeps the Generated/ folder clean when developers delete .xcs files
          if (File.Exists(transformed)) File.Delete(transformed);
          
          return ev;
       });
       
       // ========================================
       // Event 2: File Parsing Handler (Main Logic)
       // ========================================
       Events.Roslyn.FileParsed.AddEventListener(ev =>
       {
           var (file, syntaxTree) = ev;

           // GUARD 1: Skip files in the Generated folder (they're already transformed)
           if (file.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}")) 
           {
              return ev;
           }
           
           // GUARD 2: Skip non-.xcs files (only transform XML-embedded C# files)
           if (!file.EndsWith(".xcs", StringComparison.OrdinalIgnoreCase))
           {
              return ev;
           }
           
           // STEP 1: Find all XML expressions in the parsed C# code
           // ExpressionLocator uses Roslyn's syntax tree to locate <...> blocks
           var expressions = ExpressionLocator.FindExpressions(syntaxTree);
           
           // If there are no XML expressions, no transformation needed
           if (!expressions.Any()) return ev;

           // STEP 2: Extract the original file content and sort XML positions
           var originalContent = syntaxTree.GetText().ToString();
           var sortedExpressions = expressions.OrderBy(e => e.Start).ToList();
           
           // These will hold the pieces of code we weave together
           var finalParts = new List<string>();
           
           // These map positions from generated code back to original code
           // DiagnosticService will use these to translate compiler errors
           var fileSourceMaps = new List<SourceMapEntry>();
           
           // Track our position as we iterate through the file
           int lastPosition = 0;                    // Where we are in the original file
           int currentTransformedOffset = 0;        // Where we are in the generated file

           // STEP 3: Process each XML expression, weaving in surrounding C# code
           foreach (var (expressionStart, expressionEnd) in sortedExpressions)
           {
              // ====================================
              // PHASE 1: WEAVE IN LEADING C# CODE
              // ====================================
              // Everything from the last XML block to this XML block is pure C#
              // We include this verbatim and map it 1:1 (no transformation)
              
              var leadingCSharp = originalContent.Substring(lastPosition, expressionStart - lastPosition);
              if (leadingCSharp.Length > 0)
              {
                  // Map this C# code: same text, same positions (identity mapping)
                  // This tells DiagnosticService: "chars 50-100 in original = chars 50-100 in generated"
                  fileSourceMaps.Add(new SourceMapEntry
                  {
                      OriginalStart = lastPosition,
                      OriginalEnd = expressionStart,
                      TransformedStart = currentTransformedOffset,
                      TransformedEnd = currentTransformedOffset + leadingCSharp.Length
                  });
              }

              finalParts.Add(leadingCSharp);
              currentTransformedOffset += leadingCSharp.Length;

              // ====================================
              // PHASE 2: PARSE AND TRANSFORM XML
              // ====================================
              // Extract the XML block from the original file
              int xmlLength = expressionEnd - expressionStart;
              var xmlText = originalContent.Substring(expressionStart, xmlLength);
              var xmlSpan = xmlText.AsSpan();

              // Step 2a: Tokenize the XML (break it into tokens like OPEN_TAG, TEXT, CLOSE_TAG, etc.)
              var parser = new Parser("Document", "CreateElement");
              var tokens = parser.Parse(ref xmlSpan); 
              
              // Step 2b: Build an Abstract Syntax Tree (AST) from the tokens
              // The AST is a tree structure that represents the XML hierarchy
              var builder = new AstBuilder();
              var ast = builder.Build(tokens, xmlSpan);
              
              // Step 2c: Generate C# code from the AST
              // The CodeGenerator outputs something like: Document.CreateElement("root", "Hello")
              // It also returns a source map showing how pieces of XML map to pieces of generated code
              var generator = new CodeGenerator("Document", "CreateElement");
              var generatedCode = generator.Generate(ast, out var sourceMap);

              // ====================================
              // PHASE 3: WEAVE IN GENERATED CODE
              // ====================================
              finalParts.Add(generatedCode);

              // ====================================
              // PHASE 4: MAP XML TRANSFORMATIONS
              // ====================================
              // The sourceMap from the generator tells us: "XML chars X-Y became generated chars A-B"
              // But these positions are relative to the XML block's start
              // We need to offset them to be relative to the entire file
              
              foreach (var entry in sourceMap)
              {
                 fileSourceMaps.Add(new SourceMapEntry
                 {
                    // Offset original positions by where the XML block started in the file
                    OriginalStart = expressionStart + entry.OriginalStart,
                    OriginalEnd = expressionStart + entry.OriginalEnd,
                    
                    // Offset generated positions by where we are in the output
                    TransformedStart = currentTransformedOffset + entry.TransformedStart,
                    TransformedEnd = currentTransformedOffset + entry.TransformedEnd
                 });
              }

              // Update our position counters for the next iteration
              currentTransformedOffset += generatedCode.Length;
              lastPosition = expressionEnd;
           }

           // ====================================
           // PHASE 5: WEAVE IN TRAILING C# CODE
           // ====================================
           // Everything after the last XML block is pure C# (no transformation)
           if (lastPosition < originalContent.Length)
           {
               var trailingCSharp = originalContent.Substring(lastPosition);
               
               // Map this trailing C# code 1:1
               fileSourceMaps.Add(new SourceMapEntry
               {
                   OriginalStart = lastPosition,
                   OriginalEnd = originalContent.Length,
                   TransformedStart = currentTransformedOffset,
                   TransformedEnd = currentTransformedOffset + trailingCSharp.Length
               });

               finalParts.Add(trailingCSharp);
           }

           // ====================================
           // PHASE 6: ASSEMBLE AND DISPATCH
           // ====================================
           // Join all the parts (leading C#, generated code, trailing C#) into one big string
           var fullContent = string.Join("", finalParts);
           
           // Dispatch the transformation result to all listening services
           // WorkspaceService will save this to disk
           // DiagnosticService will use the source maps to translate compiler errors
           Events.Transformer.FileTransformed.Dispatch(new FileTransformedPayload 
           {
               File = file,
               Content = fullContent,
               SourceMaps = fileSourceMaps
           });
           
           return ev;
       });
    }
}

/// <summary>
/// Contains the output of a file transformation: the generated code and its source maps.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What This Holds (ELI5):</strong>
/// When a .xcs file is transformed, we produce three pieces of information:
/// <list type="number">
/// <item><description><strong>File:</strong> The path to the original .xcs file</description></item>
/// <item><description><strong>Content:</strong> The generated pure C# code</description></item>
/// <item><description><strong>SourceMaps:</strong> A list of position mappings for error translation</description></item>
/// </list>
/// This struct bundles all three together and passes it through the event system to other services.
/// </para>
/// <para>
/// <strong>Example:</strong>
/// Original .xcs file contains (chars 50-100):
/// <code>&lt;root&gt;Hello&lt;/root&gt;</code>
/// Generated .cs file contains (chars 200-250):
/// <code>Document.CreateElement("root", "Hello")</code>
/// The SourceMaps list includes an entry: (OriginalStart: 50, OriginalEnd: 100, TransformedStart: 200, TransformedEnd: 250)
/// This tells DiagnosticService: "If there's an error at position 225 in the generated code, report it at position 75 in the original code."
/// </para>
/// </remarks>
public struct FileTransformedPayload
{
    /// <summary>
    /// The file path of the original .xcs file that was transformed.
    /// </summary>
    /// <remarks>
    /// This is a URI or local file path (depending on the context) pointing to the source file.
    /// Other services use this to track which original file corresponds to the generated output.
    /// </remarks>
    public string File { get; set; }
    
    /// <summary>
    /// The complete generated C# code (pure C# without any XML).
    /// </summary>
    /// <remarks>
    /// This is the full content that will be written to the Generated/.cs file.
    /// It's valid C# that the compiler can understand and analyze.
    /// </remarks>
    public string Content { get; set; }
    
    /// <summary>
    /// List of source maps linking generated code positions back to original code positions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each entry maps a region from the generated code back to the corresponding region in the original code.
    /// DiagnosticService uses these entries to translate compiler error positions.
    /// </para>
    /// <para>
    /// <strong>Map Entry Example:</strong>
    /// If a SourceMapEntry has:
    /// <list type="bullet">
    /// <item><description>OriginalStart: 100, OriginalEnd: 150</description></item>
    /// <item><description>TransformedStart: 500, TransformedEnd: 550</description></item>
    /// </list>
    /// It means: "Characters 100-150 in the original file became characters 500-550 in the generated file."
    /// </para>
    /// <para>
    /// When DiagnosticService gets an error at position 525 in the generated code:
    /// <list type="number">
    /// <item><description>It finds the map entry covering position 525</description></item>
    /// <item><description>It calculates: 525 is 25 chars into the mapped region (525 - 500)</description></item>
    /// <item><description>It applies that offset to the original: 100 + 25 = 125</description></item>
    /// <item><description>It reports the error at position 125 in the original file</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public List<SourceMapEntry> SourceMaps { get; set; }
}