namespace InlineXML.Modules.Eventing;

public static partial class Events
{
	/// <summary>
	/// provides access to all events related to code analysis, compiler diagnostics, 
	/// and source-mapped error reporting.
	/// </summary>
	public static readonly DiagnosticEventMap Diagnostics = new();
}

/// <summary>
/// encapsulates the event groups used to communicate the state of the 
/// diagnostic pipeline. this allows the system to decouple the heavy 
/// roslyn compilation logic from the UI/broadcast logic.
/// </summary>
public class DiagnosticEventMap
{
	/// <summary>
	/// triggered when a file has completed its diagnostic pass. 
	/// the payload contains the path to the original .xcs source file, 
	/// signaling that translated diagnostics are now available in the 
	/// store and ready for broadcast to the client.
	/// </summary>
	public readonly EventGroup<string> FileScanned = new();
}