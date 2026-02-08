using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.Files;
using InlineXML.Modules.InlineXml;
using InlineXML.Modules.Routing;
using InlineXML.Modules.Workspace;
using Microsoft.CodeAnalysis.Text;
using Diagnostic = InlineXML.Modules.Routing.Diagnostic;
using Range = InlineXML.Modules.Routing.Range;

namespace InlineXML.Modules.Roslyn;

/// <summary>
/// Provides advanced diagnostic analysis for generated C# code by bridging the gap 
/// between Roslyn compilation errors and the original XML source documents.
/// </summary>
/// <remarks>
/// This service monitors file transformations, performs real-time background compilations 
/// using the .NET Compiler Platform (Roslyn), and maps generated code errors back to 
/// their original line/column positions in the source XML via <see cref="SourceMapEntry"/>.
/// </remarks>
public partial class DiagnosticService : AbstractService
{
    private readonly FileService _fileService;
    private readonly RoutingService _router;
    private readonly WorkspaceService _workspaceService;
    
    /// <summary> Caches source mappings keyed by the generated file path. </summary>
    private readonly ConcurrentDictionary<string, (string SourceUri, List<SourceMapEntry> Maps)> _sourceMapCache = new(StringComparer.OrdinalIgnoreCase);
    
    /// <summary> Maintains a virtual project state of <see cref="SyntaxTree"/> objects for compilation. </summary>
    private readonly ConcurrentDictionary<string, SyntaxTree> _projectFiles = new(StringComparer.OrdinalIgnoreCase);
    
    /// <summary> Stores error IDs (e.g., CS1591) to ignore, parsed from project configuration. </summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _suppressedErrors = new(StringComparer.OrdinalIgnoreCase);
    
    /// <summary> Trusted system assemblies required to resolve types during compilation. </summary>
    private readonly List<MetadataReference> _commonReferences;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticService"/>.
    /// </summary>
    /// <param name="fileService">Service for URI/Path resolution.</param>
    /// <param name="router">Service for communicating diagnostics to the client/LSP.</param>
    public DiagnosticService(FileService fileService, RoutingService router, WorkspaceService workspaceService)
    {
        _fileService = fileService;
        _router = router;
        _workspaceService = workspaceService;

        // ELI5: We need to give the compiler "Books" (References) so it knows what 
        // words like 'String' or 'HttpClient' mean. We load every assembly currently 
        // running in this app that isn't a "ghost" (dynamic) assembly.
        _commonReferences = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>().ToList();

        // Register for the "FileTransformed" event. 
        // Whenever the XML is turned into C# code, this logic wakes up.
        Events.Transformer.FileTransformed.AddEventListener(payload =>
        {
            try {
                // 1. Find out where the files live on the actual hard drive.
                string sourceLocal = NormalizePath(_fileService.ToLocalPath(payload.File));
                string projectDir = FindProjectDir(sourceLocal);
                string relativePath = Path.GetRelativePath(projectDir, sourceLocal);
                
                // 2. Predict where the generated C# file was saved.
                string genPath = NormalizePath(Path.Combine(projectDir, "Generated", Path.ChangeExtension(relativePath, ".cs")));
                
                // 3. Store the "Map". This map is like a Rosetta Stone that tells us 
                //    "Line 10 in C# is actually Line 2 in XML".
                _sourceMapCache[genPath] = (payload.File, payload.SourceMaps);
                
                // 4. Check the .csproj file to see if the user wants to hide certain errors.
                LoadProjectSuppressions(projectDir);
                
                // 5. Start the background brain to look for errors.
                _ = RunDiagnosticsAsync(genPath, projectDir);
            } catch (Exception ex) { System.Console.Error.WriteLine($"[DIAG-CACHE-ERR]: {ex.Message}"); }
            return payload;
        });
        
        RegisterCompletionRoute();
    }

    /// <summary>
    /// Executes a full Roslyn compilation on the target file and broadcasts translated 
    /// diagnostics to the connected client.
    /// </summary>
    /// <param name="targetPath">The absolute path to the generated C# file.</param>
    /// <param name="projectDir">The root directory of the project for context.</param>
    public async Task RunDiagnosticsAsync(string targetPath, string projectDir)
    {
        try {
            targetPath = NormalizePath(targetPath);
            if (!File.Exists(targetPath)) return;

            // ELI5: Step 1 - Read the code and turn it into a "Syntax Tree".
            // A Syntax Tree is how a computer sees code—it's like a family tree 
            // of all the brackets, variables, and semicolons.
            string genCode = await File.ReadAllTextAsync(targetPath);
            var tree = CSharpSyntaxTree.ParseText(genCode, path: targetPath);
            var root = await tree.GetRootAsync();
            _projectFiles[targetPath] = tree;

            // ELI5: Step 2 - Gather all other .cs files in the folder.
            // You can't check one file in isolation if it uses classes from another file.
            await EnsureProjectLoaded(projectDir);

            // ELI5: Step 3 - Create a "Virtual Compiler".
            // We tell Roslyn: "Pretend you're a real compiler, look at all these files, 
            // and tell me if anything is broken."
            var compilation = CSharpCompilation.Create("LspProject")
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddReferences(_commonReferences).AddSyntaxTrees(_projectFiles.Values);

            // ELI5: Step 4 - Get the list of errors for JUST the file we are looking at.
            var roslynDiagnostics = compilation.GetDiagnostics()
                .Where(d => d.Location.SourceTree?.FilePath != null && NormalizePath(d.Location.SourceTree.FilePath) == targetPath);

            if (!_sourceMapCache.TryGetValue(targetPath, out var mapInfo)) return;

            // Load the original XML text so we can calculate the right character positions.
            SourceText sourceText;
            using (var stream = new FileStream(_fileService.ToLocalPath(mapInfo.SourceUri), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                sourceText = SourceText.From(stream);
            
            var translatedDiagnostics = new List<Diagnostic>();
            _suppressedErrors.TryGetValue(projectDir, out var projectSuppressions);

            foreach (var d in roslynDiagnostics)
            {
                // ELI5: Step 5 - Ignore errors the user doesn't care about.
                if (projectSuppressions != null && projectSuppressions.Contains(d.Id)) continue;

                int genStartPos = d.Location.SourceSpan.Start;
                var node = root.FindNode(d.Location.SourceSpan);
                
                // Logic: Check if the error is inside a property container (Props).
                // If it is, we usually want to highlight the XML Tag itself.
                var propContainer = node.AncestorsAndSelf().OfType<ObjectCreationExpressionSyntax>()
                    .FirstOrDefault(x => x.Type.ToString().EndsWith("Props"));

                int lookupPos = (propContainer != null) ? propContainer.SpanStart : genStartPos;

                // ELI5: Step 6 - Use the "Rosetta Stone" (Map).
                // We find the piece of code that fits our error position.
                // We pick the "smallest" map entry because it's the most specific one.
                var entry = mapInfo.Maps
                    .Where(m => lookupPos >= m.TransformedStart && lookupPos <= m.TransformedEnd)
                    .OrderBy(m => m.TransformedEnd - m.TransformedStart) 
                    .FirstOrDefault();

                // Fallback: If we didn't find a perfect box, find the closest starting point before us.
                if (entry.TransformedEnd == 0) {
                    entry = mapInfo.Maps.Where(m => m.TransformedStart <= lookupPos)
                        .OrderByDescending(m => m.TransformedStart).FirstOrDefault();
                }

                if (entry.TransformedEnd != 0) {
                    bool isPropError = propContainer != null;
                    int rel = isPropError ? 0 : Math.Max(0, lookupPos - entry.TransformedStart);
                    int origPos = Math.Max(0, Math.Min(entry.OriginalStart + rel, sourceText.Length));

                    // ELI5: Step 7 - Decide how much text to highlight.
                    // If it's a property error, highlight the tag name (e.g., <Button>).
                    // Otherwise, just use the width Roslyn gave us.
                    int finalWidth = isPropError ? GetTagNameWidth(sourceText, origPos) : Math.Max(1, d.Location.SourceSpan.Length);
                    var startLinePos = sourceText.Lines.GetLinePosition(origPos);
                    var endLinePos = sourceText.Lines.GetLinePosition(Math.Min(origPos + finalWidth, sourceText.Length));

                    // ELI5: Step 8 - Build the "Ticket" to send to the IDE (VS Code/Visual Studio).
                    translatedDiagnostics.Add(new Diagnostic {
                        Range = new Range {
                            Start = new Position { Line = startLinePos.Line, Character = startLinePos.Character },
                            End = new Position { Line = endLinePos.Line, Character = endLinePos.Character }
                        },
                        Message = $"[{d.Id}] {d.GetMessage()}",
                        Severity = d.Severity == DiagnosticSeverity.Error ? 1 : 2,
                        Source = "Roslyn"
                    });
                }
            }

            // ELI5: Step 9 - "Mail" the results to the user's screen.
            _router.SendNotification("textDocument/publishDiagnostics", new PublishDiagnosticsParams {
                Uri = _fileService.ToUri(_fileService.ToLocalPath(mapInfo.SourceUri)),
                Diagnostics = translatedDiagnostics
            });
        } catch (Exception ex) { System.Console.Error.WriteLine($"[DIAG-FATAL]: {ex.Message}"); }
    }

    /// <summary>
    /// Calculates the visual width of an XML tag starting from a specific position.
    /// </summary>
    /// <returns>The number of characters to highlight.</returns>
    private int GetTagNameWidth(SourceText text, int startPos) {
        string content = text.ToString();
        if (startPos >= content.Length) return 1;
        int current = startPos;
        if (content[current] == '<') current++;
        while (current < content.Length && (char.IsLetterOrDigit(content[current]) || content[current] == '_' || content[current] == '.')) current++;
        int width = current - startPos;
        return width > 0 ? width : 1;
    }

    /// <summary>
    /// Parses the project's .csproj file to extract &lt;NoWarn&gt; settings.
    /// </summary>
    /// <param name="projectDir">The directory containing the .csproj file.</param>
    private void LoadProjectSuppressions(string projectDir) {
        if (_suppressedErrors.ContainsKey(projectDir)) return;
        var csproj = Directory.GetFiles(projectDir, "*.csproj").FirstOrDefault();
        if (csproj == null) return;
        var suppressions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            var doc = XDocument.Load(csproj);
            // ELI5: Look for the <NoWarn> tag in the XML file. It usually looks like 1234;5678.
            var noWarn = doc.Descendants("NoWarn").Select(x => x.Value).FirstOrDefault();
            if (!string.IsNullOrEmpty(noWarn)) {
                foreach (var id in noWarn.Split(';', ',')) {
                    var cleanId = id.Trim();
                    // Normalize to CSxxxx format.
                    if (!cleanId.StartsWith("CS")) cleanId = "CS" + cleanId;
                    suppressions.Add(cleanId);
                }
            }
            _suppressedErrors[projectDir] = suppressions;
        } catch { _suppressedErrors[projectDir] = new HashSet<string>(); }
    }

    /// <summary>
    /// Crawls the project directory to ensure all relevant C# files are loaded into 
    /// the syntax tree cache for cross-file reference resolution.
    /// </summary>
    private async Task EnsureProjectLoaded(string projectDir) {
        if (string.IsNullOrEmpty(projectDir) || _projectFiles.Count > 100) return;
        var files = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories);
        foreach (var file in files) {
            string norm = NormalizePath(file);
            // We ignore files we created (Generated folder) so we don't get circular logic.
            if (norm.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}")) continue;
            if (!_projectFiles.ContainsKey(norm)) 
                _projectFiles[norm] = CSharpSyntaxTree.ParseText(File.ReadAllText(norm), path: norm);
        }
    }

    /// <summary> Standardizes file paths for consistent dictionary lookups. </summary>
    private string NormalizePath(string path) => Path.GetFullPath(path).ToLowerInvariant();

    /// <summary>
    /// Traverses up the directory tree to find the nearest .csproj file.
    /// </summary>
    private string FindProjectDir(string startPath) {
        var dir = Path.GetDirectoryName(startPath);
        while (dir != null) {
            if (Directory.GetFiles(dir, "*.csproj").Any()) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.GetDirectoryName(startPath) ?? "";
    }
}