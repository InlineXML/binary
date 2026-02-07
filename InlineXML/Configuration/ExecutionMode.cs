namespace InlineXML.Configuration;

public enum ExecutionMode : byte
{
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
}