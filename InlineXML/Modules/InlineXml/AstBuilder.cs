using InlineXML.Modules.InlineXml;

namespace InlineXML.Modules.InlineXml;

/// <summary>
/// the ast builder is responsible for taking a flat stream of tokens
/// and constructing a hierarchical tree structure. it understands the 
/// nesting rules of XML and how they interleave with C# expressions.
/// </summary>
public class AstBuilder
{
   private Token[] _tokens;

   /// <summary>
   /// the main entry point for building the tree. we take the tokens
   /// produced by the parser and recursively process them into a 
   /// list of nodes representing the declarative structure.
   /// </summary>
   /// <param name="tokens"></param>
   /// <param name="source"></param>
   /// <returns></returns>
   public List<AstNode> Build(Token[] tokens, ReadOnlySpan<char> source)
   {
      _tokens = tokens;
      int i = 0;
      
      // we begin parsing at the root level. siblings are nodes 
      // that share the same parent or reside at the root.
      return ParseSiblings(ref i, source, null);
   }

   /// <summary>
   /// parses a sequence of nodes until a specific stop condition is met,
   /// such as a closing tag or a structural C# token.
   /// </summary>
   private List<AstNode> ParseSiblings(ref int i, ReadOnlySpan<char> source, string stopAtTag)
   {
      var nodes = new List<AstNode>();
      while (i < _tokens.Length)
      {
         // check if we've hit the closing tag for the current context.
         if (_tokens[i].Kind == TokenKind.TAG_OPEN && stopAtTag != null)
         {
            if (source.Slice(_tokens[i].Start, _tokens[i].End - _tokens[i].Start).ToString() == "</" && 
                i + 1 < _tokens.Length && 
                source.Slice(_tokens[i+1].Start, _tokens[i+1].End - _tokens[i+1].Start).ToString() == stopAtTag) break;
         }

         // we don't treat structural C# tokens (like trailing semicolons)
         // as child nodes. these are boundaries for our parser.
         if (_tokens[i].Kind == TokenKind.RIGHT_PAREN || _tokens[i].Kind == TokenKind.SEMICOLON) break;
         
         var node = ParseNode(ref i, source);
         if (node != null) nodes.Add(node);
         else i++;
      }
      return nodes;
   }

   /// <summary>
   /// identifies the type of the next node and delegates to the
   /// appropriate specialized parsing method.
   /// </summary>
   private AstNode ParseNode(ref int i, ReadOnlySpan<char> source)
   {
      if (i >= _tokens.Length) return null;
      var token = _tokens[i];

      // if we see a tag opening, we treat it as an element.
      if (token.Kind == TokenKind.TAG_OPEN) return ParseElement(ref i, source);

      // handle inline C# expressions, including complex ones like .map()
      if (token.Kind == TokenKind.ATTRIBUTE_EXPRESSION)
      {
         var text = source.Slice(token.Start, token.End - token.Start).ToString();
         
         // we skip stray closing braces that might be caught in the token stream.
         if (text == "}") { i++; return null; } 

         var node = new ExpressionNode { 
            Expression = text, 
            SourceStart = token.Start, 
            SourceEnd = token.End, 
            Children = new List<AstNode>() 
         };
         i++;
         
         // if the expression contains an arrow, we assume it's a mapping
         // function and look for nested XML children inside it.
         if (text.Contains("=>"))
         {
            if (i < _tokens.Length && _tokens[i].Kind == TokenKind.LEFT_PAREN) i++;
            node.Children = ParseSiblings(ref i, source, null);
            
            // CRITICAL: we swallow trailing structural tokens like ")) }"
            // these belong to the C# expression context, not the XML children.
            while (i < _tokens.Length)
            {
               var t = _tokens[i];
               var val = source.Slice(t.Start, t.End - t.Start).ToString();
               if (t.Kind == TokenKind.RIGHT_PAREN || (t.Kind == TokenKind.ATTRIBUTE_EXPRESSION && val.Contains("}")))
               {
                  node.Expression += val;
                  node.SourceEnd = t.End;
                  i++;
                  if (val.Contains("}")) break;
               }
               else break;
            }
         }
         return node;
      }

      // handle plain text content or attribute values.
      if (token.Kind == TokenKind.ATTRIBUTE_NAME)
      {
         var raw = source.Slice(token.Start, token.End - token.Start).ToString();
         i++;
         if (string.IsNullOrWhiteSpace(raw)) return null;
         return new StringLiteralNode { Value = raw, SourceStart = token.Start, SourceEnd = token.End };
      }
      return null;
   }

   /// <summary>
   /// parses a full XML element, including its tag name, attributes, 
   /// and any nested children.
   /// </summary>
   private ElementNode ParseElement(ref int i, ReadOnlySpan<char> source)
   {
      var start = _tokens[i].Start;
      i++; // move past '<'
      
      var name = source.Slice(_tokens[i].Start, _tokens[i].End - _tokens[i].Start).ToString();
      var node = new ElementNode { TagName = name, SourceStart = start };
      i++; 
      
      // parse attributes until we hit the closing '>' of the opening tag.
      while (i < _tokens.Length && _tokens[i].Kind != TokenKind.TAG_CLOSE)
      {
         if (_tokens[i].Kind == TokenKind.ATTRIBUTE_NAME)
         {
            var attr = source.Slice(_tokens[i].Start, _tokens[i].End - _tokens[i].Start).ToString();
            i++;
            
            // if an attribute is followed by an equals sign, we parse its value.
            if (i < _tokens.Length && _tokens[i].Kind == TokenKind.ATTRIBUTE_EQUALS)
            {
               i++;
               var val = ParseNode(ref i, source);
               if (val != null) node.Attributes.Add((attr, val));
            }
         } else i++;
      }

      if (i < _tokens.Length) i++; // move past '>'
      
      // recursively parse any children found inside the element.
      node.Children = ParseSiblings(ref i, source, name);
      
      // handle the closing tag logic (e.g., </div>)
      if (i < _tokens.Length && _tokens[i].Kind == TokenKind.TAG_OPEN)
      {
         i += 2; // move past '</' and the tag name
         if (i < _tokens.Length && _tokens[i].Kind == TokenKind.TAG_CLOSE) 
         { 
            node.SourceEnd = _tokens[i].End; 
            i++; 
         }
      }
      return node;
   }
}