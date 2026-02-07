using InlineXML.Configuration;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;

namespace InlineXML.Modules.LanguageServer;

/// <summary>
/// the language server service acts as the communication hub between 
/// the IDE and our transpiler. it handles the LSP lifecycle, ensuring
/// that features like autocomplete, hover, and diagnostics are 
/// delivered to the user as they type.
/// </summary>
public class LanguageServerService : AbstractService
{
	public LanguageServerService()
	{
		// we wait until the DI container has finished instancing all 
		// services before we attempt to spin up the server. this 
		// prevents race conditions where the IDE might request a 
		// transformation before the transformer is actually online.
		Events.AfterAllServicesReady.AddEventListener(mode =>
		{
			// we only boot the server logic if the application has 
			// specifically been started in LSP mode.
			if (mode is ExecutionMode.LanguageServerProtocol)
			{
				StartupServer();
			}
          
			return mode;
		});
	}

	/// <summary>
	/// initializes the LSP connection and begins listening for 
	/// JSON-RPC messages from the client.
	/// </summary>
	private void StartupServer()
	{
		// the implementation here will eventually handle the 
		// standard LSP handshake (Initialize, Initialized) 
		// and wire up our internal events to the IDE's UI.
	}
}