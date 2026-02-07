using InlineXML.Modules.InlineXml;

namespace InlineXML.Modules.Workspace;

/// <summary>
/// the file metadata acts as a shadow state for our transformed files.
/// it holds everything required to bridge the gap between the original
/// XCS source code and the generated legal C# sitting on the disk.
/// </summary>
public class FileMetadata
{
	/// <summary>
	/// the absolute path to where the generated .cs file is stored.
	/// this allows us to quickly resolve where the legal C# lives
	/// when the workspace needs to perform I/O operations.
	/// </summary>
	public string TargetPath { get; set; }

	/// <summary>
	/// the full, transformed C# content. storing this in memory
	/// allows for rapid access during the diagnostic phase without
	/// having to constantly hit the disk for file reads.
	/// </summary>
	public string TransformedContent { get; set; }

	/// <summary>
	/// the collection of source map entries for this specific file.
	/// this is the dictionary we use to translate compiler errors
	/// from the generated output back to the user's original XML.
	/// </summary>
	public List<SourceMapEntry> SourceMaps { get; set; }
}