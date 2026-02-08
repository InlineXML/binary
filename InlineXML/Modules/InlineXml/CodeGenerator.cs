using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InlineXML.Modules.InlineXml;

/// <summary>
/// Generates executable C# code from an Abstract Syntax Tree (AST) representing XML elements.
/// Produces a function call to a factory method and tracks source position mappings for error reporting.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What This Does (ELI5):</strong>
/// Imagine you have a family tree (AST) that represents XML structure:
/// <code>
/// &lt;div&gt;
///   &lt;span&gt;Hello&lt;/span&gt;
/// &lt;/div&gt;
/// </code>
/// This service walks that tree and generates C# code that calls your factory methods:
/// <code>
/// Document.CreateElement(
///   "div",
///   new DivProps(),
///   Document.CreateElement(
///     "span",
///     new SpanProps(),
///     "Hello"
///   )
/// )
/// </code>
/// It also tracks WHERE each generated piece came from in the original XML, so compiler errors
/// can be reported at the correct source location.
/// </para>
/// <para>
/// <strong>The Code Generation Pipeline:</strong>
/// <list type="number">
/// <item><description><strong>Input:</strong> AST (tree of ElementNode, ExpressionNode, StringLiteralNode)</description></item>
/// <item><description><strong>Traversal:</strong> Walk the tree recursively, generating code for each node</description></item>
/// <item><description><strong>Output:</strong> C# code as a string + source maps (position mappings)</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Key Responsibilities:</strong>
/// <list type="bullet">
/// <item><description>Traverse the AST and generate C# code for each node type</description></item>
/// <item><description>Format output with proper indentation and commas between arguments</description></item>
/// <item><description>Generate property class instantiation (e.g., new DivProps { ... })</description></item>
/// <item><description>Track source map entries linking generated code back to original XML positions</description></item>
/// <item><description>Handle nested elements and mixed content (elements + text)</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Example Transformation:</strong>
/// <code>
/// Input XML:
/// &lt;button onclick={HandleClick}&gt;Click Me&lt;/button&gt;
/// 
/// Generated C#:
/// Document.CreateElement(
///   "button",
///   new ButtonProps
///   {
///     Onclick = HandleClick
///   },
///   "Click Me"
/// )
/// 
/// Source Maps:
/// - "button" (original position 8-14) → generated code position 45-52
/// - HandleClick (original position 23-34) → generated code position 89-100
/// - "Click Me" (original position 48-56) → generated code position 108-117
/// </code>
/// </para>
/// </remarks>
public class CodeGenerator
{
    /// <summary>
    /// Accumulates the generated C# code as a string.
    /// </summary>
    /// <remarks>
    /// We use StringBuilder for efficiency (many small appends are faster than string concatenation).
    /// </remarks>
    private readonly StringBuilder _output = new();
    
    /// <summary>
    /// Accumulates source map entries that link generated code positions to original XML positions.
    /// </summary>
    /// <remarks>
    /// Each entry maps a range in the generated code back to the corresponding range in the original XML.
    /// The TransformationService will use these to translate compiler errors backward.
    /// </remarks>
    private List<SourceMapEntry> _sourceMap = new();
    
    /// <summary>
    /// Current indentation level (for readable formatted output).
    /// </summary>
    /// <remarks>
    /// Each level represents 3 spaces. This is incremented/decremented as we traverse the AST,
    /// creating properly nested indentation in the generated code.
    /// </remarks>
    private int _indent = 0;
    
    /// <summary>
    /// The factory class name (e.g., "Document" in "Document.CreateElement()").
    /// </summary>
    private readonly string _factory;
    
    /// <summary>
    /// The factory method name (e.g., "CreateElement" in "Document.CreateElement()").
    /// </summary>
    private readonly string _method;

    /// <summary>
    /// Initializes the CodeGenerator with factory and method names.
    /// </summary>
    /// <param name="f">Factory class name (e.g., "Document").</param>
    /// <param name="m">Factory method name (e.g., "CreateElement").</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// The factory and method names define what function calls we'll generate.
    /// Instead of hard-coding "Document.CreateElement", we allow flexibility so different projects
    /// can use different APIs (e.g., "React.createElement", "VirtualDOM.Create", etc.).
    /// </para>
    /// </remarks>
    public CodeGenerator(string f, string m) 
    { 
        _factory = f;
        _method = m;
    }

    /// <summary>
    /// Generates C# code from an AST and produces source maps for position tracking.
    /// </summary>
    /// <param name="nodes">The list of AST nodes (typically Element, Expression, or StringLiteral nodes).</param>
    /// <param name="sourceMap">Output parameter: the list of source map entries produced.</param>
    /// <returns>The generated C# code as a string.</returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is the main entry point. It:
    /// <list type="number">
    /// <item><description>Clears any previous output (for reuse)</description></item>
    /// <item><description>Recursively generates code for all AST nodes</description></item>
    /// <item><description>Returns the generated code and source maps</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Why Source Maps Are Critical:</strong>
    /// When the generated code is compiled, errors will reference line/column positions in the
    /// generated code. But the user wrote the original XML. The source maps let us translate:
    /// "Error at position 120 in generated code" → "Error at position 45 in original XML"
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var nodes = astBuilder.Build(tokens, span);  // AST from parser
    /// var generator = new CodeGenerator("Document", "CreateElement");
    /// string generatedCode = generator.Generate(nodes, out var sourceMap);
    /// // generatedCode contains C# code
    /// // sourceMap contains position mappings
    /// </code>
    /// </example>
    public string Generate(List<AstNode> nodes, out List<SourceMapEntry> sourceMap)
    {
        // Clear output for reuse (in case this generator is reused for multiple files)
        _output.Clear(); 
        _sourceMap.Clear(); 
        _indent = 0;

        // Recursively generate code for all nodes
        GenerateNodeList(nodes);

        // Return the source maps and generated code
        sourceMap = _sourceMap;
        return _output.ToString();
    }

    /// <summary>
    /// Generates code for a list of nodes, separating them with commas for function arguments.
    /// </summary>
    /// <param name="nodes">The list of AST nodes to generate code for.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// Since nodes become function arguments, we separate them with commas.
    /// The last node doesn't get a trailing comma (proper C# syntax).
    /// </para>
    /// <para>
    /// <strong>Example Output:</strong>
    /// <code>
    /// "button",
    /// new ButtonProps { ... },
    /// "Click Me"
    /// </code>
    /// Note the commas between items, but not after the last one.
    /// </para>
    /// </remarks>
    private void GenerateNodeList(List<AstNode> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            GenerateNode(nodes[i]);
            // Add comma and newline between nodes (but not after the last one)
            if (i < nodes.Count - 1) 
            { 
                _output.Append(","); 
                _output.Append("\n"); 
            }
        }
    }

    /// <summary>
    /// Dispatches code generation to the appropriate handler based on node type.
    /// </summary>
    /// <param name="node">The AST node to process.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// AST nodes can be three types:
    /// <list type="bullet">
    /// <item><description><strong>ElementNode:</strong> An XML element (&lt;div&gt;, etc.) → Generate CreateElement call</description></item>
    /// <item><description><strong>ExpressionNode:</strong> A C# expression ({variable}) → Generate code directly</description></item>
    /// <item><description><strong>StringLiteralNode:</strong> Text content ("Hello") → Generate quoted string</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private void GenerateNode(AstNode node)
    {
        if (node is ElementNode el) 
            GenerateElement(el);
        else if (node is ExpressionNode ex) 
            GenerateExpression(ex);
        else if (node is StringLiteralNode s) 
            GenerateStringLiteral(s);
    }

    /// <summary>
    /// Generates code for a C# expression node (e.g., {variable}, {items.Count}).
    /// </summary>
    /// <param name="ex">The ExpressionNode to generate code for.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// Expression nodes are C# code that should be included verbatim. There are two cases:
    /// <list type="number">
    /// <item><description>
    /// <strong>Simple expression:</strong> {variable} or {func()} → Just emit the code as-is
    /// </description></item>
    /// <item><description>
    /// <strong>Lambda with XML:</strong> {items.Map(x => &lt;div /&gt;)} → Extract the lambda head,
    /// emit it with parentheses, recursively generate the nested XML, then close
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Why Lambda Handling?:</strong>
    /// When you write {items.Map(x => &lt;div /&gt;)}, the AST builder recognizes the lambda
    /// and creates an ExpressionNode with children (the nested &lt;div /&gt;).
    /// We need to:
    /// 1. Emit "items.Map(x => ("
    /// 2. Emit the nested &lt;div /&gt; code
    /// 3. Emit the closing ")"
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Simple expression
    /// Input AST: ExpressionNode { Expression = "{myVar}" }
    /// Output: myVar
    /// 
    /// // Lambda with children
    /// Input AST: ExpressionNode { 
    ///   Expression = "{items.Map(x => <div />)}", 
    ///   Children = [ElementNode for div] 
    /// }
    /// Output: items.Map(x => (
    ///           Document.CreateElement(
    ///             "div",
    ///             new DivProps()
    ///           )
    ///         ))
    /// </code>
    /// </example>
    private void GenerateExpression(ExpressionNode ex)
    {
        // Emit indentation
        Append(GetIndent());
        
        // Remove the braces {expression} → expression
        var clean = StripBraces(ex.Expression);
        
        // PRECISE START: Mark where we start writing generated code from this expression
        int start = _output.Length;

        // Check if this expression contains child nodes (nested XML in a lambda)
        if (ex.Children != null && ex.Children.Count > 0)
        {
            // Extract the lambda head (everything before =>)
            // Example: "items.Map(x =>" from "items.Map(x => <div />)"
            int arrow = clean.IndexOf("=>");
            string head = (arrow != -1) ? clean.Substring(0, arrow + 2).Trim() : clean;

            // Emit the lambda head with an opening paren for the nested content
            Append(head + " (\n");
            _indent++; 
            
            // Generate code for the nested children (e.g., nested XML elements)
            GenerateNodeList(ex.Children);
            
            _indent--; 
            Append("\n" + GetIndent() + ")"); 
            
            // Add closing paren if the original expression had one
            if (clean.EndsWith(")")) Append(")"); 
        }
        else 
        {
            // Simple expression with no children: just emit the code as-is
            Append(clean);
        }

        // MAP: Create a source map entry linking generated code back to original XML
        _sourceMap.Add(new SourceMapEntry {
            OriginalStart = ex.SourceStart,
            OriginalEnd = ex.SourceEnd,
            TransformedStart = start,
            TransformedEnd = _output.Length
        });
    }

    /// <summary>
    /// Generates code for a string literal node (plain text content like "Hello").
    /// </summary>
    /// <param name="s">The StringLiteralNode to generate code for.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// String literals are plain text between XML tags. We:
    /// 1. Trim whitespace
    /// 2. Skip empty strings
    /// 3. Emit quoted C# string with escaped quotes
    /// 4. Create a source map
    /// </para>
    /// <para>
    /// <strong>Why Escape Quotes?:</strong>
    /// If the original text contains quotes (e.g., "He said \"Hello\""), we need to escape them
    /// in the generated C# string (e.g., "He said \\\"Hello\\\"").
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// Input XML: &lt;div&gt;Hello World&lt;/div&gt;
    /// Generated: "Hello World"
    /// 
    /// Input XML: &lt;div&gt;He said "Hello"&lt;/div&gt;
    /// Generated: "He said \"Hello\""
    /// </code>
    /// </example>
    private void GenerateStringLiteral(StringLiteralNode s)
    {
        // Get the string value and trim whitespace
        string v = s.Value.Trim();
        
        // Skip empty strings (just whitespace)
        if (!string.IsNullOrEmpty(v)) 
        {
            // Emit indentation
            Append(GetIndent());
            
            // PRECISE START: Mark where we start writing generated code
            int start = _output.Length;
            
            // Emit as a quoted C# string, escaping any internal quotes
            Append($"\"{v.Replace("\"", "\\\"")}\"");
            
            // MAP: Create source map linking generated code back to original XML
            _sourceMap.Add(new SourceMapEntry {
                OriginalStart = s.SourceStart,
                OriginalEnd = s.SourceEnd,
                TransformedStart = start,
                TransformedEnd = _output.Length
            });
        }
    }

    /// <summary>
    /// Generates code for an element node (XML elements like &lt;div&gt;, &lt;button&gt;, etc.).
    /// </summary>
    /// <param name="element">The ElementNode to generate code for.</param>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// This is the complex case. For each XML element, we generate a factory call:
    /// <code>
    /// Document.CreateElement(
    ///   "tagName",        // Arg 1: tag name (always a string)
    ///   new TagNameProps { ... },  // Arg 2: properties object
    ///   ...children...    // Args 3+: child elements/text
    /// )
    /// </code>
    /// </para>
    /// <para>
    /// <strong>Steps:</strong>
    /// <list type="number">
    /// <item><description>Emit opening call: Document.CreateElement(</description></item>
    /// <item><description>Emit tag name string and map it</description></item>
    /// <item><description>Generate props object (new DivProps { onclick = ... })</description></item>
    /// <item><description>Generate children (nested elements and text)</description></item>
    /// <item><description>Emit closing paren</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Attribute to Property Name Conversion:</strong>
    /// XML uses kebab-case (onclick, class-name), but C# uses PascalCase (Onclick, ClassName).
    /// We convert each attribute name to PascalCase when generating the props object.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// Input XML:
    /// &lt;button onclick={HandleClick} class="btn"&gt;Click Me&lt;/button&gt;
    /// 
    /// Generated C#:
    /// Document.CreateElement(
    ///   "button",
    ///   new ButtonProps
    ///   {
    ///     Onclick = HandleClick,
    ///     Class = "btn"
    ///   },
    ///   "Click Me"
    /// )
    /// </code>
    /// </example>
    private void GenerateElement(ElementNode element)
    {
        // Emit the opening factory call with proper indentation
        Append($"{GetIndent()}{_factory}.{_method}(\n");
        _indent++;

        // ========================================
        // ARG 1: Tag Name (always a string)
        // ========================================
        Append(GetIndent());
        int tagStart = _output.Length;
        Append($"\"{element.TagName}\"");
        
        // MAP: Link the generated tag name back to the original XML
        _sourceMap.Add(new SourceMapEntry {
            OriginalStart = element.SourceStart + 1, // Skip the '<'
            OriginalEnd = element.SourceStart + 1 + element.TagName.Length,
            TransformedStart = tagStart,
            TransformedEnd = _output.Length
        });
        _output.Append(",\n");

        // ========================================
        // ARG 2: Properties Object
        // ========================================
        // Emit: new DivProps { ... } or new DivProps() if no attributes
        Append($"{GetIndent()}new {ToPascalCase(element.TagName)}Props");
        
        if (element.Attributes.Count > 0)
        {
            // Attributes exist: emit as property initializer
            _output.Append("\n" + GetIndent() + "{\n"); 
            _indent++;

            for (int i = 0; i < element.Attributes.Count; i++)
            {
                var (name, valNode) = element.Attributes[i];
                
                // Emit property name = value
                Append(GetIndent() + ToPascalCase(name) + " = ");

                // PRECISE START: Mark where the attribute value begins in output
                int valStart = _output.Length;

                // Generate the attribute value (handle different node types)
                string valueString = valNode switch {
                    StringLiteralNode s => $"\"{s.Value.Trim('\"', '\'')}\"",
                    ExpressionNode ex => StripBraces(ex.Expression),
                    _ => "null"
                };

                Append(valueString);

                // MAP: Link generated attribute value back to original XML attribute
                _sourceMap.Add(new SourceMapEntry {
                    OriginalStart = valNode.SourceStart,
                    OriginalEnd = valNode.SourceEnd,
                    TransformedStart = valStart,
                    TransformedEnd = _output.Length
                });

                // Add comma between attributes (but not after the last one)
                if (i < element.Attributes.Count - 1) _output.Append(",");
                _output.Append("\n");
            }

            _indent--; 
            Append($"{GetIndent()}}}");
        }
        else 
        {
            // No attributes: emit empty props constructor
            _output.Append("()");
        }

        // ========================================
        // ARGS 3+: Children (nested elements and text)
        // ========================================
        if (element.Children.Count > 0) 
        { 
            _output.Append(",\n"); 
            GenerateNodeList(element.Children); 
        }

        // ========================================
        // CLOSING PAREN
        // ========================================
        _indent--; 
        _output.Append("\n" + GetIndent() + ")");
    }

    /// <summary>
    /// Removes surrounding braces from an expression string.
    /// </summary>
    /// <param name="e">The expression string (possibly with {}).</param>
    /// <returns>The expression with surrounding braces removed.</returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// In XML, expressions are wrapped in braces: {variable}. This method strips the braces:
    /// "{variable}" → "variable"
    /// </para>
    /// <para>
    /// <strong>Why?:</strong>
    /// We want to emit clean C# code without the XML brace delimiters.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// StripBraces("{myVar}") → "myVar"
    /// StripBraces("{items.Count}") → "items.Count"
    /// StripBraces("plainText") → "plainText" (no change)
    /// </code>
    /// </example>
    private string StripBraces(string e) 
    {
        var t = e.Trim();
        if (t.StartsWith("{") && t.EndsWith("}")) 
            t = t.Substring(1, t.Length - 2).Trim();
        return t;
    }

    /// <summary>
    /// Appends a string to the output buffer.
    /// </summary>
    /// <param name="t">The string to append.</param>
    /// <remarks>
    /// This is a convenience method for cleaner code generation calls.
    /// </remarks>
    private void Append(string t) => _output.Append(t);
    
    /// <summary>
    /// Generates an indentation string based on the current indent level.
    /// </summary>
    /// <returns>A string of spaces (3 spaces per indent level).</returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// Indent level 0 → "" (no spaces)
    /// Indent level 1 → "   " (3 spaces)
    /// Indent level 2 → "      " (6 spaces)
    /// etc.
    /// This creates readable, properly nested code.
    /// </para>
    /// </remarks>
    private string GetIndent() => new string(' ', _indent * 3);
    
    /// <summary>
    /// Converts a string to PascalCase (first letter uppercase).
    /// </summary>
    /// <param name="s">The string to convert.</param>
    /// <returns>The string with the first letter uppercase, or the original string if empty.</returns>
    /// <remarks>
    /// <para>
    /// <strong>What This Does (ELI5):</strong>
    /// "onclick" → "Onclick"
    /// "class" → "Class"
    /// "div" → "Div"
    /// This is used to convert XML attribute names to C# property names.
    /// </para>
    /// </remarks>
    private string ToPascalCase(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
}