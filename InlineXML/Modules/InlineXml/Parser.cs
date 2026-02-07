using System;
using System.Collections.Generic;

namespace InlineXML.Modules.InlineXml;

/// <summary>
/// the parser is a manual lexer designed to scan a stream of characters
/// and produce a flat token stream. it handles the transition between
/// XML-like syntax and the C# expressions embedded within it.
/// </summary>
public class Parser
{
   private int _tokenCount = 0;
   public Parser(string factory, string method) { }

   /// <summary>
   /// takes a raw span of source text and breaks it down into tokens.
   /// we use ReadOnlySpan here to ensure that we aren't allocating
   /// unnecessary strings while we scan the source.
   /// </summary>
   /// <param name="src"></param>
   /// <returns></returns>
   public Token[] Parse(ref ReadOnlySpan<char> src)
   {
      var tokens = new Token[src.Length];
      _tokenCount = 0;
      int pointer = 0;

      // we skip everything until we hit the first tag open
      // as our tool only cares about the XML blocks.
      while (pointer < src.Length && src[pointer] != '<') pointer++;

      if (pointer < src.Length) 
      {
         ParseInternal(ref src, ref tokens, ref pointer, 0, false);
      }

      // resize the array to the exact count so we don't pass
      // around a massive array of empty tokens.
      var result = new Token[_tokenCount];
      Array.Copy(tokens, result, _tokenCount);
      return result;
   }

   /// <summary>
   /// the internal scanning loop that decides how to categorize
   /// each character based on the current context (content vs attributes).
   /// </summary>
   private void ParseInternal(ref ReadOnlySpan<char> src, ref Token[] tokens, ref int pointer, int startOffset, bool isContent)
   {
      while (pointer < src.Length)
      {
         var current = src[pointer];

         // handling tag opening and closing brackets
         if (current == '<')
         {
            int s = pointer;
            bool isClosing = pointer + 1 < src.Length && src[pointer + 1] == '/';
            AddToken(ref tokens, TokenKind.TAG_OPEN, startOffset + s, startOffset + s + (isClosing ? 2 : 1));
            pointer += (isClosing ? 2 : 1);
            ParseTagName(ref src, ref tokens, ref pointer, startOffset);
            isContent = false;
         }
         else if (current == '>')
         {
            AddToken(ref tokens, TokenKind.TAG_CLOSE, startOffset + pointer, startOffset + pointer + 1);
            pointer++;
            isContent = true;
         }
         // inline expressions are the core of our C# integration
         else if (current == '{')
         {
            ParseExpression(ref src, ref tokens, ref pointer, startOffset);
         }
         // these structural tokens are captured so the AST builder
         // can correctly close out nested mapping functions.
         else if (current == '}' || current == ')' || current == ';')
         {
            var kind = current == '}' ? TokenKind.ATTRIBUTE_EXPRESSION : (current == ')' ? TokenKind.RIGHT_PAREN : TokenKind.SEMICOLON);
            AddToken(ref tokens, kind, startOffset + pointer, startOffset + pointer + 1);
            pointer++;
         }
         else if (!isContent && current == '=')
         {
            AddToken(ref tokens, TokenKind.ATTRIBUTE_EQUALS, startOffset + pointer, startOffset + pointer + 1);
            pointer++;
         }
         // quoted attributes need careful handling to capture the whole string literal
         else if (!isContent && current == '"')
         {
            int s = pointer; pointer++;
            while (pointer < src.Length && src[pointer] != '"') pointer++;
            if (pointer < src.Length) pointer++;
            AddToken(ref tokens, TokenKind.ATTRIBUTE_NAME, startOffset + s, startOffset + pointer);
         }
         else if (char.IsWhiteSpace(current)) 
         {
            pointer++;
         }
         else
         {
            // fallback for general text or attribute names
            int s = pointer;
            while (pointer < src.Length)
            {
               char next = src[pointer];
               if (next == '<' || next == '{' || next == '}' || next == ')' || next == '(') break;
               if (!isContent && (char.IsWhiteSpace(next) || next == '=' || next == '>' || next == '"')) break;
               pointer++;
            }
            if (pointer > s) AddToken(ref tokens, TokenKind.ATTRIBUTE_NAME, startOffset + s, startOffset + pointer);
         }
      }
   }

   /// <summary>
   /// scans balanced C# expressions. it is recursive-aware, meaning it can
   /// detect when an XML tag is nested inside a C# mapping function
   /// like items.map(i => <div>{i}</div>).
   /// </summary>
   private void ParseExpression(ref ReadOnlySpan<char> src, ref Token[] tokens, ref int pointer, int startOffset)
   {
      int start = pointer;
      int depth = 0;

      while (pointer < src.Length)
      {
         if (src[pointer] == '{') 
         { 
            depth++; pointer++; 
         }
         else if (src[pointer] == '}')
         {
            depth--; pointer++;
            // once we return to the root depth, we've captured the full expression
            if (depth == 0)
            {
               AddToken(ref tokens, TokenKind.ATTRIBUTE_EXPRESSION, startOffset + start, startOffset + pointer);
               return;
            }
         }
         // specialized logic for detecting the start of a nested XML block inside a map
         else if (depth == 1 && src[pointer] == '(')
         {
            int temp = pointer + 1;
            while (temp < src.Length && char.IsWhiteSpace(src[temp])) temp++;
            
            // if we see a '(' followed by a '<', we know we are entering a nested
            // XML structure inside the C# code.
            if (temp < src.Length && src[temp] == '<')
            {
               AddToken(ref tokens, TokenKind.ATTRIBUTE_EXPRESSION, startOffset + start, startOffset + pointer);
               AddToken(ref tokens, TokenKind.LEFT_PAREN, startOffset + pointer, startOffset + pointer + 1);
               pointer++;
               
               // recurse back into the internal parser for the nested block
               ParseInternal(ref src, ref tokens, ref pointer, startOffset, false);
               start = pointer;
            }
            else pointer++;
         }
         else pointer++;
      }
   }

   /// <summary>
   /// handles the extraction of tag names, allowing for digits, 
   /// hyphens, and underscores to support custom elements.
   /// </summary>
   private void ParseTagName(ref ReadOnlySpan<char> src, ref Token[] tokens, ref int pointer, int startOffset)
   {
      int s = pointer;
      while (pointer < src.Length && (char.IsLetterOrDigit(src[pointer]) || src[pointer] == '-' || src[pointer] == '_')) 
      {
         pointer++;
      }
      AddToken(ref tokens, TokenKind.TAG_NAME, startOffset + s, startOffset + pointer);
   }

   /// <summary>
   /// adds a new token to our growing list, resizing the internal 
   /// storage array if we hit the capacity limit.
   /// </summary>
   private void AddToken(ref Token[] tokens, TokenKind kind, int start, int end)
   {
      if (_tokenCount >= tokens.Length) 
      {
         Array.Resize(ref tokens, tokens.Length * 2);
      }
      tokens[_tokenCount++] = new Token { Kind = kind, Start = start, End = end };
   }
}