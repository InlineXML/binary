namespace InlineXML.Modules.InlineXml;

/// <summary>
/// a token represents a meaningful segment of the source text.
/// it doesn't store the string itself—instead, it tracks the
/// kind of token and its position, allowing the rest of the
/// pipeline to slice the source span efficiently.
/// </summary>
public struct Token
{
	/// <summary>
	/// the category of the token (e.g., TAG_OPEN, ATTRIBUTE_NAME).
	/// this tells the AST builder how to interpret the text 
	/// found at this specific location.
	/// </summary>
	public TokenKind Kind;

	/// <summary>
	/// the character offset in the source text where this token begins.
	/// </summary>
	public int Start;
    
	/// <summary>
	/// the character offset in the source text where this token ends.
	/// </summary>
	public int End;
}