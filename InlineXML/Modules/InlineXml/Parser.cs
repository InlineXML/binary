using System;
using System.Collections.Generic;

namespace InlineXML.Modules.InlineXml;

/// <summary>
/// A high-performance, forward-only Lexer that tokenizes XML-like syntax embedded within C# source.
/// </summary>
/// <remarks>
/// This parser is designed for speed, utilizing <see cref="ReadOnlySpan{T}"/> to minimize heap allocations.
/// It specifically handles the transition points between raw XML tags, C# expressions <c>{ ... }</c>, 
/// and attribute declarations.
/// </remarks>
public class Parser
{
    private int _tokenCount = 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="Parser"/> class.
    /// </summary>
    /// <param name="factory">The factory name for context (reserved for future AST metadata).</param>
    /// <param name="method">The method name for context (reserved for future AST metadata).</param>
    public Parser(string factory, string method) { }

    /// <summary>
    /// Scans the provided source span and converts it into a flat array of <see cref="Token"/> structures.
    /// </summary>
    /// <param name="src">The source text to tokenize.</param>
    /// <returns>An array of identified <see cref="Token"/> objects.</returns>
    public Token[] Parse(ref ReadOnlySpan<char> src)
    {
        // ELI5: We create a large "tray" to hold 16,384 tokens.
        // If we need more, we'll expand it later.
        var tokens = new Token[16384];
        _tokenCount = 0;
        int pointer = 0;

        // ELI5: Walk through the text until we find the very first '<'. 
        // We ignore everything before the root XML tag.
        while (pointer < src.Length && src[pointer] != '<') pointer++;

        if (pointer < src.Length) 
        {
            // ELI5: Start the "Internal Engine" to grab the root tag and all its children.
            ParseInternal(ref src, ref tokens, ref pointer, null);
        }

        // ELI5: Shrink the array to fit exactly the number of tokens we actually found.
        var result = new Token[_tokenCount];
        Array.Copy(tokens, result, _tokenCount);
        return result;
    }

    /// <summary>
    /// The primary recursive descent loop that identifies tags, attributes, and text nodes.
    /// </summary>
    private void ParseInternal(ref ReadOnlySpan<char> src, ref Token[] tokens, ref int pointer, string? stopAtTag)
    {
        while (pointer < src.Length)
        {
            // ELI5: Check if we just hit a closing tag like </div>.
            // If we did, we finish this level and "pop" back up to the parent.
            if (stopAtTag != null && IsAtClosingTag(ref src, pointer, stopAtTag))
            {
                AddToken(ref tokens, TokenKind.TAG_OPEN, pointer, pointer + 2); // "</"
                pointer += 2;
                ParseTagName(ref src, ref tokens, ref pointer);
                while (pointer < src.Length && src[pointer] != '>') pointer++;
                if (pointer < src.Length)
                {
                    AddToken(ref tokens, TokenKind.TAG_CLOSE, pointer, pointer + 1); // ">"
                    pointer++;
                }
                return;
            }

            // --- 1. HANDLE TAGS ---
            if (src[pointer] == '<')
            {
                // ELI5: If we see "</" but weren't expecting it here, stop and let the parent handle it.
                if (pointer + 1 < src.Length && src[pointer + 1] == '/') return;

                int start = pointer;
                AddToken(ref tokens, TokenKind.TAG_OPEN, start, start + 1); // "<"
                pointer++;

                int nameStart = pointer;
                ParseTagName(ref src, ref tokens, ref pointer);
                string tagName = src.Slice(nameStart, pointer - nameStart).ToString();

                // --- 2. HANDLE ATTRIBUTES ---
                // ELI5: While we haven't hit the end of the tag ('>' or '/>'), look for attributes.
                while (pointer < src.Length && src[pointer] != '>' && src[pointer] != '/')
                {
                    if (char.IsWhiteSpace(src[pointer])) { pointer++; continue; }
                    
                    int attrStart = pointer;
                    while (pointer < src.Length && !char.IsWhiteSpace(src[pointer]) && src[pointer] != '=' && src[pointer] != '>' && src[pointer] != '/') pointer++;
                    AddToken(ref tokens, TokenKind.ATTRIBUTE_NAME, attrStart, pointer);

                    if (pointer < src.Length && src[pointer] == '=')
                    {
                        AddToken(ref tokens, TokenKind.ATTRIBUTE_EQUALS, pointer, pointer + 1);
                        pointer++;
                        while (pointer < src.Length && char.IsWhiteSpace(src[pointer])) pointer++;
                        
                        // ELI5: Attribute values can be "Strings" or {C# Expressions}.
                        if (pointer < src.Length && src[pointer] == '"') {
                            int vStart = pointer; pointer++;
                            while (pointer < src.Length && src[pointer] != '"') pointer++;
                            if (pointer < src.Length) pointer++;
                            AddToken(ref tokens, TokenKind.ATTRIBUTE_NAME, vStart, pointer);
                        } else if (pointer < src.Length && src[pointer] == '{') {
                            ParseExpression(ref src, ref tokens, ref pointer);
                        }
                    }
                }

                // --- 3. CLOSING THE TAG ---
                if (pointer < src.Length && src[pointer] == '/') // Self-closing tag "/>"
                {
                    AddToken(ref tokens, TokenKind.TAG_CLOSE, pointer, pointer + 2);
                    pointer += 2;
                    // If this was the root, we are done.
                    if (stopAtTag == null) return;
                }
                else if (pointer < src.Length && src[pointer] == '>') // Open tag ">"
                {
                    AddToken(ref tokens, TokenKind.TAG_CLOSE, pointer, pointer + 1);
                    pointer++;
                    // ELI5: Dive into the children of this tag.
                    ParseInternal(ref src, ref tokens, ref pointer, tagName);
                    if (stopAtTag == null) return;
                }
            }
            // --- 4. HANDLE EXPRESSIONS ---
            else if (src[pointer] == '{')
            {
                ParseExpression(ref src, ref tokens, ref pointer);
            }
            // --- 5. HANDLE WHITESPACE/TEXT ---
            else if (char.IsWhiteSpace(src[pointer]))
            {
                pointer++;
            }
            else 
            {
                // ELI5: Anything else is treated as a "Text Node" (plain text inside XML).
                int textStart = pointer;
                while (pointer < src.Length && src[pointer] != '<' && src[pointer] != '{') pointer++;
                AddToken(ref tokens, TokenKind.ATTRIBUTE_NAME, textStart, pointer);
            }
        }
    }

    /// <summary>
    /// Parses a C# expression by tracking curly-brace depth and ignoring characters inside string literals.
    /// </summary>
    private void ParseExpression(ref ReadOnlySpan<char> src, ref Token[] tokens, ref int pointer)
    {
        int start = pointer;
        int depth = 0;
        bool inString = false;

        // ELI5: This is like counting matching parentheses in math. 
        // We don't stop until we find the '}' that matches our starting '{'.
        while (pointer < src.Length)
        {
            char c = src[pointer];

            // Ignore braces if they are inside a "quote" (string literal).
            if (c == '"' && (pointer == 0 || src[pointer - 1] != '\\')) inString = !inString;

            if (!inString)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        pointer++;
                        AddToken(ref tokens, TokenKind.ATTRIBUTE_EXPRESSION, start, pointer);
                        return;
                    }
                }
            }
            pointer++;
        }
    }

    /// <summary>
    /// Performs a zero-allocation check to see if the current pointer is at a specific closing tag.
    /// </summary>
    private bool IsAtClosingTag(ref ReadOnlySpan<char> src, int pointer, string tag)
    {
        if (pointer + 1 >= src.Length) return false;
        if (src[pointer] != '<' || src[pointer + 1] != '/') return false;
        var slice = src.Slice(pointer + 2);
        if (slice.Length < tag.Length) return false;
        return slice.StartsWith(tag.AsSpan()) && (slice.Length == tag.Length || !char.IsLetterOrDigit(slice[tag.Length]));
    }

    /// <summary>
    /// Identifies valid XML tag name characters (alphanumeric plus '-', '_', and '.').
    /// </summary>
    private void ParseTagName(ref ReadOnlySpan<char> src, ref Token[] tokens, ref int pointer)
    {
        int s = pointer;
        while (pointer < src.Length && (char.IsLetterOrDigit(src[pointer]) || src[pointer] == '-' || src[pointer] == '_' || src[pointer] == '.')) pointer++;
        AddToken(ref tokens, TokenKind.TAG_NAME, s, pointer);
    }

    /// <summary>
    /// Adds a new token to the collection, resizing the underlying array if necessary.
    /// </summary>
    private void AddToken(ref Token[] tokens, TokenKind kind, int start, int end)
    {
        if (start >= end) return;
        if (_tokenCount >= tokens.Length) Array.Resize(ref tokens, tokens.Length * 2);
        tokens[_tokenCount++] = new Token { Kind = kind, Start = start, End = end };
    }
}