using InlineXML.Configuration;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;

namespace InlineXML.Modules.Console;

public class ConsoleService : AbstractService
{
	public ConsoleService()
	{
		Events.AfterAllServicesReady.AddEventListener(mode =>
		{
			if (mode is ExecutionMode.CommandLine)
			{
				StartupCommandLine();
			}
			
			return mode;
		});
	}

	private void StartupCommandLine()
	{
		
	}
}