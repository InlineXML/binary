using System.Collections.Concurrent;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;

namespace InlineXML.Modules.Workspace;

public class WorkspaceService : AbstractService
{
	/// <summary>
	/// Basically, any XCS file that comes through here has a
	/// generated legal C# it stores somewhere dependent on config.
	/// the key is the original file name, the value is the transformed file location,
	/// we also make it concurrent for obvious reasons.
	/// </summary>
	private ConcurrentDictionary<string, string> _fileMappings = [];
	
	/// <summary>
	/// Registers a file inside the mappings for I/O, ready for usage elsewhere.
	/// </summary>
	/// <param name="fileName"></param>
	/// <exception cref="ArgumentException"></exception>
	public void RegisterFile(string fileName)
	{
		// first, make sure this has been registered.
		if (!_fileMappings.ContainsKey(fileName))
		{
			return;
		}
		// Normalize path separators
		var normalizedPath = Path.GetFullPath(fileName).Replace('/', Path.DirectorySeparatorChar);

		// Find the "components" folder in the path
		var componentsIndex =
			normalizedPath.IndexOf(Path.Combine("components", ""), StringComparison.OrdinalIgnoreCase);
		if (componentsIndex == -1)
		{
			throw new ArgumentException("Path must contain a 'components' folder.", nameof(fileName));
		}

		// Extract the relative path after 'components/'
		var relativePath = normalizedPath[(componentsIndex + "components".Length + 1)..];

		// Replace the original extension (.xcs) with .cs
		var generatedFileName = Path.ChangeExtension(relativePath, ".cs");

		// Build the target path under 'Generated'
		var projectRoot = normalizedPath[..componentsIndex]; // part before 'components'
		var targetPath = Path.Combine(projectRoot, "Generated", generatedFileName);

		_fileMappings[normalizedPath] = targetPath;

		Events.Workspace.FileRegistered.Dispatch(normalizedPath);
	}

}