namespace InlineXML.Modules.Eventing;

/// <summary>
/// Contains event-related utilities and functionality for the application.
/// </summary>
public static partial class Events
{
	/// <summary>
	/// Gets the workspace events dispatcher for handling workspace-related events.
	/// </summary>
	/// <remarks>
	/// Use this to subscribe to or dispatch workspace events such as file registration,
	/// configuration changes, and other workspace lifecycle events.
	/// </remarks>
	public static readonly WorkspaceEvents Workspace = new();
}

/// <summary>
/// Provides event dispatchers for workspace-related operations.
/// </summary>
/// <remarks>
/// <c>WorkspaceEvents</c> contains a collection of event groups that allow listeners to
/// subscribe to and react to various workspace events. This is used for coordinating
/// operations across different modules in response to workspace state changes.
/// </remarks>
public class WorkspaceEvents
{
	/// <summary>
	/// Dispatches when a file is registered within the workspace.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This event is triggered whenever a new file is registered in the workspace.
	/// The event data is a string containing the file path or identifier.
	/// </para>
	/// <para>
	/// Listeners can use this event to perform initialization, indexing, or other
	/// operations dependent on file registration.
	/// </para>
	/// </remarks>
	public readonly EventGroup<string> FileRegistered = new();
	
	/// <summary>
	/// Dispatches when a file that's already registered changes.
	/// </summary>
	public readonly EventGroup<string> FileChanged = new();
	
	/// <summary>
	/// Dispatches when a file has been removed from the workspace,
	/// The second item in the tuple is for the mapped output file
	/// for files that have undergone transformation.
	/// </summary>
	public readonly EventGroup<(string, string)> FileRemoved = new();
}