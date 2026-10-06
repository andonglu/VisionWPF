using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VisionFlow.Expressions
{
    internal enum TokenKind
    {
        Number,
        String,
        Identifier,
        Reference,
        Operator,
        LeftParen,
        RightParen,
        Comma,
        Question,
        Colon,
        End
    }

    internal sealed class Token
    {
        public TokenKind Kind { get; set; }
        /// <summary>运算符、标识符、引用内容（不含花括号）或字符串内容。</summary>
        public string Text { get; set; }
        /// <summary>数值常量（long 或 double）。</summary>
        public object Value { get; set; }
        public int Position { get; set; }
    }

    /// <summary>词法分析：把表达式文本切分为记号。</summary>
    internal static class ExpressionLexer
    {
        private static readonly string[] TwoCharOperators = { "||", "&&", "==", "!=", ">=", "<=" };

        public static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                int start = i;
                if (char.IsDigit(c))
                {
                    tokens.Add(ReadNumber(text, ref i));
                }
                else if (c == '"')
                {
                    tokens.Add(ReadString(text, ref i));
                }
                else if (c == '{')
                {
                    int end = text.IndexOf('}', i + 1);
                    if (end < 0)
                    {
                        throw new ExpressionException("引用缺少右花括号 }", start);
                    }
                    string content = text.Substring(i + 1, end - i - 1);
                    if (content.IndexOf('{') >= 0)
                    {
                        throw new ExpressionException("引用不能嵌套花括号", start);
                    }
                    tokens.Add(new Token { Kind = TokenKind.Reference, Text = content.Trim(), Position = start });
                    i = end + 1;
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    {
                        i++;
                    }
                    tokens.Add(new Token { Kind = TokenKind.Identifier, Text = text.Substring(start, i - start), Position = start });
                }
                else
                {
                    tokens.Add(ReadSymbol(text, ref i));
                }
            }
            tokens.Add(new Token { Kind = TokenKind.End, Text = string.Empty, Position = text.Length });
            return tokens;
        }

        private static Token ReadNumber(string text, ref int i)
        {
            int start = i;
            bool isDecimal = false;
            while (i < text.Length && char.IsDigit(text[i]))
            {
                i++;
            }
            if (i + 1 < text.Length && text[i] == '.' && char.IsDigit(text[i + 1]))
            {
                isDecimal = true;
                i++;
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }
            }
            if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
            {
                int exponentStart = i;
                i++;
                if (i < text.Length && (text[i] == '+' || text[i] == '-'))
                {
                    i++;
                }
                if (i >= text.Length || !char.IsDigit(text[i]))
                {
                    throw new ExpressionException("科学计数法缺少指数", exponentStart);
                }
                isDecimal = true;
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }
            }
            if (i < text.Length && (char.IsLetter(text[i]) || text[i] == '_'))
            {
                throw new ExpressionException($"数值后紧跟了字母 '{text[i]}'", i);
            }

            string literal = text.Substring(start, i - start);
            object value;
            if (isDecimal)
            {
                value = double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            else if (long.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out long integer))
            {
                value = integer;
            }
            else
            {
                throw new ExpressionException($"整数 {literal} 超出范围", start);
            }
            return new Token { Kind = TokenKind.Number, Text = literal, Value = value, Position = start };
        }

        private static Token ReadString(string text, ref int i)
        {
            int start = i;
            var builder = new StringBuilder();
            i++;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '"')
                {
                    i++;
                    return new Token { Kind = TokenKind.String, Text = builder.ToString(), Position = start };
                }
                if (c == '\\')
                {
                    if (i + 1 >= text.Length)
                    {
                        break;
                    }
                    char escaped = text[i + 1];
                    switch (escaped)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case 'n': builder.Append('\n'); break;
                        case 't': builder.Append('\t'); break;
                        default:
                            throw new ExpressionException($"不支持的转义字符 \\{escaped}", i);
                    }
                    i += 2;
                    continue;
                }
                builder.Append(c);
                i++;
            }
            throw new ExpressionException("字符串缺少结束的双引号", start);
        }

        private static Token ReadSymbol(string text, ref int i)
        {
            int start = i;
            if (i + 1 < text.Length)
            {
                string pair = text.Substring(i, 2);
                if (Array.IndexOf(TwoCharOperators, pair) >= 0)
                {
                    i += 2;
                    return new Token { Kind = TokenKind.Operator, Text = pair, Position = start };
                }
            }

            char c = text[i];
            i++;
            switch (c)
            {
                case '+':
                case '-':
                case '*':
                case '/':
                case '%':
                case '!':
                case '>':
                case '<':
                    return new Token { Kind = TokenKind.Operator, Text = c.ToString(), Position = start };
                case '(':
                    return new Token { Kind = TokenKind.LeftParen, Text = "(", Position = start };
                case ')':
                    return new Token { Kind = TokenKind.RightParen, Text = ")", Position = start };
                case ',':
                    return new Token { Kind = TokenKind.Comma, Text = ",", Position = start };
                case '?':
                    return new Token { Kind = TokenKind.Question, Text = "?", Position = start };
                case ':':
                    return new Token { Kind = TokenKind.Colon, Text = ":", Position = start };
                case '=':
                    throw new ExpressionException("比较相等请使用 ==", start);
                case '&':
                    throw new ExpressionException("逻辑与请使用 &&", start);
                case '|':
                    throw new ExpressionException("逻辑或请使用 ||", start);
                case '}':
                    throw new ExpressionException("多余的右花括号 }", start);
                default:
                    throw new ExpressionException($"无法识别的字符 '{c}'", start);
            }
        }
    }
}
