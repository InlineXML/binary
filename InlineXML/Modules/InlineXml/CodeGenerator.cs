using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InlineXML.Modules.InlineXml;

/// <summary>
/// the code generator is responsible for taking our processed AST
/// and turning it into legal, compilable C# code. it also handles
/// the generation of source maps so we can bridge the gap between
/// the generated output and the original XCS source.
/// </summary>
public class CodeGenerator
{
   private readonly StringBuilder _output = new();
   private List<SourceMapEntry> _sourceMap = new();
   private int _indent = 0;

   public CodeGenerator(string f, string m) { }

   /// <summary>
   /// generates a C# string from a list of AST nodes. 
   /// we return the full string and an out-parameter for the 
   /// source map entries required for debugging and diagnostics.
   /// </summary>
   /// <param name="nodes"></param>
   /// <param name="sourceMap"></param>
   /// <returns></returns>
   public string Generate(List<AstNode> nodes, out List<SourceMapEntry> sourceMap)
   {
      // reset the state of the generator for a fresh run
      _output.Clear(); 
      _sourceMap.Clear(); 
      _indent = 0;

      // start the expression wrapper
      AppendLine("("); 
      _indent++;

      // process all top-level nodes
      GenerateNodeList(nodes);

      // close the expression wrapper
      _indent--; 
      Append($"\n{GetIndent()});");

      sourceMap = _sourceMap;
      return _output.ToString();
   }

   /// <summary>
   /// iterates through a list of nodes and ensures they are
   /// delimited correctly by commas for usage inside 
   /// method arguments or array initializers.
   /// </summary>
   /// <param name="nodes"></param>
   private void GenerateNodeList(List<AstNode> nodes)
   {
      for (int i = 0; i < nodes.Count; i++)
      {
         GenerateNode(nodes[i]);
         
         // if we aren't at the last node, we need to add 
         // a comma to separate these in the generated factory call.
         if (i < nodes.Count - 1) 
         { 
            _output.Append(","); 
            AppendLine(""); 
         }
      }
   }

   /// <summary>
   /// handles the specific generation logic for different node types.
   /// this is where we decide how an element, an expression, or a
   /// string literal should look in the final output.
   /// </summary>
   /// <param name="node"></param>
   private void GenerateNode(AstNode node)
   {
      // capture the current position in the output buffer for source mapping
      int start = _output.Length;

      if (node is ElementNode el) 
      {
         GenerateElement(el);
      }
      else if (node is ExpressionNode ex)
      {
         // strip the XML braces to reveal the raw C# expression
         var clean = StripBraces(ex.Expression);

         // if the expression has children (like a .map function),
         // we need to wrap the children back into the mapping arrow.
         if (ex.Children != null && ex.Children.Count > 0)
         {
            int arrow = clean.IndexOf("=>");
            string head = clean.Substring(0, arrow + 2).Trim();

            Append($"{GetIndent()}{head} (");
            _indent++; 
            AppendLine("");

            GenerateNodeList(ex.Children);

            _indent--; 
            AppendLine("");
            Append($"{GetIndent()})"); 
            
            // if the original expression had a closing paren for the map, 
            // we ensure it is preserved here.
            int mapClose = clean.LastIndexOf(')');
            if (mapClose > arrow) Append(")"); 
         }
         else 
         {
            Append($"{GetIndent()}{clean}");
         }
      }
      else if (node is StringLiteralNode s)
      {
         string v = s.Value.Trim();

         // we only output string literals if they contain actual content.
         // basic text inside an XML tag needs to be quoted for the factory call.
         if (!string.IsNullOrEmpty(v)) 
         {
            Append($"{GetIndent()}\"{v.Replace("\"", "\\\"")}\"");
         }
      }

      // record the mapping between the original source span and the generated span
      _sourceMap.Add(new SourceMapEntry 
      { 
         OriginalStart = node.SourceStart, 
         OriginalEnd = node.SourceEnd, 
         TransformedStart = start, 
         TransformedEnd = _output.Length 
      });
   }

   /// <summary>
   /// generates the Factory.CreateElement call for a specific XML tag.
   /// handles the recursive generation of attributes and child nodes.
   /// </summary>
   /// <param name="element"></param>
   private void GenerateElement(ElementNode element)
   {
      AppendLine($"{GetIndent()}Factory.CreateElement(");
      _indent++;

      // the first argument is always the tag name
      AppendLine($"{GetIndent()}\"{element.TagName}\",");

      // the second argument is the props object, which we pascal-case
      // to match standard C# naming conventions.
      Append($"{GetIndent()}new {ToPascalCase(element.TagName)}Props");
      
      if (element.Attributes.Count > 0)
      {
         _output.Append("\n" + GetIndent() + "{\n"); 
         _indent++;

         for (int i = 0; i < element.Attributes.Count; i++)
         {
            var (n, v) = element.Attributes[i];
            string valueString;

            // if the attribute value is a string literal, we need to ensure
            // it is properly quoted. if it's an expression, we strip the braces.
            if (v is StringLiteralNode s)
            {
               var rawValue = s.Value.Trim('\"', '\'');
               valueString = $"\"{rawValue}\"";
            }
            else
            {
               valueString = StripBraces(((ExpressionNode)v).Expression);
            }

            Append($"{GetIndent()}{ToPascalCase(n)} = {valueString}");

            if (i < element.Attributes.Count - 1) _output.Append(",");
            _output.Append("\n");
         }

         _indent--; 
         Append($"{GetIndent()}}}");
      }
      else 
      {
         // empty props object if no attributes are present
         _output.Append("()");
      }

      // if the element has children, they are passed as subsequent
      // arguments to the CreateElement method.
      if (element.Children.Count > 0) 
      { 
         _output.Append(","); 
         AppendLine(""); 
         GenerateNodeList(element.Children); 
      }

      _indent--; 
      _output.Append("\n" + GetIndent() + ")");
   }

   /// <summary>
   /// helper to remove the curly braces from an inline expression
   /// so it can be injected directly into the generated C#.
   /// </summary>
   /// <param name="e"></param>
   /// <returns></returns>
   private string StripBraces(string e) 
   {
      var t = e.Trim();
      if (t.StartsWith("{") && t.EndsWith("}")) 
      {
         t = t.Substring(1, t.Length - 2).Trim();
      }
      return t;
   }

   private void Append(string t) => _output.Append(t);
   private void AppendLine(string t) => _output.AppendLine(t);
   private string GetIndent() => new string(' ', _indent * 3);
   
   /// <summary>
   /// converts a string to PascalCase, which is used for mapping
   /// html-style tags and attributes to C# property names.
   /// </summary>
   /// <param name="s"></param>
   /// <returns></returns>
   private string ToPascalCase(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
}