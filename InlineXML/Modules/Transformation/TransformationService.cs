using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.Roslyn;
using Microsoft.CodeAnalysis.Text;

namespace InlineXML.Modules.Transformation;

public class TransformationService : AbstractService
{
	public TransformationService()
	{
		
		Events.Workspace.FileRemoved.AddEventListener(ev =>
		{
			// get the file that's been removed, and potentially its
			// transformation target.
			var (_, transformed) = ev;

			// if it doesn't have a transformation, 
			// we can skip out.
			if (string.IsNullOrEmpty(transformed))
			{
				return ev;
			}
			
			// however here, we know there's a file to clean up.
			// so we clean it up, the reason we're doing this here. 
			// imagine you're in an IDE, you've just deleted a .xcs
			// file, the generated file stays behind. If you know
			// your way around this project, you'll probably understand
			// that it's just a stale artifact that's remained, but a new
			// user to this format, will probably be oblivious to how
			// this actually works, they're probably just experimenting
			// and won't understand how this works under the hood.
			File.Delete(transformed);
			
			// contractually, return the event for the next consumer
			return ev;
		});
		
		// once a file has been parsed
		Events.Roslyn.FileParsed.AddEventListener(ev =>
		{
			// destructure the payload of the event.
			var (file, syntaxTree) = ev;

			// if the current file isn't a ".xcs" file
			// return the event for processing by other consumers
			// we're not interested in this particular
			// syntax tree.
			if (!file.EndsWith(".xcs"))
			{
				return ev;
			}
			
			// here we've got XCS file to handle, XCS files allow 
			// inline XML to describe tree structures, the promise
			// we need to give to consumers of this tool, is that
			// we aren't locking down what this structure can represent.
			// As much as it is like JSX, it's a) not a JSX clone, and
			// b) to be used to show a declarative structure of something.
			
			var expressions = ExpressionLocator.FindExpressions(syntaxTree);

			foreach (var (start, end) in expressions)
			{
				// Get the actual source text for this expression
				var sourceText = syntaxTree.GetText();
				var expressionText = sourceText.ToString(new TextSpan(start, end - start));
    
				// Get line/column info
				var (line, col) = ExpressionLocator.GetLineColumn(syntaxTree, start);
    
				// Do something with the expression...
			}
			
			return ev;
		});
	}
}