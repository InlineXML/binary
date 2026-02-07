using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Concurrent;

namespace InlineXML.Modules.Diagnostics;

/// <summary>
/// the diagnostic service validates the generated output of our transpiler.
/// it monitors generated C# files, runs the roslyn compiler against them, 
/// and translates any errors back to the original XCS source using 
/// the workspace's reverse-mapping.
/// </summary>
public class DiagnosticService : AbstractService
{
    private readonly WorkspaceService _workspaces;
    
    // holds the translated diagnostics per source file, ready for the debouncer.
    private readonly ConcurrentDictionary<string, List<Diagnostic>> _diagnosticStore = new();

    public DiagnosticService(WorkspaceService workspaces)
    {
       _workspaces = workspaces;

       // the transformer just wrote a .cs file and triggered FileChanged.
       // we pick up that file, find its .xcs creator, and validate it.
       Events.Workspace.FileChanged.AddEventListener(file =>
       {
          // we only care about .cs files for the actual compilation check.
          if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
          {
             // use the workspace to find which .xcs file owns this generated .cs
             var xcsSource = _workspaces.FindOriginalFile(file);
             
             if (xcsSource != null)
             {
                CollectDiagnostics(xcsSource, file);
             }
          }
          
          return file;
       });
    }

    /// <summary>
    /// performs a full roslyn diagnostic pass on the generated C# file.
    /// </summary>
    private void CollectDiagnostics(string xcsFile, string generatedFile)
    {
       var metadata = _workspaces.GetMetadata(xcsFile);
       
       if (metadata == null || string.IsNullOrEmpty(metadata.TransformedContent))
       {
          return;
       }

       // parse the legal C# content sitting in the 'Generated' folder.
       var syntaxTree = CSharpSyntaxTree.ParseText(
          metadata.TransformedContent, 
          path: generatedFile 
       );

       // build the compilation with the project's full context (DLLs/Refs).
       var compilation = CSharpCompilation.Create("InlineXML_Validator")
          .AddReferences(_workspaces.GetProjectReferences())
          .AddSyntaxTrees(syntaxTree);

       // these diagnostics have offsets relative to the generated .cs file.
       var rawDiagnostics = compilation.GetDiagnostics();

       // translate the locations back to the .xcs file for the user.
       UpdateDiagnosticCache(xcsFile, rawDiagnostics, metadata);
    }

    /// <summary>
    /// projects C# errors back onto the original .xcs source using source maps.
    /// </summary>
    private void UpdateDiagnosticCache(string xcsFile, IEnumerable<Diagnostic> diagnostics, FileMetadata metadata)
    {
       var translatedDiagnostics = new List<Diagnostic>();

       foreach (var diagnostic in diagnostics)
       {
          var generatedSpan = diagnostic.Location.SourceSpan;
          
          // find the mapping entry where the C# error occurs.
          var map = metadata.SourceMaps.FirstOrDefault(m => 
             generatedSpan.Start >= m.TransformedStart && 
             generatedSpan.End <= m.TransformedEnd);

          if (map.OriginalStart != 0 || map.OriginalEnd != 0)
          {
             // create a location that points to the original .xcs file.
             var mappedLocation = Location.Create(
                xcsFile, 
                new Microsoft.CodeAnalysis.Text.TextSpan(map.OriginalStart, Math.Max(0, map.OriginalEnd - map.OriginalStart)),
                new Microsoft.CodeAnalysis.Text.LinePositionSpan(
                   new Microsoft.CodeAnalysis.Text.LinePosition(0, 0), 
                   new Microsoft.CodeAnalysis.Text.LinePosition(0, 0))
             );

             var mappedDiagnostic = Diagnostic.Create(
                diagnostic.Descriptor,
                mappedLocation,
                diagnostic.GetMessage()
             );

             translatedDiagnostics.Add(mappedDiagnostic);
          }
          else
          {
             // if unmapped, we keep it as-is so the error isn't swallowed.
             translatedDiagnostics.Add(diagnostic);
          }
       }

       _diagnosticStore[xcsFile] = translatedDiagnostics;
       
       // notify the debouncer that fresh diagnostics are ready for broadcast.
       Events.Diagnostics.FileScanned.Dispatch(xcsFile);
    }
}