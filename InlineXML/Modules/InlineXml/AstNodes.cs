using System.Collections.Generic;

namespace InlineXML.Modules.InlineXml;

/// <summary>
/// the base class for all nodes in our abstract syntax tree.
/// every node tracks its original source coordinates, which is
/// the foundation for our source mapping and diagnostic logic.
/// </summary>
public abstract class AstNode
{
    /// <summary>
    /// the character offset in the original XCS file where this node starts.
    /// </summary>
    public int SourceStart { get; set; }

    /// <summary>
    /// the character offset in the original XCS file where this node ends.
    /// </summary>
    public int SourceEnd { get; set; }
}

/// <summary>
/// represents a declarative element (tag) in the XCS structure.
/// it acts as a container for attributes and nested child nodes.
/// </summary>
public class ElementNode : AstNode
{
    /// <summary>
    /// the name of the XML tag (e.g., "div", "stack", "button").
    /// </summary>
    public string TagName { get; set; }

    /// <summary>
    /// a collection of key-value pairs representing the element's properties.
    /// the value can be either a string literal or a complex expression.
    /// </summary>
    public List<(string name, AstNode value)> Attributes { get; set; } = new();

    /// <summary>
    /// any nested nodes found between the opening and closing tags.
    /// </summary>
    public List<AstNode> Children { get; set; } = new();
}

/// <summary>
/// represents a simple string literal, typically used for attribute
/// values that don't require C# evaluation.
/// </summary>
public class StringLiteralNode : AstNode
{
    /// <summary>
    /// the raw string value of the node.
    /// </summary>
    public string Value { get; set; }
}

/// <summary>
/// represents an inline C# expression. this is a unique node because
/// it can "bridge" back into XML—allowing for things like mapping
/// functions to contain nested ElementNodes.
/// </summary>
public class ExpressionNode : AstNode
{
    /// <summary>
    /// the raw C# code contained within the expression (e.g., "items.Select(x => ...)").
    /// </summary>
    public string Expression { get; set; }

    /// <summary>
    /// if the expression contains nested XCS (like an arrow function 
    /// returning a <div>), those elements are stored here.
    /// </summary>
    public List<AstNode> Children { get; set; } = new();
}

/// <summary>
/// represents raw text content found between XML tags.
/// </summary>
public class TextNode : AstNode
{
    /// <summary>
    /// the literal text content.
    /// </summary>
    public string Content { get; set; }
}