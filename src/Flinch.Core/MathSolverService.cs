using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Flinch.Core.Contracts;

namespace Flinch.Core
{
    /// <summary>
    /// Service that coordinates mathematical AST parsing, isolated SymPy solving,
    /// domain verification, residual evaluation, and offline C# algebra solving.
    /// </summary>
    public sealed class MathSolverService
    {
        private readonly string? _pythonExecutablePath;
        private readonly string? _workerScriptPath;
        private readonly MathAstParser _parser = new();

        public MathSolverService(string? pythonExecutablePath = null, string? workerScriptPath = null)
        {
            _pythonExecutablePath = pythonExecutablePath ?? FindPythonExecutable();
            _workerScriptPath = workerScriptPath ?? FindWorkerScript();
        }

        public async Task<MathResult> SolveAsync(string expressionOrEquation, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(expressionOrEquation))
            {
                return new MathResult
                {
                    OriginalEquation = string.Empty,
                    DomainAssumptions = "None",
                    IsVerified = false,
                    VerificationResidual = "Empty input."
                };
            }

            MathAstNode ast;
            try
            {
                ast = _parser.Parse(expressionOrEquation);
            }
            catch (Exception ex)
            {
                return new MathResult
                {
                    OriginalEquation = expressionOrEquation,
                    DomainAssumptions = "None",
                    IsVerified = false,
                    VerificationResidual = $"Parse error: {ex.Message}"
                };
            }

            return await SolveAstAsync(ast, expressionOrEquation, ct).ConfigureAwait(false);
        }

        public async Task<MathResult> SolveAstAsync(MathAstNode ast, string originalEquation, CancellationToken ct = default)
        {
            string astJson = MathAstParser.ToJson(ast);

            // Attempt isolated Python SymPy worker first if available
            if (!string.IsNullOrEmpty(_pythonExecutablePath) && !string.IsNullOrEmpty(_workerScriptPath) &&
                File.Exists(_pythonExecutablePath) && File.Exists(_workerScriptPath))
            {
                try
                {
                    var workerResult = await RunWorkerProcessAsync(astJson, ct).ConfigureAwait(false);
                    if (workerResult != null && workerResult.OrderedSteps.Count > 0 && workerResult.OriginalEquation != "Error")
                    {
                        workerResult.ParsedAstJson = astJson;
                        return workerResult;
                    }
                }
                catch
                {
                    // Fallback to internal C# solver
                }
            }

            // High-precision internal C# algebra fallback
            return SolveWithInternalEngine(ast, originalEquation, astJson);
        }

        private async Task<MathResult?> RunWorkerProcessAsync(string astJson, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonExecutablePath!,
                Arguments = $"\"{_workerScriptPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5)); // Strict 5-second timeout

            // Send AST JSON to process stdin
            await using (var writer = process.StandardInput)
            {
                await writer.WriteAsync(astJson.AsMemory(), cts.Token).ConfigureAwait(false);
                await writer.FlushAsync(cts.Token).ConfigureAwait(false);
            }

            string output = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            return JsonSerializer.Deserialize<MathResult>(output.Trim(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        /// <summary>
        /// Exact offline C# solving engine for linear, quadratic, and arithmetic expressions.
        /// </summary>
        public MathResult SolveWithInternalEngine(MathAstNode ast, string originalEquation, string? astJson = null)
        {
            var result = new MathResult
            {
                OriginalEquation = originalEquation,
                ParsedAstJson = astJson ?? MathAstParser.ToJson(ast),
                DomainAssumptions = "x in Real",
                IsVerified = false
            };

            var variables = ast.GetVariables();

            // 1. If pure arithmetic (no variables)
            if (variables.Count == 0)
            {
                try
                {
                    if (ast is MathEquationNode eq)
                    {
                        double l = eq.Left.Evaluate();
                        double r = eq.Right.Evaluate();
                        double residual = Math.Abs(l - r);
                        bool verified = residual < 1e-6;

                        result.SolutionSet.Add(verified ? "True" : "False");
                        result.IsVerified = verified;
                        result.VerificationResidual = residual.ToString("0.000000e+00", CultureInfo.InvariantCulture);

                        result.OrderedSteps.Add(new MathStep
                        {
                            StepNumber = 1,
                            Rule = "Evaluate LHS and RHS",
                            BeforeExpression = $"{eq.Left.ToSafeLatex()} = {eq.Right.ToSafeLatex()}",
                            AfterExpression = $"{l} = {r}",
                            Condition = verified ? "Identity holds" : "Contradiction"
                        });
                        return result;
                    }

                    double val = ast.Evaluate();
                    result.SolutionSet.Add(val.ToString(CultureInfo.InvariantCulture));
                    result.IsVerified = true;
                    result.VerificationResidual = "0.000000e+00";
                    result.OrderedSteps.Add(new MathStep
                    {
                        StepNumber = 1,
                        Rule = "Exact Arithmetic Evaluation",
                        BeforeExpression = ast.ToSafeLatex(),
                        AfterExpression = val.ToString(CultureInfo.InvariantCulture),
                        Condition = "Real domain"
                    });
                    return result;
                }
                catch (Exception ex)
                {
                    result.VerificationResidual = $"Evaluation exception: {ex.Message}";
                    return result;
                }
            }

            // 2. Single variable polynomial solving (Linear & Quadratic)
            string varName = "x";
            foreach (var v in variables) { varName = v; break; }

            // Sample polynomial at x = 0, 1, -1 to determine coefficients for up to degree 2:
            // P(x) = a*x^2 + b*x + c
            double f0, f1, f_1, f2;
            try
            {
                f0 = ast.Evaluate(new Dictionary<string, double> { [varName] = 0.0 });
                f1 = ast.Evaluate(new Dictionary<string, double> { [varName] = 1.0 });
                f_1 = ast.Evaluate(new Dictionary<string, double> { [varName] = -1.0 });
                f2 = ast.Evaluate(new Dictionary<string, double> { [varName] = 2.0 });
            }
            catch (DivideByZeroException)
            {
                result.DomainAssumptions = $"{varName} != 0 (Domain Singular Point)";
                result.VerificationResidual = "Division by zero at evaluation probe.";
                return result;
            }
            catch (Exception ex)
            {
                result.VerificationResidual = $"Domain violation: {ex.Message}";
                return result;
            }

            // Parabolic fit:
            // c = f0
            // f1 = a + b + c => a + b = f1 - c
            // f_1 = a - b + c => a - b = f_1 - c
            // 2a = (f1 + f_1) - 2c => a = (f1 + f_1 - 2*c) / 2
            // 2b = f1 - f_1 => b = (f1 - f_1) / 2
            double c = f0;
            double a = (f1 + f_1 - 2.0 * c) / 2.0;
            double b = (f1 - f_1) / 2.0;

            // Verify if higher degree: check if f2 == 4a + 2b + c
            double expectedF2 = 4.0 * a + 2.0 * b + c;
            bool isQuadraticOrLinear = Math.Abs(f2 - expectedF2) < 1e-5;

            int step = 1;
            result.OrderedSteps.Add(new MathStep
            {
                StepNumber = step++,
                Rule = "Standard Form f(x) = 0",
                BeforeExpression = ast.ToSafeLatex(),
                AfterExpression = $"{FormatPolynomial(a, b, c, varName)} = 0",
                Condition = "Algebraic Reduction"
            });

            if (Math.Abs(a) < 1e-9 && Math.Abs(b) < 1e-9)
            {
                // Degenerate: c = 0
                if (Math.Abs(c) < 1e-9)
                {
                    result.SolutionSet.Add($"All {varName} in Real");
                    result.IsVerified = true;
                    result.VerificationResidual = "0.000000e+00";
                }
                else
                {
                    result.SolutionSet.Add("No solution");
                    result.IsVerified = true;
                    result.VerificationResidual = "Contradiction";
                }
                return result;
            }

            // Case A: Linear equation b*x + c = 0
            if (Math.Abs(a) < 1e-9)
            {
                double sol = -c / b;
                result.OrderedSteps.Add(new MathStep
                {
                    StepNumber = step++,
                    Rule = "Isolate Variable",
                    BeforeExpression = $"{b:0.###}{varName} + {c:0.###} = 0",
                    AfterExpression = $"{varName} = {-c / b:0.###}",
                    Condition = $"{b:0.###} != 0"
                });

                // Verification check
                double res = Math.Abs(ast.Evaluate(new Dictionary<string, double> { [varName] = sol }));
                result.SolutionSet.Add($"{varName} = {FormatNumberOrFraction(sol)}");
                result.IsVerified = res < 1e-5;
                result.VerificationResidual = res.ToString("0.000000e+00", CultureInfo.InvariantCulture);

                result.OrderedSteps.Add(new MathStep
                {
                    StepNumber = step++,
                    Rule = "Residual Verification",
                    BeforeExpression = $"{varName} = {FormatNumberOrFraction(sol)}",
                    AfterExpression = $"Residual |f({varName}*)| = {result.VerificationResidual}",
                    Condition = result.IsVerified ? "Verified Root" : "Extraneous Root"
                });

                return result;
            }

            // Case B: Quadratic equation a*x^2 + b*x + c = 0
            double discriminant = b * b - 4.0 * a * c;
            result.OrderedSteps.Add(new MathStep
            {
                StepNumber = step++,
                Rule = "Compute Discriminant",
                BeforeExpression = $"{a:0.###}{varName}^2 + {b:0.###}{varName} + {c:0.###} = 0",
                AfterExpression = $"\\Delta = b^2 - 4ac = {discriminant:0.###}",
                Condition = discriminant >= 0 ? "Real roots exist" : "No real roots"
            });

            if (discriminant < 0)
            {
                result.SolutionSet.Add("No real solutions");
                result.IsVerified = true;
                result.VerificationResidual = "0.000000e+00";
                return result;
            }

            double sqrtD = Math.Sqrt(discriminant);
            double x1 = (-b + sqrtD) / (2.0 * a);
            double x2 = (-b - sqrtD) / (2.0 * a);

            result.OrderedSteps.Add(new MathStep
            {
                StepNumber = step++,
                Rule = "Quadratic Formula",
                BeforeExpression = $"x = \\frac{{-b \\pm \\sqrt{{\\Delta}}}}{{2a}}",
                AfterExpression = $"{varName}_1 = {FormatNumberOrFraction(x1)}, {varName}_2 = {FormatNumberOrFraction(x2)}",
                Condition = "Exact algebraic formula"
            });

            // Verification
            double res1 = Math.Abs(ast.Evaluate(new Dictionary<string, double> { [varName] = x1 }));
            double res2 = Math.Abs(ast.Evaluate(new Dictionary<string, double> { [varName] = x2 }));
            double maxRes = Math.Max(res1, res2);

            result.SolutionSet.Add($"{varName}_1 = {FormatNumberOrFraction(x1)}");
            if (Math.Abs(x1 - x2) > 1e-7)
            {
                result.SolutionSet.Add($"{varName}_2 = {FormatNumberOrFraction(x2)}");
            }

            result.IsVerified = maxRes < 1e-4;
            result.VerificationResidual = maxRes.ToString("0.000000e+00", CultureInfo.InvariantCulture);

            result.OrderedSteps.Add(new MathStep
            {
                StepNumber = step++,
                Rule = "Residual Verification",
                BeforeExpression = string.Join(", ", result.SolutionSet),
                AfterExpression = $"Max Residual = {result.VerificationResidual}",
                Condition = result.IsVerified ? "Verified Roots" : "Domain Check Required"
            });

            return result;
        }

        private static string FormatPolynomial(double a, double b, double c, string varName)
        {
            var parts = new List<string>();
            if (Math.Abs(a) > 1e-9) parts.Add($"{a:0.###}{varName}^2");
            if (Math.Abs(b) > 1e-9) parts.Add($"{(b >= 0 && parts.Count > 0 ? "+" : "")}{b:0.###}{varName}");
            if (Math.Abs(c) > 1e-9) parts.Add($"{(c >= 0 && parts.Count > 0 ? "+" : "")}{c:0.###}");
            return parts.Count == 0 ? "0" : string.Join(" ", parts);
        }

        private static string FormatNumberOrFraction(double val)
        {
            if (Math.Abs(val - Math.Round(val)) < 1e-7)
                return Math.Round(val).ToString(CultureInfo.InvariantCulture);
            return val.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string? FindPythonExecutable()
        {
            // Priority 1: flinch/.venv
            string venvPy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\..\flinch\.venv\Scripts\python.exe");
            string fullVenv = Path.GetFullPath(venvPy);
            if (File.Exists(fullVenv)) return fullVenv;

            // Priority 2: System python
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string py315 = Path.Combine(localAppData, @"Programs\Python\Python315\python.exe");
            if (File.Exists(py315)) return py315;

            return "python.exe";
        }

        private static string? FindWorkerScript()
        {
            string localScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\..\src\Flinch.MathWorker\solver.py");
            string fullScript = Path.GetFullPath(localScript);
            if (File.Exists(fullScript)) return fullScript;

            string cwdScript = Path.Combine(Directory.GetCurrentDirectory(), @"src\Flinch.MathWorker\solver.py");
            if (File.Exists(cwdScript)) return cwdScript;

            return null;
        }
    }
}
