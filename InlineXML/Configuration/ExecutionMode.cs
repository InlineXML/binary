namespace InlineXML.Configuration;

/// <summary>
/// defines the operational context of the application. 
/// this enum drives the initialization of the various service "heads" 
/// (CLI, LSP, etc.) ensuring the system only allocates resources 
/// necessary for the current task.
/// </summary>
public enum ExecutionMode : byte
{
	/// <summary>
	/// Useful for testing the output of something internally.
	/// this mode typically enables verbose logging and internal
	/// state inspection that you wouldn't want in production.
	/// </summary>
	DeveloperMode,
    
	/// <summary>
	/// Useful in CLI contexts, such as creating a watcher or
	/// build, in order to not lock people out of this utility
	/// while not having an IDE plugin for their choice IDE,
	/// we can allow a "watch" mode, but of course in this
	/// instance, there will be no code completion/ intellisense
	/// etc...
	/// </summary>
	CommandLine,
    
	/// <summary>
	/// So, for support from IDEs, we allow them to run in this
	/// mode, this needs to support all the required endpoints
	/// in the specification. All functionality should come
	/// from Roslyn, it's battle tested and just works. The
	/// only thing we need to do is keep track of source
	/// mapping.
	/// </summary>
	LanguageServerProtocol,

	/// <summary>
	/// a specialized headless mode for continuous integration.
	/// unlike the standard command line, this mode treats all
	/// warnings as errors and produces machine-readable output 
	/// (like JSON or JUnit) for build pipeline consumption.
	/// </summary>
	ContinuousIntegration,

	/// <summary>
	/// a "dry-run" mode that performs the full transformation 
	/// and diagnostic pipeline in memory without writing any 
	/// generated files to the disk. 
	/// </summary>
	DiagnosticOnly
}