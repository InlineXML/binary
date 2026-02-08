using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace InlineXML.Modules.Roslyn;

public static class ExpressionLocator
{
    public static IEnumerable<(int Start, int End)> FindExpressions(SyntaxTree syntaxTree)
    {
        var root = syntaxTree.GetRoot();
        return FindExpressions(root);
    }

    public static IEnumerable<(int Start, int End)> FindExpressions(SyntaxNode node)
    {
        var text = node.SyntaxTree.GetText();
        var parenthesizedExpressions = node.DescendantNodes()
            .OfType<ParenthesizedExpressionSyntax>();

        foreach (var expr in parenthesizedExpressions)
        {
            var firstInnerToken = expr.OpenParenToken.GetNextToken();
        
            if (firstInnerToken.IsKind(SyntaxKind.LessThanToken))
            {
                var potentialTagName = firstInnerToken.GetNextToken();
                if (!potentialTagName.IsKind(SyntaxKind.IdentifierToken)) 
                    continue;

                int openParenPos = expr.OpenParenToken.SpanStart;
                int xmlStart = -1;
                for (int i = openParenPos; i < text.Length; i++)
                {
                    if (text[i] == '<') { xmlStart = i; break; }
                }

                if (xmlStart == -1) continue;

                // FIND THE MATCHING CLOSING PAREN
                int balance = 0;
                int closingParenPos = -1;
                for (int i = openParenPos; i < text.Length; i++)
                {
                    if (text[i] == '(') balance++;
                    else if (text[i] == ')') balance--;

                    if (balance == 0) { closingParenPos = i; break; }
                }

                if (closingParenPos == -1) continue;

                // CRITICAL: The range MUST end at the last '>' before the closing paren
                int xmlEnd = -1;
                for (int i = closingParenPos - 1; i >= xmlStart; i--)
                {
                    if (text[i] == '>') { xmlEnd = i + 1; break; }
                }

                if (xmlEnd != -1 && xmlStart < xmlEnd)
                {
                    yield return (xmlStart, xmlEnd);
                }
            }
        }
    }

    public static (int Line, int Column) GetLineColumn(SyntaxTree syntaxTree, int position)
    {
        var lineSpan = syntaxTree.GetLineSpan(new TextSpan(position, 0));
        return (lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1);
    }
}