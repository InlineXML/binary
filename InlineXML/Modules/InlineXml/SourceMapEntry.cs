namespace InlineXML.Modules.InlineXml;

/// <summary>
/// the source map entry is the bridge between our custom XCS syntax 
/// and the generated C#. it allows us to map character positions 
/// from the transformed output back to the original source code, 
/// ensuring that diagnostics and squiggles appear in the right place.
/// </summary>
public struct SourceMapEntry
{
	/// <summary>
	/// the starting character offset in the original .xcs file.
	/// </summary>
	public int OriginalStart { get; set; }

	/// <summary>
	/// the ending character offset in the original .xcs file.
	/// </summary>
	public int OriginalEnd { get; set; }

	/// <summary>
	/// the starting character offset in the generated .cs file.
	/// </summary>
	public int TransformedStart { get; set; }

	/// <summary>
	/// the ending character offset in the generated .cs file.
	/// </summary>
	public int TransformedEnd { get; set; }
}