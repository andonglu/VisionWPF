using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Expressions
{
    /// <summary>
    /// 解析后的表达式。不可变，可在多个线程中重复求值。
    /// </summary>
    public sealed class CompiledExpression
    {
        private readonly ExprNode _root;

        public string Text { get; private set; }

        /// <summary>形如 模块.变量 的引用（花括号内的文本），去重并保持首次出现的顺序。</summary>
        public IReadOnlyList<string> References { get; private set; }

        /// <summary>不含点的名称（如数组处理中的 Item），由调用方在求值时提供取值。</summary>
        public IReadOnlyList<string> LocalNames { get; private set; }

        internal CompiledExpression(string text, ExprNode root, List<string> references, List<string> localNames)
        {
            Text = text;
            _root = root;
            References = references;
            LocalNames = localNames;
        }

        /// <summary>
        /// 在流程上下文中求值：模块.变量 引用按 <see cref="VariableReference"/> 解析，
        /// 局部名称交给 resolveLocal（未提供时视为未定义）。
        /// </summary>
        public object Evaluate(FlowContext ctx, Func<string, object> resolveLocal = null)
        {
            if (ctx == null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            return Evaluate(path =>
            {
                if (path.IndexOf('.') >= 0)
                {
                    return VariableReference.Parse(path).Resolve(ctx);
                }
                if (resolveLocal == null)
                {
                    throw new InvalidOperationException($"未定义的名称 {path}");
                }
                return resolveLocal(path);
            });
        }

        /// <summary>使用自定义解析器求值：resolve 接收花括号内的引用文本，返回引用的值。</summary>
        public object Evaluate(Func<string, object> resolve)
        {
            if (resolve == null)
            {
                throw new ArgumentNullException(nameof(resolve));
            }
            return _root.Evaluate(new EvalScope { Resolve = resolve });
        }

        public override string ToString()
        {
            return Text;
        }
    }

    /// <summary>
    /// 表达式解析器。语法：
    /// - 引用写在花括号内：{模块.变量}、{模块.数组[0]}、{模块.对象.成员}、{Loop.Index}；不含点的 {名称} 为局部名称。
    /// - 常量：整数、小数、科学计数法、"字符串"（支持 \" \\ \n \t）、true、false、pi。
    /// - 运算符（优先级从低到高）：?:、||、&amp;&amp;、== !=、&gt; &gt;= &lt; &lt;=、+ -、* / %、一元 ! -。
    /// - 函数见 <see cref="ExpressionFunctions.All"/>，函数名与 true / false / pi 不区分大小写。
    /// 解析结果按表达式文本缓存。
    /// </summary>
    public static class ExpressionParser
    {
        private const int MaxCacheSize = 4096;
        private static readonly ConcurrentDictionary<string, CompiledExpression> Cache =
            new ConcurrentDictionary<string, CompiledExpression>(StringComparer.Ordinal);

        /// <summary>解析表达式；语法错误时抛出 <see cref="ExpressionException"/>。</summary>
        public static CompiledExpression Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ExpressionException("表达式不能为空", -1);
            }
            if (Cache.TryGetValue(text, out CompiledExpression cached))
            {
                return cached;
            }

            CompiledExpression expression = new Parser(text).ParseAll();
            if (Cache.Count >= MaxCacheSize)
            {
                Cache.Clear();
            }
            Cache[text] = expression;
            return expression;
        }

        public static bool TryParse(string text, out CompiledExpression expression, out ExpressionException error)
        {
            try
            {
                expression = Parse(text);
                error = null;
                return true;
            }
            catch (ExpressionException ex)
            {
                expression = null;
                error = ex;
                return false;
            }
        }

        private sealed class Parser
        {
            private readonly string _text;
            private readonly List<Token> _tokens;
            private readonly List<string> _references = new List<string>();
            private readonly List<string> _localNames = new List<string>();
            private int _index;

            public Parser(string text)
            {
                _text = text;
                _tokens = ExpressionLexer.Tokenize(text);
            }

            private Token Current
            {
                get { return _tokens[_index]; }
            }

            public CompiledExpression ParseAll()
            {
                ExprNode root = ParseConditional();
                if (Current.Kind != TokenKind.End)
                {
                    throw new ExpressionException($"多余的内容 '{Describe(Current)}'", Current.Position);
                }
                return new CompiledExpression(_text, root, _references, _localNames);
            }

            private ExprNode ParseConditional()
            {
                ExprNode condition = ParseBinary(0);
                if (Current.Kind != TokenKind.Question)
                {
                    return condition;
                }
                int position = Current.Position;
                _index++;
                ExprNode whenTrue = ParseConditional();
                Expect(TokenKind.Colon, "条件运算缺少 :");
                ExprNode whenFalse = ParseConditional();
                return new ConditionalNode { Position = position, Condition = condition, WhenTrue = whenTrue, WhenFalse = whenFalse };
            }

            private static readonly string[][] BinaryLevels =
            {
                new[] { "||" },
                new[] { "&&" },
                new[] { "==", "!=" },
                new[] { ">", ">=", "<", "<=" },
                new[] { "+", "-" },
                new[] { "*", "/", "%" }
            };

            private ExprNode ParseBinary(int level)
            {
                if (level >= BinaryLevels.Length)
                {
                    return ParseUnary();
                }
                ExprNode left = ParseBinary(level + 1);
                while (Current.Kind == TokenKind.Operator && Array.IndexOf(BinaryLevels[level], Current.Text) >= 0)
                {
                    Token op = Current;
                    _index++;
                    ExprNode right = ParseBinary(level + 1);
                    left = new BinaryNode { Position = op.Position, Operator = op.Text, Left = left, Right = right };
                }
                return left;
            }

            private ExprNode ParseUnary()
            {
                if (Current.Kind == TokenKind.Operator && (Current.Text == "!" || Current.Text == "-"))
                {
                    Token op = Current;
                    _index++;
                    ExprNode operand = ParseUnary();
                    if (op.Text == "-" && operand is ConstantNode constant && ExpressionValues.IsNumber(constant.Value))
                    {
                        return new ConstantNode
                        {
                            Position = op.Position,
                            Value = constant.Value is long l ? (object)(-l) : -(double)constant.Value
                        };
                    }
                    return new UnaryNode { Position = op.Position, Operator = op.Text, Operand = operand };
                }
                return ParsePrimary();
            }

            private ExprNode ParsePrimary()
            {
                Token token = Current;
                switch (token.Kind)
                {
                    case TokenKind.Number:
                        _index++;
                        return new ConstantNode { Position = token.Position, Value = token.Value };
                    case TokenKind.String:
                        _index++;
                        return new ConstantNode { Position = token.Position, Value = token.Text };
                    case TokenKind.Reference:
                        _index++;
                        return ParseReference(token);
                    case TokenKind.Identifier:
                        _index++;
                        return ParseIdentifier(token);
                    case TokenKind.LeftParen:
                        _index++;
                        ExprNode inner = ParseConditional();
                        Expect(TokenKind.RightParen, "缺少右括号 )");
                        return inner;
                    case TokenKind.End:
                        throw new ExpressionException("表达式不完整", token.Position);
                    default:
                        throw new ExpressionException($"此处不能出现 '{Describe(token)}'", token.Position);
                }
            }

            private ExprNode ParseReference(Token token)
            {
                string path = token.Text;
                if (path.Length == 0)
                {
                    throw new ExpressionException("引用不能为空", token.Position);
                }
                if (path.IndexOf('.') >= 0)
                {
                    try
                    {
                        VariableReference.Parse(path);
                    }
                    catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is OverflowException)
                    {
                        throw new ExpressionException($"引用 {{{path}}} 格式错误：{ex.Message}", token.Position, ex);
                    }
                    AddDistinct(_references, path);
                }
                else
                {
                    if (!ToolMetadata.IsValidOutputName(path))
                    {
                        throw new ExpressionException($"名称 {{{path}}} 不能包含空白或 [ ] 字符", token.Position);
                    }
                    AddDistinct(_localNames, path);
                }
                return new ReferenceNode { Position = token.Position, Path = path };
            }

            private ExprNode ParseIdentifier(Token token)
            {
                string name = token.Text;
                if (Current.Kind != TokenKind.LeftParen)
                {
                    if (string.Equals(name, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ConstantNode { Position = token.Position, Value = true };
                    }
                    if (string.Equals(name, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ConstantNode { Position = token.Position, Value = false };
                    }
                    if (string.Equals(name, "pi", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ConstantNode { Position = token.Position, Value = Math.PI };
                    }
                    throw new ExpressionException(
                        ExpressionFunctions.TryGet(name, out _)
                            ? $"函数 {name} 缺少括号"
                            : $"未知的名称 '{name}'，变量引用请写在花括号内，如 {{模块.变量}}",
                        token.Position);
                }

                if (!ExpressionFunctions.TryGet(name, out ExpressionFunction function))
                {
                    throw new ExpressionException($"未知的函数 '{name}'", token.Position);
                }
                _index++;
                var arguments = new List<ExprNode>();
                if (Current.Kind != TokenKind.RightParen)
                {
                    arguments.Add(ParseConditional());
                    while (Current.Kind == TokenKind.Comma)
                    {
                        _index++;
                        arguments.Add(ParseConditional());
                    }
                }
                Expect(TokenKind.RightParen, $"函数 {function.Name} 缺少右括号 )");

                if (arguments.Count < function.MinArgs || arguments.Count > function.MaxArgs)
                {
                    string expected = function.MinArgs == function.MaxArgs
                        ? function.MinArgs.ToString()
                        : function.MaxArgs == int.MaxValue
                            ? $"至少 {function.MinArgs}"
                            : $"{function.MinArgs} 到 {function.MaxArgs}";
                    throw new ExpressionException(
                        $"函数 {function.Signature} 需要 {expected} 个参数，实际为 {arguments.Count} 个", token.Position);
                }
                return new CallNode { Position = token.Position, Function = function, Arguments = arguments };
            }

            private void Expect(TokenKind kind, string message)
            {
                if (Current.Kind != kind)
                {
                    throw new ExpressionException(message, Current.Position);
                }
                _index++;
            }

            private static void AddDistinct(List<string> list, string value)
            {
                if (!list.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(value);
                }
            }

            private static string Describe(Token token)
            {
                switch (token.Kind)
                {
                    case TokenKind.Reference: return "{" + token.Text + "}";
                    case TokenKind.String: return "\"" + token.Text + "\"";
                    default: return token.Text;
                }
            }
        }
    }
}
