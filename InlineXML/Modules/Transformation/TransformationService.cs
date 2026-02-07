using System.Collections.Generic;
using System.IO;
using System.Linq;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.InlineXml;
using InlineXML.Modules.Roslyn;
using Microsoft.CodeAnalysis.Text;

namespace InlineXML.Modules.Transformation;

/// <summary>
/// the transformation service is the orchestrator of the transpilation pipeline.
/// it listens for parsed syntax trees, identifies XCS expressions, and 
/// coordinates the conversion into legal C# while maintaining source fidelity.
/// </summary>
public class TransformationService : AbstractService
{
    public TransformationService()
    {
       // listen for when a file is removed from the workspace.
       // we need to ensure that our generated artifacts don't 
       // outstay their welcome and clutter the user's project.
       Events.Workspace.FileRemoved.AddEventListener(ev =>
       {
          // get the file that's been removed and its corresponding
          // transformation target in the Generated folder.
          var (_, transformed) = ev;

          // if the file didn't have a generated counterpart,
          // there is nothing for us to clean up.
          if (string.IsNullOrEmpty(transformed))
          {
             return ev;
          }
          
          // we delete the stale artifact. the reason we do this here is 
          // simple: if a user deletes an .xcs file in their IDE, we don't 
          // want them to be confused by a 'ghost' .cs file left behind.
          // it keeps the project structure clean and the mental model 
          // of the tool predictable for new users.
          if (File.Exists(transformed))
          {
              File.Delete(transformed);
          }
          
          // contractually return the event for any subsequent listeners.
          return ev;
       });
       
       // this is the main entry point for the transpilation logic.
       // once roslyn has parsed a file, we step in to see if it 
       // contains any of our custom inline XML syntax.
       Events.Roslyn.FileParsed.AddEventListener(ev =>
       {
           // destructure the payload to get the file path and its syntax tree.
           var (file, syntaxTree) = ev;

           // we only care about .xcs files. if this is a standard .cs file,
           // we pass it along without intervention.
           if (!file.EndsWith(".xcs"))
           {
              return ev;
           }
           
           // XCS files allow for declarative tree structures to be defined 
           // directly in C#. while it looks like JSX, our goal is to 
           // remain platform-agnostic, allowing this structure to 
           // represent any tree-based data model.
           var expressions = ExpressionLocator.FindExpressions(syntaxTree);

           // we sort the expressions by their start position in descending order.
           // this is a critical step: since our generated C# might be a 
           // different length than the original XML, replacing text from the 
           // bottom up ensures that the offsets for expressions earlier in 
           // the file remain valid.
           var sortedExpressions = expressions.OrderByDescending(e => e.Start).ToList();
           
           var sourceText = syntaxTree.GetText();
           var currentContent = sourceText.ToString();
           
           // this will aggregate all source maps for every transformed 
           // expression found within this specific file.
           var fileSourceMaps = new List<SourceMapEntry>();

           foreach (var (expressionStart, expressionEnd) in sortedExpressions)
           {
              // extract the raw XML-like string from the source text.
              var span = currentContent.AsSpan(expressionStart, expressionEnd - expressionStart);

              // run the transpilation pipeline:
              // 1. Lexical analysis (Parser)
              // 2. Structural analysis (AstBuilder)
              // 3. Code generation (CodeGenerator)
              var parser = new Parser("Factory", "CreateElement");
              var tokens = parser.Parse(ref span);
              
              var builder = new AstBuilder();
              var ast = builder.Build(tokens, span);
              
              var generator = new CodeGenerator("Factory", "CreateElement");
              var generatedCode = generator.Generate(ast, out var sourceMap);

              // swap the custom syntax with the generated factory calls.
              currentContent = currentContent.Remove(expressionStart, expressionEnd - expressionStart)
                                             .Insert(expressionStart, generatedCode);

              // map the local offsets from the generator back to absolute 
              // file positions so diagnostics can find the original source.
              foreach (var entry in sourceMap)
              {
                 fileSourceMaps.Add(new SourceMapEntry
                 {
                    OriginalStart = expressionStart + entry.OriginalStart,
                    OriginalEnd = expressionStart + entry.OriginalEnd,
                    TransformedStart = expressionStart + entry.TransformedStart,
                    TransformedEnd = expressionStart + entry.TransformedEnd
                 });
              }
           }
           
           // dispatch the final result. the workspace service will pick 
           // this up to handle the actual file I/O.
           Events.Transformer.FileTransformed.Dispatch(new FileTransformedPayload 
           {
               File = file,
               Content = currentContent,
               SourceMaps = fileSourceMaps
           });
           
           return ev;
       });
    }
}

/// <summary>
/// the payload containing the results of a successful file transformation.
/// </summary>
public struct FileTransformedPayload
{
    /// <summary>
    /// the original .xcs file path.
    /// </summary>
    public string File;

    /// <summary>
    /// the newly generated C# content.
    /// </summary>
    public string Content;
    
    /// <summary>
    /// the source maps required to map generated positions back to the original file.
    /// </summary>
    public List<SourceMapEntry> SourceMaps;
}