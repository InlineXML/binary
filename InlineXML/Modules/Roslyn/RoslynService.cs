using System.Collections.Concurrent;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.Files;
using Microsoft.CodeAnalysis.CSharp;

namespace InlineXML.Modules.Roslyn;

/// <summary>
/// Orchestrates C# parsing and validates file changes, coordinating with the transformation pipeline
/// to prevent infinite loops and unnecessary re-parsing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What This Does (ELI5):</strong>
/// Imagine you have a production line where files flow through several stations:
/// <list type="number">
/// <item><description><strong>Station 1 (This service):</strong> Watch for files that change, parse them</description></item>
/// <item><description><strong>Station 2:</strong> Transform the parsed file (XML → C#)</description></item>
/// <item><description><strong>Station 3:</strong> Save the transformed file to disk</description></item>
/// <item><description><strong>Station 4:</strong> Analyze the transformed file for errors</description></item>
/// </list>
/// But here's the problem: when we save the transformed file (Station 3), the file system fires a "file changed" event.
/// If we aren't careful, we'd parse it again, trigger transformation again, save again, parse again... forever!
/// This is called an "infinite loop" or "feedback loop."
/// </para>
/// <para>
/// This service prevents that chaos using three tricks:
/// <list type="number">
/// <item><description><strong>A Processing Gate:</strong> "I'm already working on this file, ignore new change events"</description></item>
/// <item><description><strong>Debouncing:</strong> "Wait 200ms before parsing in case more changes are coming"</description></item>
/// <item><description><strong>Guards:</strong> "Only parse .xcs files, never touch Generated files"</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Key Responsibilities:</strong>
/// <list type="bullet">
/// <item><description>Listens for file registration and change events from the workspace</description></item>
/// <item><description>Applies guards to reject Generated files and non-.xcs files</description></item>
/// <item><description>Prevents feedback loops by tracking files being processed</description></item>
/// <item><description>Debounces rapid file changes (waits before parsing)</description></item>
/// <item><description>Parses .xcs files using Roslyn to create syntax trees</description></item>
/// <item><description>Dispatches parsed syntax trees to the transformation pipeline</description></item>
/// </list>
/// </para>
/// </remarks>
public class RoslynService : AbstractService
{
    private readonly FileService _fileService;
    
    /// <summary>
    /// The "Processing Gate": tracks files currently being processed to prevent feedback loops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// When we start processing a file, we add it to this dictionary with the key being the file path.
    /// Other events that arrive while the file is in here are ignored. When transformation completes
    /// and the file is saved to disk, we remove it from here.
    /// </para>
    /// <para>
    /// This prevents the "infinite loop" scenario:
    /// <list type="number">
    /// <item><description>User edits Foo.xcs → we parse it and add to _processingFiles</description></item>
    /// <item><description>File system fires "Foo.xcs changed" event (we got the change initially)</description></item>
    /// <item><description>We ignore it because Foo.xcs is in _processingFiles</description></item>
    /// <item><description>Transformation finishes, saves to disk</description></item>
    /// <item><description>File system fires "Foo.Generated.cs changed" event</description></item>
    /// <item><description>We ignore it (not a .xcs file)</description></item>
    /// <item><description>Transformation completes, removes from _processingFiles</description></item>
    /// <item><description>Everything is calm again</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private static readonly ConcurrentDictionary<string, byte> _processingFiles = new();
    
    /// <summary>
    /// Tracks debounce cancellation tokens for each file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// Users type code quickly, which generates many change events in rapid succession.
    /// If we parse on EVERY keystroke, we'd waste CPU parsing incomplete code.
    /// Instead, we use a "debounce" timer: "Wait 200ms, if no more changes come, then parse."
    /// </para>
    /// <para>
    /// <strong>How Debouncing Works:</strong>
    /// <list type="number">
    /// <item><description>User types at position 10, 11, 12... rapid fire</description></item>
    /// <item><description>Change event at position 10 → Start a 200ms timer</description></item>
    /// <item><description>Change event at position 11 → Cancel the old timer, start a new 200ms timer</description></item>
    /// <item><description>Change event at position 12 → Cancel again, start new timer</description></item>
    /// <item><description>User stops typing (no new events for 200ms) → Timer expires, parse happens</description></item>
    /// </list>
    /// This way we only parse once after the user stops typing, not on every keystroke.
    /// Each file gets its own cancellation token so we can independently debounce each file.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTokens = new();

    /// <summary>
    /// Initializes the RoslynService and sets up event listeners for file changes and transformations.
    /// </summary>
    /// <param name="fileService">Service for reading file contents from disk or URI.</param>
    /// <remarks>
    /// <para>
    /// <strong>What Happens Here (ELI5):</strong>
    /// The constructor wires up the event system:
    /// <list type="number">
    /// <item><description><strong>FileRegistered listener:</strong> A new .xcs file was discovered, start processing it</description></item>
    /// <item><description><strong>FileChanged listener:</strong> An existing .xcs file was modified, start processing it</description></item>
    /// <item><description><strong>FileTransformed listener:</strong> Transformation is complete, unlock the gate so we can process this file again</description></item>
    /// </list>
    /// These listeners work together to keep files flowing through the pipeline without getting stuck.
    /// </para>
    /// </remarks>
    public RoslynService(FileService fileService)
    {
        _fileService = fileService;

        // When a new file is registered (discovered) or a file changes, start the parse/transform pipeline
        Events.Workspace.FileRegistered.AddEventListener(FileEventListeners);
        Events.Workspace.FileChanged.AddEventListener(FileEventListeners);

        // RELEASE THE GATE: When the transformation is finally written to disk,
        // we allow Roslyn to care about this file again.
        // This removes the file from _processingFiles, so new change events will be processed
        Events.Transformer.FileTransformed.AddEventListener(payload =>
        {
            _processingFiles.TryRemove(payload.File, out _);
            return payload;
        });
    }

    /// <summary>
    /// Event handler for file registration and file change events.
    /// Applies guards and debouncing before parsing the file.
    /// </summary>
    /// <param name="file">The file path that changed or was registered.</param>
    /// <returns>The file path (for event pass-through).</returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is the gateway for file processing. It applies a series of guards to decide whether
    /// to parse the file or skip it:
    /// <list type="number">
    /// <item><description><strong>Guard 1 (Extension):</strong> Only parse .xcs files (not .cs, not .txt, etc.)</description></item>
    /// <item><description><strong>Guard 2 (Path):</strong> Never parse Generated/ folder files (they're auto-created)</description></item>
    /// <item><description><strong>Guard 3 (Loop Prevention):</strong> If we're already processing this file, skip it</description></item>
    /// <item><description><strong>Guard 4 (Debounce):</strong> Wait 200ms before parsing (in case more changes come)</description></item>
    /// </list>
    /// If all guards pass, we start a background task that waits 200ms, then parses the file.
    /// </para>
    /// <para>
    /// <strong>The Guard System (Why Each Guard Matters):</strong>
    /// <list type="bullet">
    /// <item><description>
    /// <strong>Extension Guard:</strong> The workspace fires FileChanged events for ALL files.
    /// If someone opens a .txt file, we don't want to try parsing it as C#.
    /// </description></item>
    /// <item><description>
    /// <strong>Path Guard:</strong> When we save the generated .cs file, the file system fires
    /// a change event. We explicitly ignore those because they're derived files, not sources.
    /// </description></item>
    /// <item><description>
    /// <strong>Loop Guard:</strong> If we receive a change event for a file we're already processing,
    /// we know it's "noise" from the file system and can safely ignore it.
    /// </description></item>
    /// <item><description>
    /// <strong>Debounce:</strong> Users type fast. We don't want to parse on every single keystroke.
    /// Waiting 200ms batches multiple keystrokes into one parse.
    /// </description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Scenario 1: User edits Utilities.xcs
    /// FileEventListeners("C:\Project\Utilities.xcs")
    /// → Guard 1 passes (ends with .xcs)
    /// → Guard 2 passes (not in Generated/)
    /// → Guard 3 passes (not in _processingFiles)
    /// → Debounce: start 200ms timer
    /// → After 200ms with no more changes: parse the file
    /// 
    /// // Scenario 2: User creates a new .txt file
    /// FileEventListeners("C:\Project\Notes.txt")
    /// → Guard 1 fails (ends with .txt, not .xcs)
    /// → Return immediately, do nothing
    /// 
    /// // Scenario 3: We're transforming Utilities.xcs, user makes another change
    /// FileEventListeners("C:\Project\Utilities.xcs")  // [2nd call while processing]
    /// → Guard 1 passes (ends with .xcs)
    /// → Guard 2 passes (not in Generated/)
    /// → Guard 3 fails (Utilities.xcs is in _processingFiles)
    /// → Return immediately, ignore this duplicate change event
    /// </code>
    /// </example>
    private string FileEventListeners(string file)
    {
        // ========================================
        // GUARD 1: Extension Check
        // ========================================
        // Only parse .xcs files (XML-embedded C# files)
        // Skip .cs, .txt, .json, or any other file type
        if (!file.EndsWith(".xcs", StringComparison.OrdinalIgnoreCase)) return file;

        // ========================================
        // GUARD 2: Path Check (Generated Folder)
        // ========================================
        // Never process files in the Generated/ folder
        // These are auto-generated files, not sources we should transform
        if (file.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}")) return file;

        // ========================================
        // GUARD 3: Loop Prevention (Processing Gate)
        // ========================================
        // If we are already mid-transform for this file, ignore this change event
        // It's just "noise" from the file system ripples caused by our own processing
        if (_processingFiles.ContainsKey(file)) return file;

        // ========================================
        // GUARD 4: Debouncing
        // ========================================
        // If there's an existing debounce timer for this file, cancel it
        // We're starting a new timer, so the old one is irrelevant
        if (_debounceTokens.TryGetValue(file, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        // Create a new cancellation token for this debounce cycle
        var cts = new CancellationTokenSource();
        _debounceTokens[file] = cts;

        // Start a background task that waits 200ms before parsing
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait 200ms before proceeding
                // If another change event arrives for this file during this time,
                // the next FileEventListeners call will cancel this token
                await Task.Delay(200, cts.Token);
                
                // If we were cancelled while waiting, stop and return
                // This happens when a new change event arrives
                if (cts.Token.IsCancellationRequested) return;

                // Lock the gate before starting the parse/transform chain
                // Add this file to _processingFiles so we ignore any duplicate change events
                _processingFiles.TryAdd(file, 0);
                
                // Now parse and process the file
                ProcessFile(file);
            }
            catch (TaskCanceledException) 
            { 
                // The debounce was cancelled because a new change arrived
                // This is expected and normal - just return silently
            }
            catch (Exception ex)
            {
                // Something unexpected went wrong
                // Remove from processing gate and log the error
                _processingFiles.TryRemove(file, out _);
                System.Console.Error.WriteLine($"[ROSLYN ERROR]: {ex.Message}");
            }
        }, cts.Token);

        return file;
    }

    /// <summary>
    /// Reads a file, parses it as C# code, and dispatches the parsed syntax tree.
    /// </summary>
    /// <param name="file">The file path to process.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This method does the actual parsing work:
    /// <list type="number">
    /// <item><description>Reads the file content from disk (or URI)</description></item>
    /// <item><description>Checks if the file is empty</description></item>
    /// <item><description>Parses the C# code into a Syntax Tree (AST) using Roslyn</description></item>
    /// <item><description>Dispatches the parsed syntax tree to the transformation service</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Why Syntax Trees?:</strong>
    /// A syntax tree is Roslyn's internal representation of code as a tree structure.
    /// Instead of "just text," Roslyn understands:
    /// <list type="bullet">
    /// <item><description>Class definitions and their members</description></item>
    /// <item><description>Method calls and expressions</description></item>
    /// <item><description>Where XML blocks are located (for the transformation service)</description></item>
    /// </list>
    /// The TransformationService uses this tree to locate XML expressions and transform them.
    /// </para>
    /// <para>
    /// <strong>Error Handling:</strong>
    /// If the file is empty, we remove it from _processingFiles and return.
    /// This prevents getting stuck in the processing gate.
    /// If parsing fails, the exception propagates up to FileEventListeners,
    /// which logs it and cleans up the gate.
    /// </para>
    /// </remarks>
    private void ProcessFile(string file)
    {
        // Read the file content
        // This might be from disk or from a URI (depending on the IDE's representation)
        string content = _fileService.GetFileContent(file);
        
        // If the file is empty, there's nothing to parse
        if (string.IsNullOrEmpty(content))
        {
            // Remove from processing gate since we're done
            _processingFiles.TryRemove(file, out _);
            return;
        }

        // Parse the C# code into a syntax tree (AST)
        // Roslyn converts "text" into a tree structure that represents the code's meaning
        var parser = CSharpSyntaxTree.ParseText(content);
        
        // Dispatch the syntax tree to the transformation service
        // The transformation service will use this to find XML expressions and generate C# code
        Events.Roslyn.FileParsed.Dispatch((file, parser));
    }
}