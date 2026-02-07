using InlineXML.Configuration;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;

namespace InlineXML.Modules.Console;

/// <summary>
/// the console service provides the command-line interface for the tool.
/// it handles bulk transpilation tasks, such as building an entire 
/// project at once or running as part of a CI/CD pipeline.
/// </summary>
public class ConsoleService : AbstractService
{
	public ConsoleService()
	{
		// we wait for the DI container to finish booting. once every
		// service is registered and the event bus is hot, we check
		// if the user wants to run this as a standard CLI tool.
		Events.AfterAllServicesReady.AddEventListener(mode =>
		{
			if (mode is ExecutionMode.CommandLine)
			{
				StartupCommandLine();
			}
          
			return mode;
		});
	}

	/// <summary>
	/// handles the logic for terminal-based execution, such as
	/// parsing command line arguments and initiating a full
	/// workspace scan and transformation.
	/// </summary>
	private void StartupCommandLine()
	{
		// this is where we'd trigger a full build cycle, 
		// outputting progress to the terminal and exiting
		// with a success or error code.
	}
}