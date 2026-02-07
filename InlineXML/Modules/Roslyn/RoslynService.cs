using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using Microsoft.CodeAnalysis.CSharp;

namespace InlineXML.Modules.Roslyn;

public class RoslynService : AbstractService
{
	public RoslynService()
	{
		// Here we can tap into files being added, that way we can 
		// instantly transform it, and write it to the target file. 
		Events.Workspace.FileRegistered.AddEventListener(FileEventListeners);
		
		// The same goes for changes. 
		Events.Workspace.FileChanged.AddEventListener(FileEventListeners);
	}

	// Here we can tap into files being added, or changed that way we can 
	// instantly transform it, and write it to the target file. 
	private string FileEventListeners(string file)
	{
		// we're only interested in .XCS files.
		if (!file.EndsWith(".xcs"))
		{
			return file;
		}
			
		// otherwise, now we do something.
		var parser = CSharpSyntaxTree.ParseText(File.ReadAllText(file));

		// emit the file to allow processing elsewhere.
		Events.Roslyn.FileParsed.Dispatch(parser);
			
		return file;
	}
}