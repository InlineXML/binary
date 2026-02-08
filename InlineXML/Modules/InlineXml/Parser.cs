using System;
using System.Collections.Generic;

namespace InlineXML.Modules.InlineXml;

/// <summary>
/// Tokenizes XML-embedded C# code into a stream of tokens for AST building.
/// Handles nested tags, expressions, and C# context boundaries.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What This Does (ELI5):</strong>
/// Imagine you're reading a foreign language document and you want to break it into meaningful pieces.
/// You might separate it into:
/// - Words (the actual content)
/// - Punctuation (periods, commas, etc.)
/// - Special markers (brackets, braces, etc.)
/// 
/// This parser does the same thing with XML-embedded C# code. It reads through character-by-character
/// and identifies meaningful chunks:
/// <list type="bullet">
/// <item><description>Tags: &lt;div&gt;, &lt;/div&gt;, &lt;img /&gt;</description></item>
/// <item><description>Attribute names and values: name="value"</description></item>
/// <item><description>C# expressions: {someVariable}, {items.Map(x => ...)}</description></item>
/// <item><description>Text content: Plain text between tags</description></item>
/// </list>
/// These chunks are called "tokens". The parser produces a list of tokens that the AstBuilder can then
/// assemble into a tree structure.
/// </para>
/// <para>
/// <strong>The Multi-Layer Challenge:</strong>
/// This isn't simple HTML parsing because XML can be nested inside C# expressions:
/// <code>
/// items.Map(x => &lt;div&gt;{x.Name}&lt;/div&gt;)
/// </code>
/// Here, the parser must:
/// 1. Recognize we're inside a C# expression `{...}`
/// 2. See that inside the expression, there's an XML tag `&lt;div&gt;`
/// 3. Recursively parse that nested XML
/// 4. Resume parsing the outer expression after the nested tag ends
/// </para>
/// <para>
/// <strong>Key Responsibilities:</strong>
/// <list type="bullet">
/// <item><description>Scan through character-by-character looking for XML tags and C# structures</description></item>
/// <item><description>Identify token boundaries (start and end positions)</description></item>
/// <item><description>Handle nested XML inside C# expressions</description></item>
/// <item><description>Track proper brace/parenthesis nesting</description></item>
/// <item><description>Distinguish between XML content and structural C# code (like closing parens or semicolons)</description></item>
/// <item><description>Return a flat token stream for the AstBuilder to process</description></item>
/// </list>
/// </para>
/// </remarks>
public class Parser
{
    /// <summary>
    /// Counter tracking how many tokens have been added during parsing.
    /// Used to return only the relevant portion of the token array.
    /// </summary>
    /// <remarks>
    /// We allocate a fixed-size token array upfront (4096 slots), then track how many we actually use.
    /// At the end, we copy only the used portion to a new array. This avoids wasting memory on unused slots.
    /// </remarks>
    private int _tokenCount = 0;
    
    /// <summary>
    /// Initializes the Parser with factory and method names for code generation context.
    /// </summary>
    /// <param name="factory">The factory class name (e.g., "Document").</param>
    /// <param name="method">The factory method name (e.g., "CreateElement").</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// The parser stores the factory and method names for context, though they're not used during tokenization.
    /// They're provided here for consistency with the full pipeline (Parser → AstBuilder → CodeGenerator).
    /// </para>
    /// </remarks>
    public Parser(string factory, string method) { }

    /// <summary>
    /// Main entry point: tokenizes XML-embedded C# code into a token stream.
    /// </summary>
    /// <param name="src">A read-only span of characters containing the C# source with embedded XML.</param>
    /// <returns>
    /// An array of tokens representing the structure of the XML and its content.
    /// Token positions are absolute within the input span.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is the public entry point that orchestrates tokenization:
    /// <list type="number">
    /// <item><description>Allocates a large token buffer (4096 tokens)</description></item>
    /// <item><description>Skips initial C# code until the first XML tag</description></item>
    /// <item><description>Calls ParseInternal to tokenize from the first tag onward</description></item>
    /// <item><description>Returns only the actual tokens produced (trimmed array)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Why Skip Initial C#?:</strong>
    /// Code often starts with C# setup before the XML begins:
    /// <code>
    /// var items = GetItems();
    /// return &lt;div&gt;{items.Count}&lt;/div&gt;
    /// </code>
    /// We only care about the XML part, so we skip forward to the first `&lt;`.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// string code = @"var x = items.Map(item => &lt;div&gt;{item.Name}&lt;/div&gt;)";
    /// var parser = new Parser("Document", "CreateElement");
    /// var tokens = parser.Parse(ref code.AsSpan());
    /// // Returns tokens for: &lt;div&gt;, {item.Name}, &lt;/div&gt;
    /// </code>
    /// </example>
    public Token[] Parse(ref ReadOnlySpan<char> src)
    {
        var tokens = new Token[4096];
        _tokenCount = 0;
        int pointer = 0;

        // Skip initial C# boilerplate until the first opening tag (<)
        // We only want to tokenize the XML and embedded expressions
        while (pointer < src.Length && src[pointer] != '<') pointer++;

        // If there's at least one tag, parse it and its children
        if (pointer < src.Length) 
        {
            ParseInternal(ref src, ref tokens, ref pointer, 0, isRoot: true);
        }

        // Trim the token array to only include the tokens we actually used
        var result = new Token[_tokenCount];
        Array.Copy(tokens, result, _tokenCount);
        return result;
    }

    /// <summary>
    /// Recursive tokenization engine that processes XML tags, attributes, expressions, and text content.
    /// </summary>
    /// <param name="src">The character span being parsed.</param>
    /// <param name="tokens">The token buffer accumulating tokens.</param>
    /// <param name="pointer">The current position in the character span (passed by reference for mutation).</param>
    /// <param name="startOffset">Offset to add to all token positions (for nested contexts).</param>
    /// <param name="isRoot">If true, this is the top-level parse (from the beginning). If false, we're inside a nested expression.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is the main tokenization loop. It scans character-by-character and handles five cases:
    /// <list type="number">
    /// <item><description><strong>Opening/Closing Tags (&lt;...&gt;):</strong> Parse tag names and attributes</description></item>
    /// <item><description><strong>Tag Closure (&gt; or /&gt;):</strong> Mark the end of a tag</description></item>
    /// <item><description><strong>C# Expressions ({...}):</strong> Recursively parse balanced braces</description></item>
    /// <item><description><strong>Structural Stops ()) or ;):</strong> Stop parsing if we hit C# code boundaries</description></item>
    /// <item><description><strong>Text Content:</strong> Capture plain text between tags</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>The startOffset Parameter:</strong>
    /// When we recursively parse nested XML inside expressions, we need to track the position offset.
    /// If the nested XML starts at character 50 in the input, all tokens from that XML should report
    /// positions relative to that offset. This keeps position tracking consistent across recursion.
    /// </para>
    /// <para>
    /// <strong>The isRoot Parameter:</strong>
    /// When isRoot = true, we're parsing from the document's beginning. We'll scan past structural
    /// C# characters (closing parens, semicolons) because we might see them in the outer C# code.
    /// When isRoot = false, we're inside a nested expression, so `)` or `;` signals we should stop
    /// and return to the parent parser.
    /// </para>
    /// </remarks>
    private void ParseInternal(ref ReadOnlySpan<char> src, ref Token[] tokens, ref int pointer, int startOffset, bool isRoot = false)
    {
        while (pointer < src.Length)
        {
            char current = src[pointer];

            // ========================================
            // CASE 1: TAG HANDLING (Open or Close)
            // ========================================
            if (current == '<')
            {
                int s = pointer;
                bool isClosing = pointer + 1 < src.Length && src[pointer + 1] == '/';
                
                // Record the opening bracket (<) or closing bracket (</)
                AddToken(ref tokens, TokenKind.TAG_OPEN, startOffset + s, startOffset + s + (isClosing ? 2 : 1));
                pointer += (isClosing ? 2 : 1);
                
                // Parse the tag name after the bracket
                ParseTagName(ref src, ref tokens, ref pointer, startOffset);

                // If this is a closing tag, skip to the > and record TAG_CLOSE
                if (isClosing)
                {
                    while (pointer < src.Length && src[pointer] != '>') pointer++;
                    if (pointer < src.Length)
                    {
                        AddToken(ref tokens, TokenKind.TAG_CLOSE, startOffset + pointer, startOffset + pointer + 1);
                        pointer++;
                    }
                    continue;
                }
                
                // ========================================
                // ATTRIBUTE LOOP: For opening tags, parse attributes
                // ========================================
                while (pointer < src.Length && src[pointer] != '>' && src[pointer] != '/')
                {
                    // Skip whitespace between attributes
                    if (char.IsWhiteSpace(src[pointer])) { pointer++; continue; }
                    
                    // If we hit an expression in an attribute, parse it
                    if (src[pointer] == '{') { ParseExpression(ref src, ref tokens, ref pointer, startOffset); continue; }

                    // Parse attribute name (identifier like "name", "className", "onClick")
                    int attrStart = pointer;
                    while (pointer < src.Length && !char.IsWhiteSpace(src[pointer]) && src[pointer] != '=' && src[pointer] != '>' && src[pointer] != '/') pointer++;
                    AddToken(ref tokens, TokenKind.ATTRIBUTE_NAME, startOffset + attrStart, startOffset + pointer);

                    // Skip whitespace after attribute name
                    while (pointer < src.Length && char.IsWhiteSpace(src[pointer])) pointer++;
                    
                    // If there's an equals sign, parse the attribute value
                    if (pointer < src.Length && src[pointer] == '=') 
                    {
                        AddToken(ref tokens, TokenKind.ATTRIBUTE_EQUALS, startOffset + pointer, startOffset + pointer + 1);
                        pointer++;
                        
                        // Skip whitespace after equals
                        while (pointer < src.Length && char.IsWhiteSpace(src[pointer])) pointer++;
                        
                        // Parse attribute value (either quoted string or C# expression)
                        if (pointer < src.Length && src[pointer] == '"') {
                            // String value: "something"
                            int valStart = pointer; 
                            pointer++;
                            while (pointer < src.Length && src[pointer] != '"') pointer++;
                            if (pointer < src.Length) pointer++;
                            AddToken(ref tokens, TokenKind.ATTRIBUTE_NAME, startOffset + valStart, startOffset + pointer);
                        } else if (pointer < src.Length && src[pointer] == '{') {
                            // Expression value: {someVar} or {items.Count}
                            ParseExpression(ref src, ref tokens, ref pointer, startOffset);
                        }
                    }
                }
            }
            // ========================================
            // CASE 2: TAG CLOSURE
            // ========================================
            else if (current == '>')
            {
                // Closing bracket for a normal closing tag
                AddToken(ref tokens, TokenKind.TAG_CLOSE, startOffset + pointer, startOffset + pointer + 1);
                pointer++;
            }
            else if (current == '/')
            {
                // Self-closing tag: />
                if (pointer + 1 < src.Length && src[pointer + 1] == '>')
                {
                   AddToken(ref tokens, TokenKind.TAG_CLOSE, startOffset + pointer, startOffset + pointer + 2);
                   pointer += 2;
                }
                else pointer++;
            }
            // ========================================
            // CASE 3: C# EXPRESSIONS
            // ========================================
            else if (current == '{')
            {
                // Recursively parse the balanced braces
                ParseExpression(ref src, ref tokens, ref pointer, startOffset);
            }
            // ========================================
            // CASE 4: STRUCTURAL STOPS
            // ========================================
            else if (current == ')' || current == ';')
            {
                // If we're in root context, skip these (they're part of the outer C# code)
                // If we're NOT in root context (nested inside an expression), stop and return
                // to let the parent parser handle the closing paren
                if (!isRoot) return;
                pointer++;
            }
            // ========================================
            // CASE 5: TEXT CONTENT
            // ========================================
            else 
            {
                // Capture plain text content between tags
                // This is literal text like "Hello", not XML or expressions
                int s = pointer;
                while (pointer < src.Length && src[pointer] != '<' && src[pointer] != '{')
                {
                    // Stop at structural C# characters (these belong to outer code, not text)
                    if (src[pointer] == ')' || src[pointer] == ';' || src[pointer] == '}') break;
                    pointer++;
                }

                // If we captured actual text (not just whitespace), create a token
                if (pointer > s)
                {
                   var text = src.Slice(s, pointer - s);
                   // Filter pure whitespace (indentation), but capture real words
                   if (!text.IsWhiteSpace())
                   {
                      AddToken(ref tokens, TokenKind.ATTRIBUTE_NAME, startOffset + s, startOffset + pointer);
                   }
                }
                else
                {
                    pointer++;
                }
            }
        }
    }

    /// <summary>
    /// Parses a C# expression enclosed in balanced braces: {expression}.
    /// Handles nested braces and nested XML tags within expressions.
    /// </summary>
    /// <param name="src">The character span being parsed.</param>
    /// <param name="tokens">The token buffer.</param>
    /// <param name="pointer">The current position (at the opening brace).</param>
    /// <param name="startOffset">Offset for token positions.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// C# expressions inside XML attributes or elements are wrapped in braces: `{variable}`, `{items.Count}`, etc.
    /// This method finds the matching closing brace and records the entire expression as a token.
    /// </para>
    /// <para>
    /// <strong>The Nesting Challenge:</strong>
    /// Expressions can contain nested braces and even nested XML:
    /// <code>
    /// {items.Map(x => &lt;div&gt;{x.Name}&lt;/div&gt;)}
    /// </code>
    /// We use a depth counter to track brace nesting:
    /// - `{` increments depth
    /// - `}` decrements depth
    /// - When depth reaches 0, we've found the closing brace
    /// </para>
    /// <para>
    /// <strong>Nested XML Support:</strong>
    /// When we detect `(` at depth 1 followed by `&lt;`, we know there's nested XML coming.
    /// We emit a LEFT_PAREN token and recursively call ParseInternal to handle the nested XML,
    /// then resume parsing the expression after the nested content.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Simple expression
    /// Input:  {someVariable}
    /// Output: TOKEN(ATTRIBUTE_EXPRESSION, position of entire braces and content)
    /// 
    /// // Nested XML in lambda
    /// Input:  {items.Map(x => &lt;div&gt;{x.Name}&lt;/div&gt;)}
    /// Output: TOKEN(ATTRIBUTE_EXPRESSION, "items.Map(x =>")
    ///         TOKEN(LEFT_PAREN, "(")
    ///         [tokens for nested &lt;div&gt;...&lt;/div&gt;]
    ///         (resume after nested XML)
    /// </code>
    /// </example>
    private void ParseExpression(ref ReadOnlySpan<char> src, ref Token[] tokens, ref int pointer, int startOffset)
    {
        int start = pointer;
        int depth = 0;

        while (pointer < src.Length)
        {
            if (src[pointer] == '{') 
            { 
                // Opening brace: increment nesting depth
                depth++; 
                pointer++; 
            }
            else if (src[pointer] == '}') 
            {
                // Closing brace
                depth--; 
                pointer++;
                
                // If depth is now 0, we've found the matching closing brace
                if (depth == 0) 
                {
                    // Record the entire expression including braces
                    AddToken(ref tokens, TokenKind.ATTRIBUTE_EXPRESSION, startOffset + start, startOffset + pointer);
                    return;
                }
            }
            // ========================================
            // NESTED XML SUPPORT
            // ========================================
            // Detect pattern: { ... ( <xml> ) ... }
            // Example: {items.Map(x => <div />)}
            else if (depth == 1 && src[pointer] == '(') 
            {
                // Look ahead: is there an XML tag after this paren?
                int next = pointer + 1;
                while (next < src.Length && char.IsWhiteSpace(src[next])) next++;
                
                if (next < src.Length && src[next] == '<') 
                {
                    // Yes! There's XML coming. Emit the expression so far, then handle the paren and nested XML
                    AddToken(ref tokens, TokenKind.ATTRIBUTE_EXPRESSION, startOffset + start, startOffset + pointer);
                    AddToken(ref tokens, TokenKind.LEFT_PAREN, startOffset + pointer, startOffset + pointer + 1);
                    pointer++;
                    
                    // Recursively parse the nested XML inside the parens
                    ParseInternal(ref src, ref tokens, ref pointer, startOffset, isRoot: false);
                    
                    // Resume parsing the expression after the nested XML
                    start = pointer;
                } 
                else 
                {
                    // No XML, just a regular paren in the expression
                    pointer++;
                }
            }
            else 
            {
                // Regular character, keep advancing
                pointer++;
            }
        }
    }

    /// <summary>
    /// Parses a tag name and records it as a token.
    /// </summary>
    /// <param name="src">The character span being parsed.</param>
    /// <param name="tokens">The token buffer.</param>
    /// <param name="pointer">The current position (at the start of the tag name).</param>
    /// <param name="startOffset">Offset for token positions.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// Tag names consist of letters, digits, hyphens, and underscores. This method scans forward
    /// until it hits a character that can't be part of a tag name (space, >, /, =, etc.), then
    /// records the tag name as a token.
    /// </para>
    /// <para>
    /// <strong>Valid Tag Names:</strong>
    /// - Simple: `div`, `span`, `MyComponent`
    /// - With hyphens: `my-element`, `web-component`
    /// - With underscores: `my_control`
    /// - Mixed: `MyWeb_Component-Item`
    /// </para>
    /// </remarks>
    private void ParseTagName(ref ReadOnlySpan<char> src, ref Token[] tokens, ref int pointer, int startOffset)
    {
        int s = pointer;
        // Advance while we see letters, digits, hyphens, or underscores
        while (pointer < src.Length && (char.IsLetterOrDigit(src[pointer]) || src[pointer] == '-' || src[pointer] == '_')) pointer++;
        AddToken(ref tokens, TokenKind.TAG_NAME, startOffset + s, startOffset + pointer);
    }

    /// <summary>
    /// Adds a token to the token buffer, resizing if necessary.
    /// </summary>
    /// <param name="tokens">The token buffer (passed by reference for potential resizing).</param>
    /// <param name="kind">The token kind/type.</param>
    /// <param name="start">The starting position of the token in the input.</param>
    /// <param name="end">The ending position of the token in the input (exclusive).</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is a helper method that:
    /// <list type="number">
    /// <item><description>Checks if the token buffer is full</description></item>
    /// <item><description>If full, doubles the buffer size</description></item>
    /// <item><description>Adds the new token to the buffer</description></item>
    /// <item><description>Increments the token count</description></item>
    /// </list>
    /// We preallocate with a large buffer (4096) to minimize resizing, but this method handles
    /// the edge case where we need more space.
    /// </para>
    /// </remarks>
    private void AddToken(ref Token[] tokens, TokenKind kind, int start, int end)
    {
        // If we're out of space, double the buffer
        if (_tokenCount >= tokens.Length) Array.Resize(ref tokens, tokens.Length * 2);
        
        // Add the token
        tokens[_tokenCount++] = new Token { Kind = kind, Start = start, End = end };
    }
}