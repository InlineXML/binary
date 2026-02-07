using InlineXML.Configuration;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;

namespace InlineXML.Modules.LanguageServer;

public class LanguageServerService : AbstractService
{
	public LanguageServerService()
	{
		Events.AfterAllServicesReady.AddEventListener(mode =>
		{
			if (mode is ExecutionMode.LanguageServerProtocol)
			{
				StartupServer();
			}
			
			return mode;
		});
	}

	private void StartupServer()
	{
		
	}
}