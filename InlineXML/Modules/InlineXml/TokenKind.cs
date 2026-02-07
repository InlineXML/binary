namespace InlineXML.Modules.InlineXml;

/// <summary>
/// defines the categories of tokens that our parser can identify.
/// we use a byte as the underlying type to keep the memory footprint
/// of our token stream as small as possible during the scanning phase.
/// </summary>
public enum TokenKind : byte
{
    /// <summary>
    /// the default state for a token that hasn't been categorized yet.
    /// </summary>
    Unknown,
    
    /// <summary>
    /// represents the opening bracket of a tag '<' or '</'.
    /// </summary>
    TAG_OPEN, 
    
    /// <summary>
    /// represents the closing bracket of a tag '>'.
    /// </summary>
    TAG_CLOSE,
    
    /// <summary>
    /// represents the identifier for a tag, such as 'div', 'stack', or 'Component'.
    /// these are used by the AST builder to determine the factory method to call.
    /// </summary>
    TAG_NAME,
    
    /// <summary>
    /// represents an attribute key. our generator ensures these are
    /// auto-Pascal-Cased to match standard C# property conventions,
    /// providing a seamless DX for the end user.
    /// </summary>
    ATTRIBUTE_NAME,
    
    /// <summary>
    /// represents the assignment operator '=' for attributes.
    /// </summary>
    ATTRIBUTE_EQUALS,
    
    /// <summary>
    /// represents a hardcoded string value wrapped in quotes.
    /// </summary>
    ATTRIBUTE_STRING_LITERAL,
    
    /// <summary>
    /// represents complex C# logic embedded within the XML.
    /// this covers everything from simple variables to nested 
    /// mapping functions and arrow expressions.
    /// </summary>
    ATTRIBUTE_EXPRESSION,
    
    /// <summary>
    /// represents an opening parenthesis '('. 
    /// tracked primarily to handle nested mapping function boundaries.
    /// </summary>
    LEFT_PAREN,
    
    /// <summary>
    /// represents a closing parenthesis ')'.
    /// </summary>
    RIGHT_PAREN,
    
    /// <summary>
    /// represents a semicolon ';', used to identify the end of 
    /// structural C# statements within the file.
    /// </summary>
    SEMICOLON,
}