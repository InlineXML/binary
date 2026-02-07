using InlineXML.Configuration;
using InlineXML.Modules.DI;

namespace InlineXML;

class Program
{
	static void Main(string[] args)
	{
		// by default, we run command line.
		var mode = ExecutionMode.CommandLine;
		
		// let's check for a language server flag. 
		if (args.Contains("--lsp"))
		{
			// change execution mode to LSP. 
			mode = ExecutionMode.LanguageServerProtocol;
		}

		if (args.Contains("--dev"))
		{
			Services.InstanceAll(ExecutionMode.DeveloperMode);
			return;
		}

		// next check for the --workspace arg, 
		// we can then get its index and check the next value
		// if there is one, this null checks etc... 
		var workspace = GetArgumentValue(args, "--workspace");

		// we know it has got to be a string, and its none-null, but 
		// just because it's a valid string, doesn't mean it's a valid file
		// path.
		if (!Directory.Exists(workspace))
		{
			throw new InvalidOperationException("The workspace you supplied does not exist.");
		}
		
		// beyond this point, we shouldn't be terminating the process via exceptions.
		// we can still use them, but they must be caught and handled nicely. 
		Services.InstanceAll(mode);
	}
	
	/// <summary>
	/// Utility function that validates a value exists in the args, and that it is not empty.
	/// </summary>
	/// <param name="args"></param>
	/// <param name="argumentName"></param>
	/// <returns></returns>
	/// <exception cref="ArgumentException"></exception>
	/// <exception cref="InvalidOperationException"></exception>
	private static string GetArgumentValue(string[] args, string argumentName)
	{
		if (args == null || args.Length == 0)
		{
			throw new ArgumentException("Arguments array is null or empty.", nameof(args));
		}
    
		if (string.IsNullOrWhiteSpace(argumentName))
		{
			throw new ArgumentException("Argument name cannot be null or empty.", nameof(argumentName));
		}
    
		var index = Array.IndexOf(args, argumentName);
    
		if (index == -1)
		{
			throw new InvalidOperationException($"Argument '{argumentName}' not found.");
		}
    
		if (index == args.Length - 1)
		{
			throw new InvalidOperationException($"Argument '{argumentName}' has no value.");
		}
    
		var value = args[index + 1];
    
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new InvalidOperationException($"Argument '{argumentName}' has an empty or whitespace value.");
		}
    
		return value;
	}
}