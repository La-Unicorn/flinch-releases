using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flinch.Core
{
    public enum MathNodeType
    {
        Number,
        Variable,
        BinaryOp,
        UnaryOp,
        Function,
        Equation
    }

    [JsonDerivedType(typeof(MathNumberNode), typeDiscriminator: "number")]
    [JsonDerivedType(typeof(MathVariableNode), typeDiscriminator: "variable")]
    [JsonDerivedType(typeof(MathBinaryOpNode), typeDiscriminator: "binaryOp")]
    [JsonDerivedType(typeof(MathUnaryOpNode), typeDiscriminator: "unaryOp")]
    [JsonDerivedType(typeof(MathFunctionNode), typeDiscriminator: "function")]
    [JsonDerivedType(typeof(MathEquationNode), typeDiscriminator: "equation")]
    public abstract class MathAstNode
    {
        public abstract MathNodeType NodeType { get; }

        public abstract string ToSafeLatex();
        public abstract string ToDisplayString();
        public abstract double Evaluate(Dictionary<string, double>? variables = null);
        public abstract HashSet<string> GetVariables();
    }

    public sealed class MathNumberNode : MathAstNode
    {
        public override MathNodeType NodeType => MathNodeType.Number;
        public double Value { get; set; }
        public string? FractionRepresentation { get; set; }

        public MathNumberNode() { }
        public MathNumberNode(double value, string? fraction = null)
        {
            Value = value;
            FractionRepresentation = fraction;
        }

        public override string ToSafeLatex()
        {
            if (!string.IsNullOrEmpty(FractionRepresentation))
                return FractionRepresentation;
            return Value.ToString(CultureInfo.InvariantCulture);
        }

        public override string ToDisplayString()
        {
            return Value.ToString(CultureInfo.InvariantCulture);
        }

        public override double Evaluate(Dictionary<string, double>? variables = null) => Value;

        public override HashSet<string> GetVariables() => new();
    }

    public sealed class MathVariableNode : MathAstNode
    {
        public override MathNodeType NodeType => MathNodeType.Variable;
        public string Name { get; set; } = "x";

        public MathVariableNode() { }
        public MathVariableNode(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !char.IsLetter(name[0]))
                throw new ArgumentException("Variable name must be an alphabetic identifier", nameof(name));
            Name = name.Trim();
        }

        public override string ToSafeLatex() => Name;
        public override string ToDisplayString() => Name;

        public override double Evaluate(Dictionary<string, double>? variables = null)
        {
            if (variables != null && variables.TryGetValue(Name, out double val))
                return val;
            throw new InvalidOperationException($"Variable '{Name}' is unbound during evaluation.");
        }

        public override HashSet<string> GetVariables() => new() { Name };
    }

    public sealed class MathBinaryOpNode : MathAstNode
    {
        public override MathNodeType NodeType => MathNodeType.BinaryOp;
        public string Operator { get; set; } = "+";
        public MathAstNode Left { get; set; } = default!;
        public MathAstNode Right { get; set; } = default!;

        public MathBinaryOpNode() { }
        public MathBinaryOpNode(string op, MathAstNode left, MathAstNode right)
        {
            Operator = op;
            Left = left ?? throw new ArgumentNullException(nameof(left));
            Right = right ?? throw new ArgumentNullException(nameof(right));
        }

        public override string ToSafeLatex()
        {
            return Operator switch
            {
                "+" => $"{Left.ToSafeLatex()} + {Right.ToSafeLatex()}",
                "-" => $"{Left.ToSafeLatex()} - {Right.ToSafeLatex()}",
                "*" => $"{Left.ToSafeLatex()} \\cdot {Right.ToSafeLatex()}",
                "/" => $"\\frac{{{Left.ToSafeLatex()}}}{{{Right.ToSafeLatex()}}}",
                "^" => $"{{{Left.ToSafeLatex()}}}^{{{Right.ToSafeLatex()}}}",
                _ => $"{Left.ToSafeLatex()} {Operator} {Right.ToSafeLatex()}"
            };
        }

        public override string ToDisplayString()
        {
            string lStr = Left is MathBinaryOpNode ? $"({Left.ToDisplayString()})" : Left.ToDisplayString();
            string rStr = Right is MathBinaryOpNode ? $"({Right.ToDisplayString()})" : Right.ToDisplayString();
            return $"{lStr} {Operator} {rStr}";
        }

        public override double Evaluate(Dictionary<string, double>? variables = null)
        {
            double l = Left.Evaluate(variables);
            double r = Right.Evaluate(variables);
            return Operator switch
            {
                "+" => l + r,
                "-" => l - r,
                "*" => l * r,
                "/" => Math.Abs(r) < 1e-12 ? throw new DivideByZeroException("Division by zero in mathematical expression.") : l / r,
                "^" => Math.Pow(l, r),
                _ => throw new NotSupportedException($"Unsupported binary operator '{Operator}'")
            };
        }

        public override HashSet<string> GetVariables()
        {
            var vars = Left.GetVariables();
            vars.UnionWith(Right.GetVariables());
            return vars;
        }
    }

    public sealed class MathUnaryOpNode : MathAstNode
    {
        public override MathNodeType NodeType => MathNodeType.UnaryOp;
        public string Operator { get; set; } = "-";
        public MathAstNode Operand { get; set; } = default!;

        public MathUnaryOpNode() { }
        public MathUnaryOpNode(string op, MathAstNode operand)
        {
            Operator = op;
            Operand = operand ?? throw new ArgumentNullException(nameof(operand));
        }

        public override string ToSafeLatex() => $"{Operator}{Operand.ToSafeLatex()}";
        public override string ToDisplayString() => $"{Operator}{Operand.ToDisplayString()}";

        public override double Evaluate(Dictionary<string, double>? variables = null)
        {
            double val = Operand.Evaluate(variables);
            return Operator == "-" ? -val : val;
        }

        public override HashSet<string> GetVariables() => Operand.GetVariables();
    }

    public sealed class MathFunctionNode : MathAstNode
    {
        public override MathNodeType NodeType => MathNodeType.Function;
        public string Name { get; set; } = "sqrt";
        public MathAstNode Argument { get; set; } = default!;

        public MathFunctionNode() { }
        public MathFunctionNode(string name, MathAstNode argument)
        {
            Name = name.ToLowerInvariant();
            Argument = argument ?? throw new ArgumentNullException(nameof(argument));
        }

        public override string ToSafeLatex()
        {
            return Name switch
            {
                "sqrt" => $"\\sqrt{{{Argument.ToSafeLatex()}}}",
                "abs" => $"|{Argument.ToSafeLatex()}|",
                "sin" => $"\\sin({Argument.ToSafeLatex()})",
                "cos" => $"\\cos({Argument.ToSafeLatex()})",
                "tan" => $"\\tan({Argument.ToSafeLatex()})",
                "ln" => $"\\ln({Argument.ToSafeLatex()})",
                "log" => $"\\log({Argument.ToSafeLatex()})",
                _ => $"\\operatorname{{{Name}}}({Argument.ToSafeLatex()})"
            };
        }

        public override string ToDisplayString() => $"{Name}({Argument.ToDisplayString()})";

        public override double Evaluate(Dictionary<string, double>? variables = null)
        {
            double arg = Argument.Evaluate(variables);
            return Name switch
            {
                "sqrt" => arg < 0 ? throw new ArgumentException("Square root of negative number in real domain.") : Math.Sqrt(arg),
                "abs" => Math.Abs(arg),
                "sin" => Math.Sin(arg),
                "cos" => Math.Cos(arg),
                "tan" => Math.Tan(arg),
                "ln" or "log" => arg <= 0 ? throw new ArgumentException("Logarithm of non-positive number.") : Math.Log(arg),
                "exp" => Math.Exp(arg),
                _ => throw new NotSupportedException($"Function '{Name}' is not supported.")
            };
        }

        public override HashSet<string> GetVariables() => Argument.GetVariables();
    }

    public sealed class MathEquationNode : MathAstNode
    {
        public override MathNodeType NodeType => MathNodeType.Equation;
        public MathAstNode Left { get; set; } = default!;
        public MathAstNode Right { get; set; } = default!;

        public MathEquationNode() { }
        public MathEquationNode(MathAstNode left, MathAstNode right)
        {
            Left = left ?? throw new ArgumentNullException(nameof(left));
            Right = right ?? throw new ArgumentNullException(nameof(right));
        }

        public override string ToSafeLatex() => $"{Left.ToSafeLatex()} = {Right.ToSafeLatex()}";
        public override string ToDisplayString() => $"{Left.ToDisplayString()} = {Right.ToDisplayString()}";

        public override double Evaluate(Dictionary<string, double>? variables = null)
        {
            // Equation residual: Left - Right
            return Left.Evaluate(variables) - Right.Evaluate(variables);
        }

        public override HashSet<string> GetVariables()
        {
            var vars = Left.GetVariables();
            vars.UnionWith(Right.GetVariables());
            return vars;
        }
    }

    /// <summary>
    /// Safe recursive-descent parser for restricted mathematical expressions and equations.
    /// Rejects any arbitrary executable code, limits recursion depth (32) and node count (256).
    /// </summary>
    public sealed class MathAstParser
    {
        private const int MaxDepth = 32;
        private const int MaxNodes = 256;

        private static readonly HashSet<string> AllowedFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "sqrt", "abs", "sin", "cos", "tan", "ln", "log", "exp"
        };

        private string _source = "";
        private int _pos;
        private int _currentDepth;
        private int _nodeCount;

        public MathAstNode Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Math input cannot be empty.", nameof(input));

            string sanitized = PreprocessOcrAndUnicode(input);

            // Hostile injection check: forbid dangerous keywords before tokenizing
            string lower = sanitized.ToLowerInvariant();
            if (lower.Contains("__import__") || lower.Contains("eval") || lower.Contains("exec") ||
                lower.Contains("system") || lower.Contains("popen") || lower.Contains("import") ||
                lower.Contains("open") || lower.Contains("require") || lower.Contains("script") ||
                lower.Contains(";") || lower.Contains("{") || lower.Contains("}") || lower.Contains("\""))
            {
                throw new InvalidOperationException("Hostile or non-mathematical tokens detected.");
            }

            _source = sanitized;
            _pos = 0;
            _currentDepth = 0;
            _nodeCount = 0;

            var node = ParseEquation();
            SkipWhitespace();
            if (_pos < _source.Length)
            {
                throw new FormatException($"Unexpected token '{_source[_pos]}' at position {_pos}.");
            }

            return node;
        }

        public static string PreprocessOcrAndUnicode(string input)
        {
            var sb = new StringBuilder(input.Trim());

            // Unicode operators to ASCII
            sb.Replace('×', '*');
            sb.Replace('÷', '/');
            sb.Replace('−', '-');
            sb.Replace('–', '-');
            sb.Replace("²", "^2");
            sb.Replace("³", "^3");
            sb.Replace("√", "sqrt");
            sb.Replace("==", "=");

            string s = sb.ToString();

            // OCR artifact normalization: e.g. "2x + 5 = l5" -> "2x + 5 = 15"
            // If lowercase 'l' or uppercase 'I' or 'O' occurs adjacent to digits, convert to 1 or 0
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c == 'l' || c == 'I')
                {
                    bool prevDigit = (i > 0 && char.IsDigit(chars[i - 1]));
                    bool nextDigit = (i < chars.Length - 1 && char.IsDigit(chars[i + 1]));
                    if (prevDigit || nextDigit)
                    {
                        chars[i] = '1';
                    }
                }
            }

            return new string(chars);
        }

        private MathAstNode ParseEquation()
        {
            EnterNode();
            var left = ParseExpression();
            SkipWhitespace();

            if (_pos < _source.Length && _source[_pos] == '=')
            {
                _pos++; // consume '='
                var right = ParseExpression();
                ExitNode();
                return RegisterNode(new MathEquationNode(left, right));
            }

            ExitNode();
            return left;
        }

        private MathAstNode ParseExpression()
        {
            EnterNode();
            var node = ParseTerm();

            while (true)
            {
                SkipWhitespace();
                if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-'))
                {
                    char op = _source[_pos++];
                    var right = ParseTerm();
                    node = RegisterNode(new MathBinaryOpNode(op.ToString(), node, right));
                }
                else
                {
                    break;
                }
            }

            ExitNode();
            return node;
        }

        private MathAstNode ParseTerm()
        {
            EnterNode();
            var node = ParsePower();

            while (true)
            {
                SkipWhitespace();
                if (_pos < _source.Length && (_source[_pos] == '*' || _source[_pos] == '/'))
                {
                    char op = _source[_pos++];
                    var right = ParsePower();
                    node = RegisterNode(new MathBinaryOpNode(op.ToString(), node, right));
                }
                // Implicit multiplication: e.g. "2x", "5(x+1)"
                else if (_pos < _source.Length && (char.IsLetter(_source[_pos]) || _source[_pos] == '('))
                {
                    var right = ParsePower();
                    node = RegisterNode(new MathBinaryOpNode("*", node, right));
                }
                else
                {
                    break;
                }
            }

            ExitNode();
            return node;
        }

        private MathAstNode ParsePower()
        {
            EnterNode();
            var node = ParseUnary();

            SkipWhitespace();
            if (_pos < _source.Length && _source[_pos] == '^')
            {
                _pos++; // consume '^'
                var right = ParseUnary(); // Right-associative or direct power
                node = RegisterNode(new MathBinaryOpNode("^", node, right));
            }

            ExitNode();
            return node;
        }

        private MathAstNode ParseUnary()
        {
            SkipWhitespace();
            if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-'))
            {
                char op = _source[_pos++];
                var operand = ParseUnary();
                return RegisterNode(new MathUnaryOpNode(op.ToString(), operand));
            }
            return ParsePrimary();
        }

        private MathAstNode ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _source.Length)
                throw new FormatException("Unexpected end of mathematical expression.");

            char c = _source[_pos];

            // Parentheses
            if (c == '(')
            {
                _pos++; // consume '('
                var inner = ParseEquation();
                SkipWhitespace();
                if (_pos >= _source.Length || _source[_pos] != ')')
                    throw new FormatException("Missing closing parenthesis ')'.");
                _pos++; // consume ')'
                return inner;
            }

            // Number
            if (char.IsDigit(c) || c == '.')
            {
                return ParseNumber();
            }

            // Identifier: Variable or Function
            if (char.IsLetter(c))
            {
                return ParseIdentifier();
            }

            throw new FormatException($"Invalid character '{c}' in mathematical expression.");
        }

        private MathAstNode ParseNumber()
        {
            int start = _pos;
            bool hasDecimal = false;

            while (_pos < _source.Length && (char.IsDigit(_source[_pos]) || _source[_pos] == '.'))
            {
                if (_source[_pos] == '.')
                {
                    if (hasDecimal) break;
                    hasDecimal = true;
                }
                _pos++;
            }

            string numStr = _source[start.._pos];
            if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                throw new FormatException($"Cannot parse '{numStr}' as a valid number.");
            }

            return RegisterNode(new MathNumberNode(val));
        }

        private MathAstNode ParseIdentifier()
        {
            int start = _pos;
            while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
            {
                _pos++;
            }

            string id = _source[start.._pos];

            // Function call with parentheses e.g. sqrt(16)
            SkipWhitespace();
            if (_pos < _source.Length && _source[_pos] == '(' && AllowedFunctions.Contains(id))
            {
                _pos++; // consume '('
                var arg = ParseExpression();
                SkipWhitespace();
                if (_pos >= _source.Length || _source[_pos] != ')')
                    throw new FormatException($"Missing closing parenthesis ')' for function '{id}'.");
                _pos++; // consume ')'
                return RegisterNode(new MathFunctionNode(id, arg));
            }

            // Single variable or identifier
            return RegisterNode(new MathVariableNode(id));
        }

        private void SkipWhitespace()
        {
            while (_pos < _source.Length && char.IsWhiteSpace(_source[_pos]))
                _pos++;
        }

        private void EnterNode()
        {
            _currentDepth++;
            if (_currentDepth > MaxDepth)
                throw new InvalidOperationException($"Math AST recursion depth exceeded safe limit ({MaxDepth}).");
        }

        private void ExitNode()
        {
            _currentDepth--;
        }

        private T RegisterNode<T>(T node) where T : MathAstNode
        {
            _nodeCount++;
            if (_nodeCount > MaxNodes)
                throw new InvalidOperationException($"Math AST node count exceeded safe limit ({MaxNodes}).");
            return node;
        }

        public static string ToJson(MathAstNode node)
        {
            return JsonSerializer.Serialize(node, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        public static MathAstNode? FromJson(string json)
        {
            return JsonSerializer.Deserialize<MathAstNode>(json);
        }
    }
}
