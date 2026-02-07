namespace InlineXML.Modules.DI;

/// <summary>
/// serves as the base architectural anchor for the system.
/// every module—from the parser to the workspace manager—must 
/// inherit from this class to be recognized and managed by the 
/// central service container.
/// </summary>
public abstract class AbstractService
{
	// this class acts as a marker in our type system, ensuring 
	// that only validated service objects can be injected into 
	// constructors or cached in the service registry.
}