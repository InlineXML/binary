using System.Collections.Concurrent;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using Microsoft.CodeAnalysis;

namespace InlineXML.Modules.Workspace;

public class WorkspaceService : AbstractService
{
    /// <summary>
    /// the absolute path to the root of the project.
    /// this allows us to anchor all our relative path logic 
    /// and ensures we are always pointing to the correct 
    /// directories regardless of where the process was started.
    /// </summary>
    public string ProjectRoot { get; private set; }

    /// <summary>
    /// Key: Original .xcs file path (normalized)
    /// Value: Metadata containing target path, transformed content, and source maps.
    /// we use a concurrent dictionary here to ensure that as files are processed
    /// in parallel, we don't run into any race conditions when updating the
    /// shadow state of the workspace.
    /// </summary>
    private ConcurrentDictionary<string, FileMetadata> _fileStore = [];

    public WorkspaceService()
    {
       // once a file has been transformed into legal C#
       Events.Transformer.FileTransformed.AddEventListener(payload =>
       {
          // Normalize the path to ensure we have a consistent key for our store
          var normalizedPath = Path.GetFullPath(payload.File).Replace('/', Path.DirectorySeparatorChar);

          // if the current file isn't already in our store
          // we need to register it so we know where the 
          // generated output should live.
          if (!_fileStore.TryGetValue(normalizedPath, out var metadata))
          {
             RegisterFile(normalizedPath, false);
             if (!_fileStore.TryGetValue(normalizedPath, out metadata))
             {
                throw new InvalidOperationException("Something unknown happened during file registration.");
             }
          }

          // update the metadata with the latest transformation results.
          // this keeps our source maps in sync with the actual content
          // currently sitting on the disk.
          metadata.TransformedContent = payload.Content;
          metadata.SourceMaps = payload.SourceMaps;

          // 1. Commit to disk so the compiler can find the legal C#
          // we ensure the directory exists first, then write the 
          // legal C# code to the "Generated" folder.
          Directory.CreateDirectory(Path.GetDirectoryName(metadata.TargetPath)!);
          File.WriteAllText(metadata.TargetPath, payload.Content);

          // 2. Dispatch that the file is updated and ready for diagnostic checks
          // this lets other services know they can now perform validation
          // against the newly written C# file.
          Events.Workspace.FileChanged.Dispatch(normalizedPath);

          return payload;
       });
    }
    
    /// <summary>
    /// sets the current workspace by scanning the directory for all
    /// XCS files. once found, it registers them and dispatches a
    /// parse request to ensure the generated folder is fully populated.
    /// </summary>
    /// <param name="dir"></param>
    public void SetWorkspace(string dir)
    {
	    // normalize the project root based on the provided directory
	    this.ProjectRoot = Path.GetFullPath(dir).Replace('/', Path.DirectorySeparatorChar);

	    // we search recursively for any .xcs files within the project root.
	    // this ensures that even deeply nested components are discovered
	    // and registered in our shadow state.
	    var xcsFiles = Directory.GetFiles(ProjectRoot, "*.xcs", SearchOption.AllDirectories);

	    foreach (var file in xcsFiles)
	    {
		    // normalize the path before we start working with it
		    var normalizedPath = Path.GetFullPath(file).Replace('/', Path.DirectorySeparatorChar);

		    // we need to make sure the file is actually inside a 'components'
		    // folder before we try to register it, as our convention
		    // requires this structure for I/O mapping.
		    if (normalizedPath.Contains(Path.Combine("components", ""), StringComparison.OrdinalIgnoreCase))
		    {
			    // 1. Register the file to set up the mapping to the 'Generated' folder
			    RegisterFile(normalizedPath, false);
		    }
	    }

	    // finally, we notify the rest of the system that the workspace
	    // has been initialized and all initial files have been queued.
	    Events.Workspace.WorkspaceChanged.Dispatch(ProjectRoot);
    }
    
    /// <summary>
    /// Registers a file inside the mappings for I/O, ready for usage elsewhere.
    /// </summary>
    /// <param name="fileName"></param>
    /// <param name="dispatch"></param>
    /// <exception cref="ArgumentException"></exception>
    public void RegisterFile(string fileName, bool dispatch = true)
    {
       // Normalize path separators and ensure we have an absolute path
       var normalizedPath = Path.GetFullPath(fileName).Replace('/', Path.DirectorySeparatorChar);

       // if we've already got this file registered, we don't
       // need to do anything further.
       if (_fileStore.ContainsKey(normalizedPath))
       {
          return;
       }

       // we check to see if the file is actually within our 
       // project root. if it isn't, we shouldn't be 
       // trying to manage it here.
       if (!normalizedPath.StartsWith(ProjectRoot, StringComparison.OrdinalIgnoreCase))
       {
          throw new ArgumentException("File must be located within the project root.", nameof(fileName));
       }

       // Find the "components" folder in the path to determine
       // where the generated file should be placed relative to 
       // the project root.
       var componentsIndex = normalizedPath.IndexOf(Path.Combine("components", ""), StringComparison.OrdinalIgnoreCase);
       if (componentsIndex == -1)
       {
          throw new ArgumentException("Path must contain a 'components' folder.", nameof(fileName));
       }

       // Extract the relative path after 'components/' to preserve
       // the directory structure inside the 'Generated' folder.
       var relativePath = normalizedPath[(componentsIndex + "components".Length + 1)..];

       // Replace the original extension (.xcs) with .cs for the legal C# output
       var generatedFileName = Path.ChangeExtension(relativePath, ".cs");

       // Build the target path under the 'Generated' folder relative to the root
       var targetPath = Path.Combine(ProjectRoot, "Generated", generatedFileName);

       // Initialize the entry with the target path. The content and maps
       // will be populated once the transformer has finished its work.
       _fileStore[normalizedPath] = new FileMetadata { TargetPath = targetPath };

       if (dispatch)
       {
          Events.Workspace.FileRegistered.Dispatch(normalizedPath);
       }
    }
    
	/// <summary>
    /// retrieves all necessary metadata references in a version-agnostic way.
    /// instead of hardcoding paths, we resolve the location of the core 
    /// runtime libraries dynamically and include all assemblies loaded 
    /// in the current execution context.
    /// </summary>
    public IEnumerable<MetadataReference> GetProjectReferences()
    {
	    var references = new Dictionary<string, MetadataReference>();

	    // 1. DYNAMIC CORE RESOLUTION (Safe & Version Agnostic)
	    // We grab the directory where the current 'Object' (CoreLib) lives.
	    var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
	    if (coreDir != null)
	    {
		    // We sweep the core directory for all system DLLs.
		    foreach (var dll in Directory.GetFiles(coreDir, "System.*.dll"))
		    {
			    var name = Path.GetFileName(dll);
			    references[name] = MetadataReference.CreateFromFile(dll);
		    }
	    }

	    // 2. DISCOVERY RESOLUTION (User-Specific DLLs)
	    // We look for anything in the user's output folders. 
	    // We use a recursive search to avoid hardcoding "net8.0" or "Debug".
	    if (Directory.Exists(ProjectRoot))
	    {
		    // We look for 'bin' folders which contain the compiled dependencies
		    var binPath = Path.Combine(ProjectRoot, "bin");
		    if (Directory.Exists(binPath))
		    {
			    var allDlls = Directory.GetFiles(binPath, "*.dll", SearchOption.AllDirectories);
			    foreach (var dll in allDlls)
			    {
				    var name = Path.GetFileName(dll);
				    // We only add it if we haven't already added a system version
				    if (!references.ContainsKey(name))
				    {
					    references[name] = MetadataReference.CreateFromFile(dll);
				    }
			    }
		    }
	    }

	    return references.Values;
    }

    /// <summary>
    /// Public API for the Diagnostic service to translate positions.
    /// this allows us to take a diagnostic from a generated C# file
    /// and map it back to the original XCS source code.
    /// </summary>
    /// <param name="originalFile"></param>
    /// <returns></returns>
    public FileMetadata GetMetadata(string originalFile) 
    {
        _fileStore.TryGetValue(originalFile, out var meta);
        return meta;
    }

    /// <summary>
    /// Helps finding the original XCS file path from a generated CS path.
    /// this is particularly useful when Roslyn reports an error in a 
    /// generated file and we need to find the XCS owner.
    /// </summary>
    /// <param name="generatedPath"></param>
    /// <returns></returns>
    public string FindOriginalFile(string generatedPath)
    {
        var normalizedGenerated = Path.GetFullPath(generatedPath).Replace('/', Path.DirectorySeparatorChar);
        return _fileStore.FirstOrDefault(x => x.Value.TargetPath.Equals(normalizedGenerated, StringComparison.OrdinalIgnoreCase)).Key;
    }
}