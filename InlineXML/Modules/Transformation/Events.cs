using InlineXML.Modules.Transformation;

namespace InlineXML.Modules.Eventing;

/// <summary>
/// the global event registry for the InlineXML system.
/// we use a partial class approach here to keep our event categories
/// cleanly separated while maintaining a single point of access 
/// for the rest of the application.
/// </summary>
public static partial class Events
{
	/// <summary>
	/// entry point for all events related to the transformation pipeline.
	/// this allows consumers to subscribe to the lifecycle of the
	/// transpiler (Parser -> AST -> Generator).
	/// </summary>
	public static readonly TransformerEventMap Transformer = new();
}

/// <summary>
/// defines the specific groups of events that the transformer can emit.
/// by mapping these to specific payload types, we ensure type-safety
/// across our decoupled service architecture.
/// </summary>
public class TransformerEventMap
{
	/// <summary>
	/// this event fires whenever a file has been successfully 
	/// transpiled from XCS into legal C#. the payload contains 
	/// the new source text and the source maps required for 
	/// diagnostic translation.
	/// </summary>
	public readonly EventGroup<FileTransformedPayload> FileTransformed = new();
}