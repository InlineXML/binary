using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.Files;
using InlineXML.Modules.InlineXml;
using InlineXML.Modules.Routing;
using Microsoft.CodeAnalysis.Text;
using Diagnostic = InlineXML.Modules.Routing.Diagnostic;
using Range = InlineXML.Modules.Routing.Range;

namespace InlineXML.Modules.Roslyn;

/// <summary>
/// Manages C# code analysis and diagnostics using Roslyn, translating compiler errors back to their source locations.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What This Does (ELI5):</strong>
/// Imagine you write code in a special XML format, and a tool converts it to normal C# code. But when the C# compiler finds errors,
/// it reports them using the generated code's line numbers. This service takes those error locations and translates them back to
/// your original XML source file, so you see errors exactly where you wrote them.
/// </para>
/// <para>
/// <strong>Key Responsibilities:</strong>
/// <list type="bullet">
/// <item><description>Listens for when files are transformed from XML to C#</description></item>
/// <item><description>Runs the C# compiler on the generated code</description></item>
/// <item><description>Maps compiler errors back to their original source locations using source maps</description></item>
/// <item><description>Respects compiler error suppressions from .csproj files</description></item>
/// <item><description>Sends error notifications to the IDE/editor via LSP protocol</description></item>
/// </list>
/// </para>
/// </remarks>
public class DiagnosticService : AbstractService
{
    private readonly FileService _fileService;
    private readonly RoutingService _router;
    
    /// <summary>
    /// Cache mapping generated file paths to their source file URI and source maps.
    /// Source maps allow us to translate line/character positions from generated code back to the original source.
    /// Key: normalized path to generated .cs file, Value: (original source URI, list of position mappings).
    /// </summary>
    private readonly ConcurrentDictionary<string, (string SourceUri, List<SourceMapEntry> Maps)> _sourceMapCache = 
        new(StringComparer.OrdinalIgnoreCase);
    
    /// <summary>
    /// Parsed syntax trees for all C# files in the project.
    /// This allows Roslyn to analyze code relationships and build a complete compilation context.
    /// Key: normalized file path, Value: parsed syntax tree.
    /// </summary>
    private readonly ConcurrentDictionary<string, SyntaxTree> _projectFiles = 
        new(StringComparer.OrdinalIgnoreCase);
    
    /// <summary>
    /// Tracks compiler error codes that should be ignored for each project.
    /// Read from the NoWarn property in .csproj files (e.g., "CS0618,CS0219").
    /// Key: project directory path, Value: set of error codes to suppress (e.g., "CS0618").
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _suppressedErrors = 
        new(StringComparer.OrdinalIgnoreCase);
    
    /// <summary>
    /// Metadata references for the current .NET runtime and loaded assemblies.
    /// These are required by Roslyn to understand available types and APIs when analyzing code.
    /// </summary>
    private readonly List<MetadataReference> _commonReferences;

    /// <summary>
    /// Initializes the DiagnosticService and sets up event listeners for code transformations.
    /// </summary>
    /// <param name="fileService">Service for file I/O operations and path normalization.</param>
    /// <param name="router">Service for sending LSP notifications (errors, warnings) to the client.</param>
    /// <remarks>
    /// <strong>What Happens Here (ELI5):</strong>
    /// This constructor does three things:
    /// <list type="number">
    /// <item><description>Saves references to the file and routing services for later use</description></item>
    /// <item><description>Collects all the .NET framework assemblies so Roslyn can look up type information</description></item>
    /// <item><description>Subscribes to file transformation events, so whenever a file is converted from XML to C#, we automatically run diagnostics on it</description></item>
    /// </list>
    /// </remarks>
    public DiagnosticService(FileService fileService, RoutingService router)
    {
        _fileService = fileService;
        _router = router;

        // Load all available .NET assemblies as metadata references so Roslyn can resolve types
        _commonReferences = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        // Subscribe to the FileTransformed event: whenever a file is transformed, run diagnostics on it
        Events.Transformer.FileTransformed.AddEventListener(payload =>
        {
            try 
            {
                // Convert the file path to a local path and normalize it (remove .., make lowercase, etc.)
                string sourceLocal = NormalizePath(_fileService.ToLocalPath(payload.File));
                
                // Find the project root directory (the one containing .csproj)
                string projectDir = FindProjectDir(sourceLocal);
                
                // Calculate where the generated C# file should be: Project/Generated/RelativePath.cs
                string relativePath = Path.GetRelativePath(projectDir, sourceLocal);
                string genPath = NormalizePath(Path.Combine(projectDir, "Generated", Path.ChangeExtension(relativePath, ".cs")));
                
                // Store the mapping: generated file path → original source file + source maps
                _sourceMapCache[genPath] = (payload.File, payload.SourceMaps);
                
                // Load any error suppressions from the project's .csproj file (only once per project)
                LoadProjectSuppressions(projectDir);

                // Run the C# compiler on the generated file to find errors
                _ = RunDiagnosticsAsync(genPath, projectDir);
            }
            catch (Exception ex) { System.Console.Error.WriteLine($"[DIAG-CACHE-ERR]: {ex.Message}"); }
            return payload;
        });
    }

    /// <summary>
    /// Loads suppressed error codes from the project's .csproj file.
    /// </summary>
    /// <param name="projectDir">The directory containing the .csproj file.</param>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// This method reads the .csproj file and looks for a section that says "ignore these error codes".
    /// For example, if your .csproj says NoWarn="CS0618,CS0219", this method saves that list so we can
    /// skip those warnings later when reporting diagnostics.
    /// </remarks>
    /// <example>
    /// In a .csproj file:
    /// <code>
    /// &lt;PropertyGroup&gt;
    ///   &lt;NoWarn&gt;CS0618;CS0219&lt;/NoWarn&gt;
    /// &lt;/PropertyGroup&gt;
    /// </code>
    /// This tells the compiler to ignore "CS0618" (obsolete member) and "CS0219" (unused variable) errors.
    /// </example>
    private void LoadProjectSuppressions(string projectDir)
    {
        // If we've already loaded suppressions for this project, don't do it again
        if (_suppressedErrors.ContainsKey(projectDir)) return;

        // Find the .csproj file in the project directory
        var csproj = Directory.GetFiles(projectDir, "*.csproj").FirstOrDefault();
        if (csproj == null) return;

        var suppressions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Parse the XML .csproj file
            var doc = XDocument.Load(csproj);
            
            // Look for the <NoWarn> element which lists error codes to ignore
            var noWarn = doc.Descendants("NoWarn").Select(x => x.Value).FirstOrDefault();
            if (!string.IsNullOrEmpty(noWarn))
            {
                // Split by semicolon or comma (both are valid separators)
                foreach (var id in noWarn.Split(';', ','))
                {
                    var cleanId = id.Trim();
                    
                    // Ensure the error code starts with "CS" (e.g., "0618" becomes "CS0618")
                    if (!cleanId.StartsWith("CS")) cleanId = "CS" + cleanId;
                    
                    suppressions.Add(cleanId);
                }
            }
            
            // Cache the suppressions for this project
            _suppressedErrors[projectDir] = suppressions;
            System.Console.WriteLine($"[DIAG] Loaded {suppressions.Count} suppressions from {Path.GetFileName(csproj)}");
        }
        catch 
        { 
            // If something goes wrong, just create an empty set (no suppressions)
            _suppressedErrors[projectDir] = new HashSet<string>(); 
        }
    }

    /// <summary>
    /// Runs C# compilation diagnostics on a generated file and translates errors back to source locations.
    /// </summary>
    /// <param name="targetPath">Normalized path to the generated .cs file to analyze.</param>
    /// <param name="projectDir">The project directory containing the .csproj file.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// This is the main method that:
    /// <list type="number">
    /// <item><description>Reads the generated C# file from disk</description></item>
    /// <item><description>Feeds it to the C# compiler (Roslyn) to find errors</description></item>
    /// <item><description>Uses the source maps to translate those error locations back to the original XML source</description></item>
    /// <item><description>Filters out errors that are suppressed in the .csproj</description></item>
    /// <item><description>Sends the translated errors to the IDE/editor so the user sees them in the right place</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// If the generated code has an error at line 42, and the source map says "line 42 in generated = line 15 in source",
    /// then we report the error at line 15 so the user sees it where they actually typed the code.
    /// </example>
    public async Task RunDiagnosticsAsync(string targetPath, string projectDir)
    {
        try
        {
            // Normalize the path for consistency
            targetPath = NormalizePath(targetPath);
            
            // If the file doesn't exist, there's nothing to diagnose
            if (!File.Exists(targetPath)) return;

            // Read the entire generated C# file
            string genCode = await File.ReadAllTextAsync(targetPath);
            
            // Parse it into an abstract syntax tree (AST) - Roslyn's internal representation
            var tree = CSharpSyntaxTree.ParseText(genCode, path: targetPath);
            
            // Cache this syntax tree so other files can reference it during compilation
            _projectFiles[targetPath] = tree;

            // Load other project files so Roslyn understands the full project structure
            await EnsureProjectLoaded(projectDir);

            // Create a Roslyn compilation object with all syntax trees and references
            // This tells Roslyn to analyze all the code together
            var compilation = CSharpCompilation.Create("LspProject")
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddReferences(_commonReferences)
                .AddSyntaxTrees(_projectFiles.Values);

            // Get all diagnostic errors/warnings from the compilation
            var roslynDiagnostics = compilation.GetDiagnostics()
                // Filter to only diagnostics from the file we're analyzing
                .Where(d => d.Location.SourceTree?.FilePath != null && 
                           NormalizePath(d.Location.SourceTree.FilePath) == targetPath);

            // Retrieve the source map for this generated file
            if (!_sourceMapCache.TryGetValue(targetPath, out var mapInfo)) return;

            // Load the original source file content for line/character mapping
            var sourceText = SourceText.From(_fileService.GetFileContent(mapInfo.SourceUri));
            
            // Get the generated file's line information
            var genText = tree.GetText();
            
            var translatedDiagnostics = new List<Diagnostic>();

            // Retrieve error suppressions for this project (if any)
            _suppressedErrors.TryGetValue(projectDir, out var projectSuppressions);

            // Process each compiler error
            foreach (var d in roslynDiagnostics)
            {
                // Skip this error if it's suppressed in the .csproj NoWarn list
                if (projectSuppressions != null && projectSuppressions.Contains(d.Id))
                {
                    System.Console.WriteLine($"[DIAG] Skipping suppressed error: {d.Id}");
                    continue;
                }

                // Get the error's line and character position in the generated file
                var span = d.Location.GetLineSpan();
                
                // Convert line/character to absolute character position in generated file
                int genStartPos = genText.Lines[span.StartLinePosition.Line].Start + span.StartLinePosition.Character;
                int genEndPos = genText.Lines[span.EndLinePosition.Line].Start + span.EndLinePosition.Character;

                // Find the source map entry that covers this error position in the generated code
                // Map entries are ordered by size, so we get the smallest matching entry (most specific match)
                var entry = mapInfo.Maps
                    .Where(m => genStartPos >= m.TransformedStart && genStartPos <= m.TransformedEnd)
                    .OrderBy(m => m.TransformedEnd - m.TransformedStart) 
                    .FirstOrDefault();

                // Only translate if we found a valid source map entry
                if (entry.OriginalEnd != 0) 
                {
                    // Calculate the offset within the mapped region
                    // Example: if the map says "chars 100-150 in generated = chars 50-100 in source",
                    // and the error is at position 110, the offset is 10
                    int rel = genStartPos - entry.TransformedStart;
                    
                    // Apply that offset to the original source position
                    int origPos = entry.OriginalStart + rel;
                    
                    // Convert absolute position back to line/character coordinates in the original source
                    var startLinePos = sourceText.Lines.GetLinePosition(origPos);
                    
                    // Calculate the end position (preserve error width)
                    int width = genEndPos - genStartPos;
                    var endLinePos = sourceText.Lines.GetLinePosition(origPos + width);

                    // Create the diagnostic object with translated positions
                    translatedDiagnostics.Add(new Diagnostic {
                        Range = new Range {
                            Start = new Position { Line = startLinePos.Line, Character = startLinePos.Character },
                            End = new Position { Line = endLinePos.Line, Character = endLinePos.Character }
                        },
                        // Include the error code (e.g., "CS0103") for reference
                        Message = $"[{d.Id}] {d.GetMessage()}",
                        // 1 = Error, 2 = Warning (LSP protocol standard values)
                        Severity = d.Severity == DiagnosticSeverity.Error ? 1 : 2,
                        Source = "Roslyn"
                    });
                }
            }

            // Send all translated diagnostics to the IDE/editor via the LSP protocol
            // The IDE will display them as red squiggles, warning icons, etc.
            _router.SendNotification("textDocument/publishDiagnostics", new PublishDiagnosticsParams
            {
                Uri = _fileService.ToUri(_fileService.ToLocalPath(mapInfo.SourceUri)),
                Diagnostics = translatedDiagnostics
            });
        }
        catch (Exception ex) { System.Console.Error.WriteLine($"[DIAG-FATAL]: {ex.Message}"); }
    }

    /// <summary>
    /// Ensures all C# files in the project are loaded and cached for compilation analysis.
    /// </summary>
    /// <param name="projectDir">The project directory to scan for C# files.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// Roslyn needs to analyze the entire project to understand how files reference each other.
    /// This method finds all .cs files in the project (except generated ones), parses them into
    /// syntax trees, and stores them in the cache. That way, when we compile, Roslyn knows about
    /// all available code and can provide accurate error checking.
    /// </remarks>
    private async Task EnsureProjectLoaded(string projectDir)
    {
        // Skip if projectDir is empty or if we already have too many files cached
        if (string.IsNullOrEmpty(projectDir) || _projectFiles.Count > 20) return;
        
        // Find all .cs files in the project (recursively searching subdirectories)
        var files = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories);
        
        foreach (var file in files)
        {
            string norm = NormalizePath(file);
            
            // Skip the "Generated" folder since those are derived files
            if (norm.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}")) continue;
            
            // If we haven't parsed this file yet, read and parse it
            if (!_projectFiles.ContainsKey(norm))
            {
                _projectFiles[norm] = CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(norm), path: norm);
            }
        }
    }

    /// <summary>
    /// Normalizes a file path to a standard format for consistent comparison and caching.
    /// </summary>
    /// <param name="path">The file path to normalize.</param>
    /// <returns>
    /// The normalized path: fully resolved (no .. or .), lowercase, and with system-appropriate separators.
    /// </returns>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// Different ways of writing the same path can cause cache misses. For example:
    /// "C:\Project\...\File.cs", "./File.cs", and "C:\FILE.CS" are all the same file,
    /// but without normalization they'd be treated as different keys in our cache.
    /// This method converts them all to one standard format.
    /// </remarks>
    /// <example>
    /// "C:\Project\..\Project\File.CS" becomes "c:\project\file.cs"
    /// </example>
    private string NormalizePath(string path) => Path.GetFullPath(path).ToLowerInvariant();

    /// <summary>
    /// Finds the project root directory by searching upward for a .csproj file.
    /// </summary>
    /// <param name="startPath">The file path or directory to start searching from.</param>
    /// <returns>
    /// The directory containing a .csproj file, or the parent directory of startPath if no .csproj is found.
    /// </returns>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// Projects are organized in folders. The root folder always contains a .csproj file that describes
    /// the project settings. This method walks up the directory tree (Parent → Parent's Parent → etc.)
    /// until it finds a .csproj file, which tells us we're at the project root.
    /// </remarks>
    /// <example>
    /// If you start at "C:\MyProject\src\Utils\Helper.cs" and "C:\MyProject\" contains a .csproj,
    /// this method returns "C:\MyProject\".
    /// </example>
    private string FindProjectDir(string startPath)
    {
        // Start with the parent directory of the file
        var dir = Path.GetDirectoryName(startPath);
        
        // Keep moving up the directory tree
        while (dir != null)
        {
            // If this directory contains a .csproj file, it's the project root
            if (Directory.GetFiles(dir, "*.csproj").Any()) return dir;
            
            // Otherwise, move to the parent directory
            dir = Path.GetDirectoryName(dir);
        }
        
        // Fallback: return the immediate parent directory
        return Path.GetDirectoryName(startPath) ?? "";
    }
}