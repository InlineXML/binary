using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InlineXML.Modules.InlineXml;

/// <summary>
/// Orchestrates the transformation of Abstract Syntax Tree (AST) nodes into executable C# code.
/// </summary>
/// <remarks>
/// This generator implements a recursive descent pattern to convert XML-like structures 
/// into fluent API calls. It simultaneously generates a <see cref="SourceMapEntry"/> 
/// collection to maintain traceability between the source XML and generated C#.
/// </remarks>
public class CodeGenerator
{
    private readonly StringBuilder _output = new();
    private List<SourceMapEntry> _sourceMap = new();
    private int _indent = 0;
    private readonly string _factory;
    private readonly string _method;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeGenerator"/> class.
    /// </summary>
    /// <param name="factory">The name of the static factory or service (e.g., "UI").</param>
    /// <param name="method">The method name used for element creation (e.g., "Create").</param>
    public CodeGenerator(string factory, string method) 
    { 
        _factory = factory; 
        _method = method; 
    }

    /// <summary>
    /// entry point for code generation. Transforms a list of AST nodes into a single C# string.
    /// </summary>
    /// <param name="nodes">The list of parsed <see cref="AstNode"/> elements.</param>
    /// <param name="sourceMap">Outputs the mapping data used for debugging and diagnostics.</param>
    /// <returns>A formatted string of generated C# code.</returns>
    public string Generate(List<AstNode> nodes, out List<SourceMapEntry> sourceMap)
    {
        // ELI5: Clear the "paper" (StringBuilder) and reset our "ruler" (indent) 
        // before we start writing the new code.
        _output.Clear(); 
        _sourceMap.Clear(); 
        _indent = 0;
        
        GenerateNodeList(nodes, false);
        
        sourceMap = _sourceMap;
        return _output.ToString();
    }

    /// <summary>
    /// Iterates through a collection of nodes, handling indentation and comma separation.
    /// </summary>
    private void GenerateNodeList(List<AstNode> nodes, bool nested)
    {
        // ELI5: Filter out "ghost" nodes (empty spaces or newlines in the XML 
        // that don't need to be C# code).
        var validNodes = nodes.Where(n => !(n is StringLiteralNode s && string.IsNullOrWhiteSpace(s.Value))).ToList();
        
        for (int i = 0; i < validNodes.Count; i++)
        {
            if (nested) _output.Append(GetIndent());
            GenerateNode(validNodes[i]);
            
            // ELI5: If there's another node coming after this one, add a comma 
            // so the C# compiler knows they are separate items in a list.
            if (i < validNodes.Count - 1)
            {
                _output.Append(",\n");
            }
        }
    }

    /// <summary>
    /// Routes the node to its specific generation logic based on its concrete type.
    /// </summary>
    private void GenerateNode(AstNode node)
    {
        if (node is ElementNode el) GenerateElement(el);
        else if (node is ExpressionNode ex) GenerateExpression(ex);
        else if (node is StringLiteralNode s) GenerateStringLiteral(s);
    }

    /// <summary>
    /// Processes C# expressions embedded in XML (e.g., {props.Title}).
    /// </summary>
    private void GenerateExpression(ExpressionNode ex)
    {
        int start = _output.Length;
        string body = ex.Expression.Trim();
        
        // ELI5: Remove the curly braces { } from the XML expression because 
        // they aren't valid in raw C# code.
        if (body.StartsWith("{")) body = body.Substring(1);
        if (body.EndsWith("}")) body = body.Substring(0, body.Length - 1);
        body = body.Trim();

        // ELI5: If this expression has children (like a logical loop or "if" statement 
        // wrapping a tag), we need to handle the "Header" (the IF) and the "Footer" (the closing bracket).
        if (ex.Children != null && ex.Children.Count > 0)
        {
            // Example: "user.IsLoggedIn && ("
            string header = ExtractLogicHeader(body);
            
            _output.Append(header); 
            _indent++;
            
            GenerateNodeList(ex.Children, true);
            
            _indent--;
            _output.Append("\n" + GetIndent());
            
            // ELI5: If we opened 2 parentheses in the header, we must close 2 here.
            _output.Append(MatchClosingParentheses(header));
        }
        else 
        {
            _output.Append(body);
        }

        // ELI5: Tell the Source Map exactly where this expression started and 
        // ended in the C# so we can find it later if there's an error.
        _sourceMap.Add(new SourceMapEntry {
            OriginalStart = ex.SourceStart, TransformedStart = start,
            OriginalEnd = ex.SourceEnd, TransformedEnd = _output.Length
        });
    }

    /// <summary>
    /// Extracts the logical C# prefix before an inline XML tag appears in an expression.
    /// </summary>
    private string ExtractLogicHeader(string body)
    {
        int xmlIndex = body.IndexOf('<');
        if (xmlIndex == -1) return body;
        return body.Substring(0, xmlIndex).TrimEnd();
    }

    /// <summary>
    /// Analyzes the header to ensure all opened parentheses are properly balanced in the output.
    /// </summary>
    private string MatchClosingParentheses(string header)
    {
        int openCount = 0;
        foreach (char c in header)
        {
            if (c == '(') openCount++;
            else if (c == ')') openCount--;
        }
        
        return new string(')', Math.Max(0, openCount));
    }

    /// <summary>
    /// Generates a factory method call representing an XML element (e.g., UI.Create("div", ...)).
    /// </summary>
    private void GenerateElement(ElementNode element)
    {
        int start = _output.Length;

        // ELI5: Start writing the function call, e.g., "UI.Create("
        _output.Append($"{_factory}.{_method}(\n");
        _indent++;

        // Tag Name Logic
        _output.Append(GetIndent());
        // ELI5: If the tag starts with a Big Letter (like <MyComponent>), it's a class.
        // If it's small (like <div>), it's just a string name.
        bool isComponent = char.IsUpper(element.TagName[0]);
        _output.Append(isComponent ? element.TagName : $"\"{element.TagName}\"");
        _output.Append(",\n");

       // Props / Attributes Logic
        string propsPrefix = isComponent ? "" : "Html";
        _output.Append($"{GetIndent()}new {propsPrefix}{ToPascalCase(element.TagName)}Props");
        
        if (element.Attributes.Count > 0)
        {
            _output.Append("\n" + GetIndent() + "{\n");
            _indent++;
            for (int i = 0; i < element.Attributes.Count; i++)
            {
                var attr = element.Attributes[i];
                _output.Append($"{GetIndent()}{ToPascalCase(attr.name)} = ");
                
                // ELI5: If the attribute is a string "hello", keep it as "hello".
                // If it's an expression {val}, treat it as code.
                if (attr.value is StringLiteralNode s) _output.Append($"\"{s.Value.Trim('\"', '\'')}\"");
                else if (attr.value is ExpressionNode ex)
                {
                    string inner = ex.Expression.Trim();
                    if (inner.StartsWith("{")) inner = inner.Substring(1);
                    if (inner.EndsWith("}")) inner = inner.Substring(0, inner.Length - 1);
                    _output.Append(inner.Trim());
                }

                if (i < element.Attributes.Count - 1) _output.Append(",");
                _output.Append("\n");
            }
            _indent--;
            _output.Append(GetIndent() + "}");
        }
        else _output.Append("()");

        // Children Logic
        if (element.Children.Count > 0) 
        { 
            _output.Append(",\n"); 
            GenerateNodeList(element.Children, true); 
        }

        _indent--;
        _output.Append("\n" + GetIndent() + ")");

        // Record the mapping for this entire element block.
        _sourceMap.Add(new SourceMapEntry {
            OriginalStart = element.SourceStart, TransformedStart = start,
            OriginalEnd = element.SourceEnd, TransformedEnd = _output.Length
        });
    }

    /// <summary>
    /// Escapes and writes a raw string literal to the output.
    /// </summary>
    private void GenerateStringLiteral(StringLiteralNode s)
    {
        int start = _output.Length;
        // ELI5: If the text has quotes inside it, add backslashes so C# doesn't get confused.
        _output.Append($"\"{s.Value.Replace("\"", "\\\"")}\"");
        _sourceMap.Add(new SourceMapEntry {
            OriginalStart = s.SourceStart, TransformedStart = start,
            OriginalEnd = s.SourceEnd, TransformedEnd = _output.Length
        });
    }

    /// <summary> Generates a whitespace string based on the current indentation level. </summary>
    private string GetIndent() => new string(' ', _indent * 4);

    /// <summary> Converts a string to PascalCase (e.g., "onclick" to "OnClick"). </summary>
    private string ToPascalCase(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
}