using System;
using System.Collections.Generic;
using System.Linq;

namespace InlineXML.Modules.InlineXml;

/// <summary>
/// Responsible for the structural analysis phase of the transformation pipeline.
/// Converts a flat array of <see cref="Token"/> objects into a hierarchical Abstract Syntax Tree (AST).
/// </summary>
/// <remarks>
/// This builder implements a recursive descent parser. It handles complex scenarios such as 
/// nested XML elements and "Hybrid Nodes," where C# expressions (e.g., lambdas) contain 
/// further nested XML fragments.
/// </remarks>
public class AstBuilder
{
    private Token[] _tokens;

    /// <summary>
    /// entry point for the building process.
    /// </summary>
    /// <param name="tokens">The lexed tokens from the <see cref="Parser"/>.</param>
    /// <param name="source">The original source text used for content extraction.</param>
    /// <returns>A collection of high-level <see cref="AstNode"/> objects.</returns>
    public List<AstNode> Build(Token[] tokens, ReadOnlySpan<char> source)
    {
        // ELI5: Think of 'tokens' as a box of LEGO pieces. 
        // We are going to go through them one by one to build a castle (the AST).
        _tokens = tokens;
        int i = 0;
        return ParseSiblings(ref i, null, source);
    }

    /// <summary>
    /// Parses a sequence of nodes at the same hierarchical level until a closing tag is encountered.
    /// </summary>
    private List<AstNode> ParseSiblings(ref int i, string? stopAtTag, ReadOnlySpan<char> source)
    {
        var nodes = new List<AstNode>();
        while (i < _tokens.Length)
        {
            // ELI5: If we are looking for a specific "Stop" sign (like </div >), 
            // and we see it, we stop building this branch and go back up.
            if (stopAtTag != null && IsClosingTag(i, stopAtTag, source)) break;
            
            var node = ParseNode(ref i, source);
            if (node != null) nodes.Add(node);
            else i++;
        }
        return nodes;
    }

    /// <summary>
    /// Identifies the type of the current token and routes it to the appropriate parsing logic.
    /// </summary>
    private AstNode? ParseNode(ref int i, ReadOnlySpan<char> source)
    {
        if (i >= _tokens.Length) return null;
        var token = _tokens[i];

        // ELI5: If the token is a '<', we know we are starting a new XML Element.
        if (token.Kind == TokenKind.TAG_OPEN)
        {
            if (GetTokenText(token, source) == "</") return null;
            return ParseElement(ref i, source);
        }

        // ELI5: This is a "Hybrid Node." It's C# code that might have XML hidden inside it.
        // Example: { users.Select(u => <p>{u.Name}</p>) }
        if (token.Kind == TokenKind.ATTRIBUTE_EXPRESSION)
        {
            var text = GetTokenText(token, source);
            var node = new ExpressionNode 
            { 
                Expression = text, 
                SourceStart = token.Start, 
                SourceEnd = token.End 
            };
            i++;

            // RECURSION CHECK: If this C# expression contains XML symbols, we dig deeper.
            if (text.Contains("<") && text.Contains(">"))
            {
                // ELI5: We strip the outer { } and treat the inside like a mini-document.
                string inner = text.Trim().Substring(1, text.Trim().Length - 2);
                var innerSpan = inner.AsSpan();
                
                var subParser = new Parser("Document", "CreateElement");
                var subTokens = subParser.Parse(ref innerSpan);
                
                if (subTokens.Length > 0)
                {
                    // ELI5: We start a "Sub-Builder" to handle the XML found inside the C#.
                    var subBuilder = new AstBuilder();
                    node.Children = subBuilder.Build(subTokens, innerSpan);
                    
                    // ELI5: We update the expression to keep only the "Header" (e.g., "u => ").
                    // This way, the generator knows how to wrap the nested tags.
                    int xmlStart = inner.IndexOf('<');
                    node.Expression = "{" + inner.Substring(0, xmlStart).Trim();
                }
            }
            return node;
        }

        // ELI5: Simple text or attribute names are treated as plain literals.
        if (token.Kind == TokenKind.ATTRIBUTE_NAME)
        {
            var raw = GetTokenText(token, source);
            i++;
            return new StringLiteralNode { Value = raw, SourceStart = token.Start, SourceEnd = token.End };
        }

        return null;
    }

    /// <summary>
    /// Consumes tokens to form a complete <see cref="ElementNode"/>, including its attributes and children.
    /// </summary>
    private ElementNode ParseElement(ref int i, ReadOnlySpan<char> source)
    {
        var startToken = _tokens[i];
        i++; // skip '<'
        
        var nameToken = _tokens[i];
        var name = GetTokenText(nameToken, source);
        var node = new ElementNode { TagName = name, SourceStart = startToken.Start };
        i++; // skip 'name'

        // ELI5: Keep looking for attributes (like class="btn") until we see the '>' or '/>'.
        while (i < _tokens.Length && _tokens[i].Kind != TokenKind.TAG_CLOSE)
        {
            if (_tokens[i].Kind == TokenKind.ATTRIBUTE_NAME)
            {
                var attr = GetTokenText(_tokens[i], source);
                i++;
                if (i < _tokens.Length && _tokens[i].Kind == TokenKind.ATTRIBUTE_EQUALS)
                {
                    i++;
                    var val = ParseNode(ref i, source); // Get the attribute's value
                    if (val != null) node.Attributes.Add((attr, val));
                }
            } 
            else i++;
        }

        if (i < _tokens.Length && _tokens[i].Kind == TokenKind.TAG_CLOSE)
        {
            var closeText = GetTokenText(_tokens[i], source);
            node.SourceEnd = _tokens[i].End;
            i++;

            // ELI5: If the tag ended with '>' (not '/>'), it means there is 
            // "stuff" (children) inside the tags. We go back to ParseSiblings.
            if (closeText == ">")
            {
                node.Children = ParseSiblings(ref i, name, source);
                
                // ELI5: After finding children, we expect to see a closing tag </tagName>.
                if (i < _tokens.Length && IsClosingTag(i, name, source))
                {
                    i++; // skip '</'
                    i++; // skip 'name'
                    if (i < _tokens.Length && _tokens[i].Kind == TokenKind.TAG_CLOSE)
                    {
                        node.SourceEnd = _tokens[i].End;
                        i++; // skip '>'
                    }
                }
            }
        }
        return node;
    }

    /// <summary>
    /// Performs a look-ahead to check if the current token sequence represents 
    /// a closing tag for a specific element name.
    /// </summary>
    private bool IsClosingTag(int index, string tagName, ReadOnlySpan<char> source)
    {
        if (index + 1 >= _tokens.Length) return false;
        return _tokens[index].Kind == TokenKind.TAG_OPEN && 
               GetTokenText(_tokens[index], source) == "</" && 
               GetTokenText(_tokens[index+1], source) == tagName;
    }

    /// <summary>
    /// Safely extracts text from the source span based on token boundaries.
    /// </summary>
    private string GetTokenText(Token token, ReadOnlySpan<char> source)
    {
        var len = token.End - token.Start;
        return (len > 0 && token.Start + len <= source.Length) 
            ? source.Slice(token.Start, len).ToString() 
            : "";
    }
}