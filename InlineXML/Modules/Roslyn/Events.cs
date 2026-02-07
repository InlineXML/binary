using Microsoft.CodeAnalysis;

namespace InlineXML.Modules.Eventing;

/// <summary>
/// Contains file parsing and analysis events.
/// </summary>
/// <remarks>
/// <para>
/// This partial class provides event dispatchers for file parsing operations.
/// Use these events to hook into the parsing pipeline and perform analysis or
/// processing on parsed code.
/// </para>
/// </remarks>
public static partial class Events
{
	/// <summary>
	/// Any Roslyn shared event.
	/// </summary>
	public static readonly RoslynEvents Roslyn = new();
}

public class RoslynEvents
{
	/// <summary>
	/// Dispatched when a file has been parsed into a syntax tree.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This event fires after a file has been successfully parsed by Roslyn and converted
	/// into a <see cref="SyntaxTree"/>. The event data is the parsed syntax tree representing
	/// the file's structure.
	/// </para>
	/// <para>
	/// Use this event to perform semantic analysis, validation, transformation, or any other
	/// operations that require access to the parsed syntax tree of a file.
	/// </para>
	/// </remarks>
	public readonly EventGroup<(string, SyntaxTree)> FileParsed = new();
}