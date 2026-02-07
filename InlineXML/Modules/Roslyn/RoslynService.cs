using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using Microsoft.CodeAnalysis.CSharp;

namespace InlineXML.Modules.Roslyn;

/// <summary>
/// the roslyn service acts as the gateway between the physical file system
/// and our transformation logic. it leverages the microsoft code analysis
/// library to turn raw text into structured syntax trees that we can query.
/// </summary>
public class RoslynService : AbstractService
{
    public RoslynService()
    {
       // we tap into the workspace events to ensure that as soon as a file
       // is registered—either via a manual add or an initial scan—we
       // begin the process of understanding its structure.
       Events.Workspace.FileRegistered.AddEventListener(FileEventListeners);
       
       // we also listen for changes. this allows for a "live" development
       // experience where saving an XCS file instantly triggers a 
       // re-generation of the corresponding C# code.
       Events.Workspace.FileChanged.AddEventListener(FileEventListeners);
    }

    /// <summary>
    /// handles the core logic for reading an XCS file and producing
    /// a roslyn syntax tree. this tree is then dispatched to the rest
    /// of the system for expression locating and transformation.
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    private string FileEventListeners(string file)
    {
       // we're only interested in .xcs files. if anything else leaks
       // through the event bus, we ignore it and return the file path
       // for the next listener in the chain.
       if (!file.EndsWith(".xcs"))
       {
          return file;
       }
          
       // we read the file content and pass it to the roslyn parser.
       // even though XCS contains our custom XML-like syntax, we parse
       // it as standard C# first so we can use Roslyn's powerful 
       // trivia and span detection to find our custom blocks.
       var parser = CSharpSyntaxTree.ParseText(File.ReadAllText(file));

       // once the syntax tree is ready, we dispatch it. 
       // this is the signal for the TransformationService to begin
       // looking for those pesky XML expressions.
       Events.Roslyn.FileParsed.Dispatch((file, parser));
          
       return file;
    }
}