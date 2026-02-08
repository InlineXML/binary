using InlineXML.Configuration;
using InlineXML.Modules.DI;
using InlineXML.Modules.Eventing;
using InlineXML.Modules.LanguageServer;
using InlineXML.Modules.Routing;
using InlineXML.Modules.Workspace;

namespace InlineXML;

/// <summary>
/// The entry point for the InlineXML engine. This class handles the initial 
/// bootstrap logic, determining whether the application should run as a 
/// CLI tool or as a persistent Language Server.
/// </summary>
class Program
{
    /// <summary>
    /// The main execution loop. Now asynchronous to support the persistent
    /// nature of the Language Server Protocol.
    /// </summary>
    static async Task Main(string[] args)
    {
       // by default, we run command line.
       var mode = ExecutionMode.CommandLine;
       
       // let's check for a language server flag. 
       if (args.Contains("--lsp"))
       {
          // change execution mode to LSP. 
          mode = ExecutionMode.LanguageServerProtocol;
       }

       // Developer mode allows for local testing and service dumping
       if (args.Contains("--dev"))
       {
          Services.InstanceAll(ExecutionMode.DeveloperMode);
          return;
       }

       // next check for the --workspace arg, 
       // we can then get its index and check the next value
       var workspace = GetArgumentValue(args, "--workspace");

       // we know it has got to be a string, and its none-null, but 
       // just because it's a valid string, doesn't mean it's a valid file path.
       if (!Directory.Exists(workspace))
       {
          // We use Console.Error because stdout is reserved for LSP JSON-RPC traffic.
          Console.Error.WriteLine($"[Fatal Error] The workspace you supplied does not exist: {workspace}");
          return;
       }
       
       // Handle to the LSP service so we can await its lifecycle
       LanguageServerService? activeLangSvc = null;

       // set the workspace when all services are ready. 
       Events.ServiceReady.AddEventListener(service =>
       {
          if (service is WorkspaceService workspaceSvc)
          {
             workspaceSvc.SetWorkspace(workspace);
          }

          // If we are spinning up the LSP service, we capture it to 
          // prevent the Main thread from exiting prematurely.
          if (service is LanguageServerService langSvc)
          {
             activeLangSvc = langSvc;
          }

          return service;
       });
       
       // beyond this point, we shouldn't be terminating the process via exceptions.
       // we can still use them, but they must be caught and handled nicely by the Router. 
       Services.InstanceAll(mode);

       // THE LIFELONG ANCHOR:
       // If we are in LSP mode, we block the Main thread until the IDE 
       // severs the connection (stdin/stdout pipe).
       if (mode == ExecutionMode.LanguageServerProtocol)
       {
	       // Resolve the RoutingService from your DI container
	       var router = Services.Get<RoutingService>();
    
	       Console.Error.WriteLine("[SYSTEM] LSP Loop Starting. Process is now anchored.");
    
	       // This is the critical line. It blocks Main until the stdin stream is closed.
	       await router.ListenAsync(); 
    
	       Console.Error.WriteLine("[SYSTEM] Stdin closed. LSP Loop terminated.");
       }
    }
    
    /// <summary>
    /// Utility function that validates a value exists in the args, and that it is not empty.
    /// It ensures that paths with spaces are correctly unquoted.
    /// </summary>
    /// <param name="args">The raw command line arguments.</param>
    /// <param name="argumentName">The flag to look for (e.g. --workspace).</param>
    /// <returns>The string value following the flag.</returns>
    private static string GetArgumentValue(string[] args, string argumentName)
    {
       if (args == null || args.Length == 0)
       {
          throw new ArgumentException("Arguments array is null or empty.", nameof(args));
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

       // We trim the quotes because our Executor.js might wrap the path for safety
       return value.Trim('"');
    }
}