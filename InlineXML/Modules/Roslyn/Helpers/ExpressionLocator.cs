using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace InlineXML.Modules.Roslyn;

/// <summary>
/// Utility for locating and extracting XCS component expression ranges from syntax trees.
/// </summary>
/// <remarks>
/// <para>
/// XCS (InlineXML Component Syntax) allows declarative markup-like syntax within C# code.
/// This utility identifies component expressions marked by the <c>(<</c> pattern (with optional
/// whitespace between the parenthesis and angle bracket) and determines their complete boundaries
/// by walking the Roslyn syntax tree.
/// </para>
/// </remarks>
public static class ExpressionLocator
{
    /// <summary>
    /// Finds all XCS component expression ranges in the given syntax tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks the Roslyn syntax tree to find parenthesized expressions that contain
    /// angle bracket tokens, indicating XCS component expressions.
    /// </para>
    /// </remarks>
    /// <param name="syntaxTree">The syntax tree to search for XCS expressions.</param>
    /// <returns>A collection of tuples containing the start and end positions of each expression found.</returns>
    public static IEnumerable<(int Start, int End)> FindExpressions(SyntaxTree syntaxTree)
    {
        var root = syntaxTree.GetRoot();
        return FindExpressions(root);
    }

    /// <summary>
    /// Finds all XCS component expression ranges in the given syntax node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks the syntax node to find parenthesized expressions that contain
    /// angle bracket tokens, indicating XCS component expressions.
    /// </para>
    /// </remarks>
    /// <param name="node">The syntax node to search for XCS expressions.</param>
    /// <returns>A collection of tuples containing the start and end positions of each expression found.</returns>
    public static IEnumerable<(int Start, int End)> FindExpressions(SyntaxNode node)
    {
        var parenthesizedExpressions = node.DescendantNodes()
            .OfType<ParenthesizedExpressionSyntax>();

        foreach (var expr in parenthesizedExpressions)
        {
            // Check if this parenthesized expression contains an opening angle bracket
            if (ContainsAngleBracket(expr.OpenParenToken, expr.CloseParenToken))
            {
                yield return (expr.SpanStart, expr.Span.End);
            }
        }
    }

    /// <summary>
    /// Determines if a parenthesized expression contains an opening angle bracket token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checks if the tokens between the opening and closing parentheses include an
    /// opening angle bracket, which indicates an XCS component expression.
    /// </para>
    /// </remarks>
    /// <param name="openParen">The opening parenthesis token.</param>
    /// <param name="closeParen">The closing parenthesis token.</param>
    /// <returns>True if an opening angle bracket is found between the parentheses; otherwise false.</returns>
    private static bool ContainsAngleBracket(SyntaxToken openParen, SyntaxToken closeParen)
    {
        var current = openParen.GetNextToken();
        
        while (current != closeParen && current.Kind() != SyntaxKind.None)
        {
            if (current.Kind() == SyntaxKind.LessThanToken)
            {
                return true;
            }
            current = current.GetNextToken();
        }
        
        return false;
    }

    /// <summary>
    /// Gets the line and column information for a position in the given syntax tree.
    /// </summary>
    /// <param name="syntaxTree">The syntax tree to query.</param>
    /// <param name="position">The absolute position in the source text.</param>
    /// <returns>A tuple containing the line number and column number (both 1-indexed).</returns>
    public static (int Line, int Column) GetLineColumn(SyntaxTree syntaxTree, int position)
    {
        var lineSpan = syntaxTree.GetLineSpan(new TextSpan(position, 0));
        return (lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1);
    }
}