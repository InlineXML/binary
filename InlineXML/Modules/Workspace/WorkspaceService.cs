using System.Collections.Concurrent;
using System.IO;
using System.Text;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.Transformation;
using InlineXML.Modules.Files;

namespace InlineXML.Modules.Workspace;

/// <summary>
/// Manages file system operations, project structure, and source file tracking.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What This Does (ELI5):</strong>
/// When you open a project in an IDE, the IDE needs to know:
/// <list type="bullet">
/// <item><description>Where is the project root folder?</description></item>
/// <item><description>What source files exist in this project?</description></item>
/// <item><description>When files are created or changed, where should they be saved?</description></item>
/// </list>
/// This service handles all of that. It keeps track of the project structure, caches which files exist,
/// and when the transformation service converts an XML file to C# code, it saves that generated C# to disk
/// in the right location (a "Generated" folder).
/// </para>
/// <para>
/// <strong>Key Responsibilities:</strong>
/// <list type="bullet">
/// <item><description>Tracks the project root directory and all source files within it</description></item>
/// <item><description>Listens for file transformation events and saves generated .cs files to disk</description></item>
/// <item><description>Maintains a cache of all .cs and .xcs files in the project for quick lookup</description></item>
/// <item><description>Cleans up malformed file paths before processing them</description></item>
/// <item><description>Provides detailed error logging for file I/O problems</description></item>
/// </list>
/// </para>
/// </remarks>
public class WorkspaceService : AbstractService
{
    private readonly FileService _fileService;
    
    /// <summary>
    /// The root directory of the current project (where the .csproj file is).
    /// </summary>
    /// <remarks>
    /// All relative paths and file operations are resolved relative to this directory.
    /// Empty string indicates no project is currently loaded.
    /// </remarks>
    public string ProjectRoot { get; private set; } = string.Empty;
    
    /// <summary>
    /// Unused dictionary field. Left for backward compatibility.
    /// </summary>
    /// <remarks>
    /// This was likely intended for future features but is superseded by _cachedSourceFiles.
    /// Can be removed in a future refactor.
    /// </remarks>
    private readonly ConcurrentDictionary<string, string> _fileStore = new(StringComparer.OrdinalIgnoreCase);
    
    /// <summary>
    /// Cache of all source files (.cs and .xcs) in the project.
    /// Excludes files in bin/, obj/, and Generated/ folders.
    /// </summary>
    /// <remarks>
    /// This cache is refreshed whenever SetWorkspace is called or a new file is discovered.
    /// Kept in memory for fast lookup without repeated directory scans.
    /// </remarks>
    private List<string> _cachedSourceFiles = new();

    /// <summary>
    /// Initializes the WorkspaceService and sets up event listeners for file transformations.
    /// </summary>
    /// <param name="fileService">Service for file I/O operations and path normalization.</param>
    /// <remarks>
    /// <strong>What Happens Here (ELI5):</strong>
    /// The constructor subscribes to the "FileTransformed" event. This means whenever the transformation
    /// service converts an XML file to C# code, this service automatically:
    /// <list type="number">
    /// <item><description>Receives the transformed code</description></item>
    /// <item><description>Cleans up the file path (removing corrupted double-path strings)</description></item>
    /// <item><description>Figures out where the generated .cs file should live</description></item>
    /// <item><description>Creates the necessary directories (like "Generated" folder)</description></item>
    /// <item><description>Saves the generated C# code to disk</description></item>
    /// <item><description>Notifies other services that a file has been changed</description></item>
    /// </list>
    /// All of this happens automatically whenever a transformation completes, with comprehensive error handling.
    /// </remarks>
    public WorkspaceService(FileService fileService)
    {
        _fileService = fileService;

        // Subscribe to the FileTransformed event: whenever a file transformation completes, save the output
        Events.Transformer.FileTransformed.AddEventListener(payload =>
        {
            string? localPath = null;
            string? targetPath = null;
            try 
            {
                // Step 1: Convert the URI to a local file path and clean it up
                // This catches cases where the path might have double drive letters like "c:\Users\c:\Users\file.cs"
                localPath = _fileService.ToLocalPath(payload.File);
                localPath = SanitizePath(localPath);
                
                // Register this file in our cache (without dispatching the event yet)
                RegisterFile(localPath, false);
                
                // Step 2: Find the project root and calculate the relative path
                // Example: if file is "C:\MyProject\src\Utils.xcs", we get relative path "src\Utils.xcs"
                var projectDir = FindProjectDir(localPath);
                var relativePath = Path.GetRelativePath(projectDir, localPath);
                
                // Step 3: Build the output path for the generated C# file
                // Example: "C:\MyProject\Generated\src\Utils.cs"
                targetPath = Path.Combine(projectDir, "Generated", Path.ChangeExtension(relativePath, ".cs"));
                var targetDir = Path.GetDirectoryName(targetPath);

                // Create the Generated folder and any subdirectories if they don't exist
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }
                
                // Write the transformed C# code to the generated file
                File.WriteAllText(targetPath, payload.Content);
                
                // Notify other services (like DiagnosticService) that a file has been transformed
                // Use the original URI so VS Code knows which file in the editor to update
                Events.Workspace.FileChanged.Dispatch(payload.File);
            }
            catch (Exception ex)
            {
                // Log comprehensive error information for debugging file save failures
                LogError("TRANSFORM_SAVE_ERROR", ex, $"Source: {localPath} | Target: {targetPath}");
            }

            return payload;
        });
    }

    /// <summary>
    /// Sets the project root directory and refreshes the source file cache.
    /// </summary>
    /// <param name="dir">The path to the project root (should contain a .csproj file).</param>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// When the IDE opens a folder (workspace), this method is called. It:
    /// <list type="number">
    /// <item><description>Normalizes the directory path (removes .., fixes case, etc.)</description></item>
    /// <item><description>Saves it as the ProjectRoot so all other methods know where the project is</description></item>
    /// <item><description>Scans the entire project directory to find all .cs and .xcs files</description></item>
    /// <item><description>Caches those files for fast lookup</description></item>
    /// <item><description>Notifies other services that the workspace has changed</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// When VS Code opens folder "C:\Users\Developer\MyProject\", this is called with that path.
    /// It will scan the folder and find all source files, ready for editing and transformation.
    /// </example>
    public void SetWorkspace(string dir)
    {
        try
        {
            // Normalize the path: resolve relative paths, remove trailing slashes, make it absolute
            ProjectRoot = SanitizePath(_fileService.ToLocalPath(dir)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            
            // Scan the entire project directory and cache all source files
            RefreshSourceFileCache();
            
            // Notify other services (like DiagnosticService) that the workspace has changed
            Events.Workspace.WorkspaceChanged.Dispatch(ProjectRoot);
        }
        catch (Exception ex)
        {
            // Log detailed error information if workspace loading fails
            LogError("SET_WORKSPACE_ERROR", ex, dir);
        }
    }

    /// <summary>
    /// Gets a read-only list of all cached source files in the project.
    /// </summary>
    /// <returns>
    /// A read-only list of normalized file paths for all .cs and .xcs files in the project
    /// (excluding bin/, obj/, and Generated/ directories).
    /// </returns>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// Other services need to know what files are in the project. This method returns the cached list
    /// without requiring a new disk scan, making it very fast. The list is "read-only" to prevent
    /// accidental modifications to the cache.
    /// </remarks>
    /// <example>
    /// <code>
    /// var files = workspace.GetAllSourceFiles();
    /// foreach (var file in files)
    /// {
    ///     Console.WriteLine(file); // "C:\MyProject\Utils.cs", "C:\MyProject\Helpers.xcs", etc.
    /// }
    /// </code>
    /// </example>
    public IReadOnlyList<string> GetAllSourceFiles() => _cachedSourceFiles.AsReadOnly();

    /// <summary>
    /// Scans the project directory and rebuilds the cache of source files.
    /// </summary>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// This method walks the entire project directory tree and finds all source files (.cs and .xcs).
    /// It intentionally skips three types of folders:
    /// <list type="bullet">
    /// <item><description><strong>bin/</strong> - Contains compiled executable files, not source code</description></item>
    /// <item><description><strong>obj/</strong> - Contains intermediate compiler output, not source code</description></item>
    /// <item><description><strong>Generated/</strong> - Contains auto-generated C# files, not original sources</description></item>
    /// </list>
    /// The cache is cleared and rebuilt fresh, so this is called when the workspace changes or when
    /// the IDE needs an updated file list.
    /// </remarks>
    private void RefreshSourceFileCache()
    {
        // Clear the old cache
        _cachedSourceFiles.Clear();
        
        // Don't try to scan if no project is loaded
        if (string.IsNullOrEmpty(ProjectRoot)) return;

        try
        {
            // Find all files in the project (recursively, including subdirectories)
            var files = Directory.GetFiles(ProjectRoot, "*.*", SearchOption.AllDirectories)
                // Filter to only .cs (C# source) and .xcs (XML-based C# source) files
                .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xcs")) && 
                            // Exclude the build output directory (contains .dll, .exe, etc.)
                            !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && 
                            // Exclude the intermediate build directory (contains .obj files)
                            !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && 
                            // Exclude the auto-generated files directory
                            !f.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}"))
                // Get the full absolute path for each file
                .Select(Path.GetFullPath)
                .ToList();

            // Add all discovered files to the cache
            _cachedSourceFiles.AddRange(files);
        }
        catch (Exception ex)
        {
            // Log errors but don't crash - we still want the service running even if scanning failed
            LogError("REFRESH_CACHE_ERROR", ex, ProjectRoot);
        }
    }

    /// <summary>
    /// Registers a file in the source cache if it's not already present.
    /// </summary>
    /// <param name="fileName">The file path to register.</param>
    /// <param name="dispatch">
    /// If true, dispatch a FileRegistered event to notify other services.
    /// If false, just add to cache silently (used during batch operations).
    /// </param>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// When a new file is created or discovered, this method:
    /// <list type="number">
    /// <item><description>Normalizes the file path (removes .., makes lowercase, etc.)</description></item>
    /// <item><description>Checks if it's already in the cache (avoid duplicates)</description></item>
    /// <item><description>If it's new, adds it to the cache</description></item>
    /// <item><description>Optionally notifies other services that a new file has been registered</description></item>
    /// </list>
    /// The dispatch flag exists because sometimes we're doing batch operations (like refreshing the entire
    /// cache) where firing an event for every single file would be wasteful.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Register a file and notify other services
    /// workspace.RegisterFile("C:\\MyProject\\NewFile.cs", dispatch: true);
    /// 
    /// // Register multiple files without firing events for each one (more efficient)
    /// workspace.RegisterFile(file1, dispatch: false);
    /// workspace.RegisterFile(file2, dispatch: false);
    /// workspace.RegisterFile(file3, dispatch: false);
    /// </code>
    /// </example>
    public void RegisterFile(string fileName, bool dispatch = true)
    {
        // Normalize the file path to match our caching format
        var normalized = SanitizePath(_fileService.ToLocalPath(fileName));
        
        // Only add if this file isn't already in the cache
        if (!_cachedSourceFiles.Contains(normalized))
        {
            _cachedSourceFiles.Add(normalized);
            
            // If requested, notify other services that a new file has been registered
            if (dispatch) Events.Workspace.FileRegistered.Dispatch(normalized);
        }
    }

    /// <summary>
    /// Finds the project root directory by searching upward from the given path.
    /// </summary>
    /// <param name="startPath">The file path or directory to start searching from.</param>
    /// <returns>
    /// The directory containing a .csproj file, or the current ProjectRoot if not found.
    /// </returns>
    /// <remarks>
    /// <strong>What This Does (ELI5):</strong>
    /// C# projects have a .csproj file at the root that describes the project settings.
    /// This method starts at a given file/directory and walks upward (parent directory → parent's parent → etc.)
    /// looking for a .csproj file. Once found, we know we've reached the project root.
    /// 
    /// If we can't find a .csproj (unlikely in a well-formed project), we return the currently
    /// known ProjectRoot as a fallback.
    /// </remarks>
    /// <example>
    /// If you have a file at "C:\MyProject\src\Utilities\Helper.cs" and the .csproj is at "C:\MyProject\",
    /// this method will walk up: Helper.cs → Utilities → src → MyProject (found .csproj!) and return "C:\MyProject\".
    /// </example>
    private string FindProjectDir(string startPath)
    {
        // Normalize the start path
        var localStartPath = SanitizePath(_fileService.ToLocalPath(startPath));
        
        // Get the parent directory of the file
        var dir = Path.GetDirectoryName(localStartPath);
        
        // Walk up the directory tree
        while (dir != null)
        {
            try 
            {
                // Check if this directory exists and contains a .csproj file
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.csproj").Any()) 
                {
                    return dir;
                }
            }
            catch 
            { 
                // If anything goes wrong (permissions, etc.), stop searching
                break; 
            }
            
            // Move to the parent directory
            dir = Path.GetDirectoryName(dir);
        }
        
        // Fallback: return the currently known project root
        // This ensures we always return something reasonable
        return ProjectRoot;
    }

    /// <summary>
    /// Normalizes and validates a file path to prevent path traversal attacks and malformed paths.
    /// </summary>
    /// <param name="path">The file path to sanitize.</param>
    /// <returns>
    /// The absolute, canonical form of the path with any malformations corrected.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// File paths can become corrupted during processing. A common bug is when a path gets duplicated:
    /// instead of "C:\Users\Developer\MyProject\File.cs", we might end up with 
    /// "C:\Users\C:\Users\Developer\MyProject\File.cs" (notice the duplicated "C:\Users\").
    /// </para>
    /// <para>
    /// This method:
    /// <list type="number">
    /// <item><description>Detects if there are multiple drive letters in the path (e.g., "c:" appears twice)</description></item>
    /// <item><description>Removes everything before the last drive letter, keeping only the valid path</description></item>
    /// <item><description>Resolves the path to its full, absolute form (removes "..", ".", etc.)</description></item>
    /// </list>
    /// This prevents the "c:\Users\c:\Users\..." bug and ensures paths are in a canonical format.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Input with duplicated drive letter
    /// SanitizePath("c:\\Users\\c:\\Users\\Developer\\file.cs")
    /// // Returns: "C:\Users\Developer\file.cs"
    /// 
    /// // Input with relative path traversal
    /// SanitizePath("C:\\project\\src\\..\\..\\file.cs")
    /// // Returns: "C:\file.cs"
    /// </code>
    /// </example>
    private string SanitizePath(string path)
    {
        // Handle empty or whitespace-only paths
        if (string.IsNullOrWhiteSpace(path)) return path;

        // Strip repeated drive letters if they were accidentally concatenated
        // Find the LAST occurrence of ":\", which indicates the last (and correct) drive letter
        // Example: "c:\Users\c:\Users\file" → keep everything from the last "c:\" onward
        if (path.LastIndexOf(":\\") > 1)
        {
            path = path.Substring(path.LastIndexOf(":\\") - 1);
        }

        // Convert to absolute path, resolving .. and . segments
        // Example: "C:\project\src\..\..\file.cs" → "C:\file.cs"
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Logs detailed error information with stack traces to standard error.
    /// </summary>
    /// <param name="context">A label describing what operation failed (e.g., "TRANSFORM_SAVE_ERROR").</param>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="detail">Additional context information about what was being processed.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// When something goes wrong in the workspace service, we need detailed logs for debugging.
    /// This method writes to standard error (STDERR) instead of standard output (STDOUT) so that:
    /// <list type="bullet">
    /// <item><description>Error logs can be easily separated from normal program output</description></item>
    /// <item><description>The LSP client (VS Code, etc.) can collect these errors separately</description></item>
    /// <item><description>Developers can review error logs without wade through normal logs</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Each error log includes:
    /// <list type="bullet">
    /// <item><description><strong>Context:</strong> What was being done when the error occurred</description></item>
    /// <item><description><strong>Error message:</strong> What the error actually says</description></item>
    /// <item><description><strong>Details:</strong> Extra context (which file, which path, etc.)</description></item>
    /// <item><description><strong>Stack trace:</strong> Exactly where in the code the error happened</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// try 
    /// {
    ///     File.WriteAllText(path, content);
    /// }
    /// catch (Exception ex)
    /// {
    ///     LogError("FILE_WRITE_FAILED", ex, $"Path: {path}");
    ///     // Output to STDERR includes full stack trace and context
    /// }
    /// </code>
    /// Output to STDERR would look like:
    /// <code>
    /// --- [WORKSPACE IO ERROR: FILE_WRITE_FAILED] ---
    /// Message: Access to the path 'C:\Protected\file.cs' is denied.
    /// Detail: Path: C:\Protected\file.cs
    /// Stack Trace:
    ///    at System.IO.FileStream..ctor(String path, FileMode mode, FileAccess access, FileShare share, Int32 bufferSize, FileOptions options, FileStream haveHandle)
    ///    ...
    /// ----------------------------------------
    /// </code>
    /// </example>
    private void LogError(string context, Exception ex, string? detail)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- [WORKSPACE IO ERROR: {context}] ---");
        sb.AppendLine($"Message: {ex.Message}");
        sb.AppendLine($"Detail: {detail ?? "N/A"}");
        sb.AppendLine($"Stack Trace:\n{ex.StackTrace}");
        sb.AppendLine("----------------------------------------");
        
        // Write to standard error so LSP logs can capture it separately
        System.Console.Error.WriteLine(sb.ToString());
    }
}