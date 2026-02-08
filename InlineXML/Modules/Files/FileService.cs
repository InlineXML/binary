using System.Collections.Concurrent;
using System.Text.Json;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.Routing;

namespace InlineXML.Modules.Files;

/// <summary>
/// Manages file I/O, URI-to-path conversion, and handles file change events from the IDE.
/// Maintains an in-memory buffer of file contents for fast access without disk I/O.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What This Does (ELI5):</strong>
/// The IDE (VS Code, etc.) and this language server need to agree on how to refer to files.
/// The IDE uses URIs (like "file:///C:/Users/Bob/project/file.cs"), while the server uses local paths.
/// This service translates between them and keeps track of file contents so we can analyze code
/// without constantly reading from disk.
/// </para>
/// <para>
/// <strong>The Two-World Problem:</strong>
/// <list type="bullet">
/// <item><description>
/// <strong>IDE World:</strong> Uses URIs like "file:///c:/Users/Project/file.cs" (URI standard format)
/// </description></item>
/// <item><description>
/// <strong>Server World:</strong> Uses local paths like "C:\Users\Project\file.cs" (OS format)
/// </description></item>
/// </list>
/// When the IDE sends "open this file" (with a URI), we convert to a local path.
/// When we send "here's an error" (with a URI), we convert from a local path.
/// </para>
/// <para>
/// <strong>The Buffer Cache:</strong>
/// Every time a file changes, the IDE sends us the new content. Instead of writing to disk
/// immediately, we cache it in memory. This is much faster for analysis and prevents disk thrashing
/// when the user types quickly.
/// </para>
/// <para>
/// <strong>Key Responsibilities:</strong>
/// <list type="bullet">
/// <item><description>Convert between URIs (IDE format) and local paths (OS format)</description></item>
/// <item><description>Listen for file open and change events from the IDE</description></item>
/// <item><description>Store file contents in memory for fast analysis</description></item>
/// <item><description>Return current file content (from buffer or disk)</description></item>
/// <item><description>Dispatch file change events to other services</description></item>
/// </list>
/// </para>
/// </remarks>
public class FileService : AbstractService
{
    /// <summary>
    /// The routing service for registering LSP endpoints.
    /// </summary>
    /// <remarks>
    /// FileService uses this to register handlers for "textDocument/didOpen" and "textDocument/didChange"
    /// LSP methods, so it can respond when the IDE sends file updates.
    /// </remarks>
    private readonly RoutingService _router;
    
    /// <summary>
    /// In-memory cache of file contents, keyed by normalized local file path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why Cache in Memory?:</strong>
    /// When the user types code, the IDE sends file change events rapidly (on every keystroke).
    /// If we read from disk each time, we'd hammer the disk. Instead, we keep the latest content
    /// in memory. This is called a "write-through cache"—the IDE is the source of truth,
    /// and we're just caching it locally.
    /// </para>
    /// <para>
    /// <strong>Concurrency:</strong>
    /// Uses ConcurrentDictionary so multiple threads can safely read/write file content
    /// (the RoutingService might process requests in parallel).
    /// </para>
    /// <para>
    /// <strong>Cache Key Format:</strong>
    /// Keys are normalized local paths (lowercase, full path, no trailing separators).
    /// This ensures different URI representations of the same file map to the same cache entry.
    /// For example: "C:\File.cs", "c:\file.cs", and "file:///C:/File.cs" all normalize to
    /// the same key.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, string> _buffers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes the FileService and registers LSP file handling endpoints.
    /// </summary>
    /// <param name="router">The RoutingService instance for registering LSP endpoints.</param>
    /// <remarks>
    /// <para>
    /// <strong>What Happens Here (ELI5):</strong>
    /// The constructor:
    /// <list type="number">
    /// <item><description>Saves the RoutingService reference</description></item>
    /// <item><description>Registers two LSP endpoints:
    /// - "textDocument/didOpen": Fired when the IDE opens a file
    /// - "textDocument/didChange": Fired when the IDE modifies a file
    /// </description></item>
    /// </list>
    /// Now whenever the IDE opens or edits a file, this service will be notified.
    /// </para>
    /// </remarks>
    public FileService(RoutingService router)
    {
        _router = router;
        RegisterFileRoutes();
    }

    /// <summary>
    /// Converts a file URI or local path to a normalized local path.
    /// </summary>
    /// <param name="uriOrPath">
    /// A file URI (e.g., "file:///C:/Users/Bob/file.cs") or a local path (e.g., "C:\Users\Bob\file.cs").
    /// </param>
    /// <returns>
    /// A normalized local path suitable for file I/O and dictionary keys.
    /// Returns empty string if the input is null or empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// File URIs and local paths look completely different:
    /// <list type="bullet">
    /// <item><description>URI: "file:///C:/Users/Bob/file.cs"</description></item>
    /// <item><description>Windows path: "C:\Users\Bob\file.cs"</description></item>
    /// <item><description>URI (URL-encoded): "file:///C%3A/Users/Bob/file.cs"</description></item>
    /// </list>
    /// This method strips URI schemes and URL encoding, normalizes slashes, and returns a clean path.
    /// </para>
    /// <para>
    /// <strong>The Conversion Steps:</strong>
    /// <list type="number">
    /// <item><description><strong>Strip scheme:</strong> Remove "file://" or "file:" prefix</description></item>
    /// <item><description><strong>Unescape URL encoding:</strong> Convert %3A → :</description></item>
    /// <item><description><strong>Fix Windows drives:</strong> Handle "/C:" → "C:"</description></item>
    /// <item><description><strong>Resolve duplicates:</strong> Fix "C:\C:\..." → "C:\..."</description></item>
    /// <item><description><strong>Normalize:</strong> Resolve .., ., and use OS separators</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Why All The Special Cases?:</strong>
    /// Different systems and applications format URIs differently:
    /// - VS Code on Windows: "file:///c:/Users/..."
    /// - VS Code on Linux: "file:///home/user/..."
    /// - Some tools URL-encode the colon: "file:///C%3A/Users/..."
    /// - Some tools have bugs that double the drive letter: "file:///C:/C:/Users/..."
    /// We handle all these cases so developers using different IDEs and configurations
    /// all get consistent behavior.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Windows URIs
    /// ToLocalPath("file:///C:/Users/Bob/file.cs")
    /// // Returns: "C:\Users\Bob\file.cs" (on Windows)
    /// 
    /// // Windows with URL encoding
    /// ToLocalPath("file:///C%3A/Users/Bob/file.cs")
    /// // Returns: "C:\Users\Bob\file.cs"
    /// 
    /// // Already a local path
    /// ToLocalPath("C:\\Users\\Bob\\file.cs")
    /// // Returns: "C:\Users\Bob\file.cs" (normalized)
    /// 
    /// // Corrupted path (double drive letter)
    /// ToLocalPath("file:///C:/C:/Users/Bob/file.cs")
    /// // Returns: "C:\Users\Bob\file.cs" (fixed)
    /// </code>
    /// </example>
    public string ToLocalPath(string uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath)) return string.Empty;
        string path = uriOrPath;

        // ========================================
        // STEP 1: Strip URI Scheme
        // ========================================
        // Remove "file://" (7 chars) or "file:" (5 chars) prefix if present
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) 
            path = path.Substring(7);
        else if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) 
            path = path.Substring(5);

        // ========================================
        // STEP 2: Unescape URL Encoding
        // ========================================
        // Convert %3A → :, %20 → space, etc.
        // This handles URIs like "file:///C%3A/Users/..."
        path = Uri.UnescapeDataString(path);
        
        // ========================================
        // STEP 3: Fix Windows Drive Letter Format
        // ========================================
        // VS Code sometimes formats URIs as "/C:/path" instead of "C:/path"
        // We detect this and remove the leading slash
        if (path.StartsWith("/") && path.Length > 2 && path[2] == ':') 
            path = path.Substring(1);

        // ========================================
        // STEP 4: Fix Corrupted Double Drive Letters
        // ========================================
        // Sometimes paths become "C:\C:\Users\..." (duplicate drive letter)
        // Find the LAST occurrence of ":\", which is the correct drive letter
        // Example: "C:\C:\Users\file" → keep "C:\Users\file"
        int lastDrive = path.LastIndexOf(":\\", StringComparison.OrdinalIgnoreCase);
        if (lastDrive > 0) 
            path = path.Substring(lastDrive - 1);

        // ========================================
        // STEP 5: Normalize Path
        // ========================================
        // Resolve .. and . segments, convert slashes to OS separators (\ on Windows, / on Unix)
        // Also remove trailing slashes
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch 
        { 
            // If Path.GetFullPath fails, do a simple fallback (just fix slashes)
            return path.Replace('/', Path.DirectorySeparatorChar); 
        }
    }

    /// <summary>
    /// Converts a local file path to a file URI suitable for sending to the IDE.
    /// </summary>
    /// <param name="localPath">A local file path (e.g., "C:\Users\Bob\file.cs").</param>
    /// <returns>
    /// A file URI (e.g., "file:///C:/Users/Bob/file.cs") suitable for LSP messages.
    /// Returns empty string if the input is null or empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is the reverse of ToLocalPath(). It takes a local file path and converts it
    /// to the URI format that the IDE expects.
    /// </para>
    /// <para>
    /// <strong>The Conversion Steps:</strong>
    /// <list type="number">
    /// <item><description><strong>Get full path:</strong> Resolve any relative paths (../, ./, etc.)</description></item>
    /// <item><description><strong>Normalize slashes:</strong> Convert Windows \ to / for URI format</description></item>
    /// <item><description><strong>Add leading slash:</strong> URIs expect /C:/... format</description></item>
    /// <item><description><strong>Add scheme:</strong> Prepend "file://"</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Why URI Format?:</strong>
    /// The LSP protocol and the IDE always work with URIs. When we report diagnostics or
    /// send notifications about files, we must use URIs, not local paths. Otherwise the IDE
    /// won't know which file the message refers to.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Windows path
    /// ToUri("C:\\Users\\Bob\\file.cs")
    /// // Returns: "file:///C:/Users/Bob/file.cs"
    /// 
    /// // Relative path
    /// ToUri(".\\project\\file.cs")
    /// // Returns: "file:///C:/CurrentDir/project/file.cs" (or wherever current dir is)
    /// 
    /// // Already a URI (fallback behavior)
    /// ToUri("file:///C:/Users/Bob/file.cs")
    /// // Returns: "file:///C:/Users/Bob/file.cs" (unchanged)
    /// </code>
    /// </example>
    public string ToUri(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return string.Empty;
        try 
        {
            // Get full path and convert backslashes to forward slashes (URI standard)
            string fullPath = Path.GetFullPath(localPath).Replace('\\', '/');
            
            // Ensure URI format: URIs expect a leading slash before the drive letter
            // Example: "/C:/Users/Bob/file.cs"
            if (!fullPath.StartsWith("/")) 
                fullPath = "/" + fullPath;
            
            // Prepend the file URI scheme
            return "file://" + fullPath;
        }
        catch 
        { 
            // If something goes wrong, return the original path as a fallback
            // Better to return something than crash
            return localPath; 
        }
    }

    /// <summary>
    /// Retrieves the current content of a file (from cache or disk).
    /// </summary>
    /// <param name="uriOrPath">A file URI or local path.</param>
    /// <returns>
    /// The file content as a string. Returns empty string if the file doesn't exist
    /// or if it hasn't been opened yet (not in cache).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This method provides a unified interface to get file content:
    /// <list type="number">
    /// <item><description>Check the in-memory cache (fast)</description></item>
    /// <item><description>If not cached, try reading from disk (slower)</description></item>
    /// <item><description>Return empty string if file doesn't exist</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Cache Precedence:</strong>
    /// The in-memory cache takes priority because it contains the latest version.
    /// If a file is open in the IDE, we have its latest content in the cache.
    /// If a file is not open (not in cache), we fall back to disk.
    /// This means:
    /// <list type="bullet">
    /// <item><description>For open files: always get the current IDE version (not stale disk version)</description></item>
    /// <item><description>For closed files: we can still read them from disk if needed</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // File is open in IDE (in cache)
    /// var content = fileService.GetFileContent("file:///C:/project/open.cs");
    /// // Returns: Latest content from IDE cache (100% up-to-date)
    /// 
    /// // File exists on disk but isn't open
    /// var content = fileService.GetFileContent("file:///C:/project/closed.cs");
    /// // Returns: Content from disk (may be outdated if user edited but didn't save)
    /// 
    /// // File doesn't exist
    /// var content = fileService.GetFileContent("file:///C:/project/nonexistent.cs");
    /// // Returns: "" (empty string)
    /// </code>
    /// </example>
    public string GetFileContent(string uriOrPath)
    {
        // Convert URI to local path
        string localPath = ToLocalPath(uriOrPath);
        
        // Check cache first (open files in IDE)
        if (_buffers.TryGetValue(localPath, out var content)) 
            return content;
        
        // Fallback to disk if not cached
        return File.Exists(localPath) ? File.ReadAllText(localPath) : string.Empty;
    }

    /// <summary>
    /// Registers LSP endpoints for file open and change events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This method registers two LSP message handlers with the RoutingService:
    /// <list type="bullet">
    /// <item><description><strong>textDocument/didOpen:</strong> Fired when the IDE opens a file. We cache the content.</description></item>
    /// <item><description><strong>textDocument/didChange:</strong> Fired when the IDE modifies a file. We update the cache.</description></item>
    /// </list>
    /// Both handlers call the same HandleUpdate method to process the file change.
    /// </para>
    /// <para>
    /// <strong>Why Return Empty Response?:</strong>
    /// According to the LSP spec, didOpen and didChange are notifications (one-way messages
    /// from IDE to server). We don't need to send back any response. We just return an empty
    /// response to satisfy the LSP protocol.
    /// </para>
    /// </remarks>
    private void RegisterFileRoutes()
    {
        // Handle: IDE opened a file (user clicked on it in the file explorer)
        _router.RegisterRoute("textDocument/didOpen", (request) => {
            HandleUpdate(request);
            return new ValueTask<LspResponse>(new LspResponse { Id = request.Id });
        });

        // Handle: IDE modified a file (user typed or pasted)
        _router.RegisterRoute("textDocument/didChange", (request) => {
            HandleUpdate(request);
            return new ValueTask<LspResponse>(new LspResponse { Id = request.Id });
        });
    }

    /// <summary>
    /// Processes a file change event from the IDE and updates the cache.
    /// </summary>
    /// <param name="request">The LSP request containing file change information.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// When the IDE sends a file change event, this method:
    /// <list type="number">
    /// <item><description>Extracts the file URI from the request</description></item>
    /// <item><description>Extracts the new file content</description></item>
    /// <item><description>Converts URI to a local path</description></item>
    /// <item><description>Stores the content in the cache</description></item>
    /// <item><description>Dispatches a FileChanged event to other services</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>LSP Message Format:</strong>
    /// Different requests provide content in different fields:
    /// <list type="bullet">
    /// <item><description>
    /// <strong>didOpen:</strong> Content is in `params.textDocument.text`
    /// </description></item>
    /// <item><description>
    /// <strong>didChange:</strong> Content is in the last item of `params.contentChanges[].text`
    /// (we take the last one because there can be multiple incremental changes)
    /// </description></item>
    /// </list>
    /// We handle both formats so the same method works for both events.
    /// </para>
    /// <para>
    /// <strong>The Event Dispatch:</strong>
    /// After updating the cache, we dispatch a FileChanged event to notify:
    /// - RoslynService: "A file changed, re-parse it"
    /// - WorkspaceService: "Update your workspace tracking"
    /// - Other services that care about file changes
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // When IDE sends:
    /// {
    ///   "method": "textDocument/didChange",
    ///   "params": {
    ///     "textDocument": { "uri": "file:///C:/project/file.cs" },
    ///     "contentChanges": [
    ///       { "text": "var x = 42;" }
    ///     ]
    ///   }
    /// }
    /// 
    /// This method:
    /// 1. Extracts uri = "file:///C:/project/file.cs"
    /// 2. Extracts text = "var x = 42;"
    /// 3. Converts URI to "C:\project\file.cs"
    /// 4. Stores in _buffers["c:\project\file.cs"] = "var x = 42;"
    /// 5. Dispatches FileChanged event with "C:\project\file.cs"
    /// </code>
    /// </example>
    private void HandleUpdate(LspRequest request)
    {
        // Extract the textDocument property from the request params
        if (request.Params.HasValue && request.Params.Value.TryGetProperty("textDocument", out var doc))
        {
            // Get the file URI from textDocument.uri
            var uri = doc.GetProperty("uri").GetString();
            if (uri == null) return;

            // Extract the file content (try multiple locations for compatibility)
            string text = "";
            
            // Try didChange format: params.contentChanges[].text (take the last change)
            if (request.Params.Value.TryGetProperty("contentChanges", out var changes) && changes.GetArrayLength() > 0)
                text = changes[changes.GetArrayLength() - 1].GetProperty("text").GetString() ?? "";
            
            // Fallback to didOpen format: params.textDocument.text
            else if (doc.TryGetProperty("text", out var t))
                text = t.GetString() ?? "";

            // Convert URI to local path and cache the content
            string localPath = ToLocalPath(uri);
            _buffers[localPath] = text;
            
            // Notify other services that a file has changed
            Events.Workspace.FileChanged.Dispatch(localPath);
        }
    }
}