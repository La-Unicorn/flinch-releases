using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Flinch.Core;
using Flinch.Core.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Flinch.Core.Tests
{
    [TestClass]
    public class MathSolverTests
    {
        private readonly MathAstParser _parser = new();
        private readonly MathSolverService _solver = new();

        [TestMethod]
        public async Task LinearEquation_SolvesAndVerifiesResidual()
        {
            // 2x + 5 = 15 => 2x = 10 => x = 5
            var result = await _solver.SolveAsync("2x + 5 = 15");

            Assert.IsNotNull(result);
            Assert.IsTrue(result.IsVerified, "Equation 2x + 5 = 15 should be verified.");
            Assert.IsTrue(result.SolutionSet.Exists(s => s.Contains("5")), "Solution set must contain 5.");
            Assert.IsTrue(result.OrderedSteps.Count >= 2, "Must produce multiple ordered steps.");

            // Verification residual should be negligible
            Assert.IsNotNull(result.VerificationResidual);
            double residual = double.Parse(result.VerificationResidual, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsTrue(residual < 1e-4, $"Residual must be negligible: {residual}");
        }

        [TestMethod]
        public async Task QuadraticEquation_SolvesTwoRootsAndVerifies()
        {
            // x^2 - 5x + 6 = 0 => (x - 2)(x - 3) = 0 => x = 2, 3
            var result = await _solver.SolveAsync("x^2 - 5x + 6 = 0");

            Assert.IsNotNull(result);
            Assert.IsTrue(result.IsVerified, "Quadratic roots must be verified.");
            Assert.IsTrue(result.SolutionSet.Count >= 2, "Must have two roots.");
            Assert.IsTrue(result.SolutionSet.Exists(s => s.Contains("2")), "Root 2 must be present.");
            Assert.IsTrue(result.SolutionSet.Exists(s => s.Contains("3")), "Root 3 must be present.");
        }

        [TestMethod]
        public async Task ExactArithmetic_EvaluatesWithoutVariables()
        {
            // sqrt(16) + 3 * 4 = 4 + 12 = 16
            var result = await _solver.SolveAsync("sqrt(16) + 3 * 4");

            Assert.IsNotNull(result);
            Assert.IsTrue(result.IsVerified);
            Assert.AreEqual("16", result.SolutionSet[0]);
            Assert.AreEqual("0.000000e+00", result.VerificationResidual);
        }

        [TestMethod]
        public async Task DomainAnomaly_DivisionByZero_HandledSafely()
        {
            // 1 / x = 0 at x = 0 is a domain singularity
            var result = await _solver.SolveAsync("1 / x = 0");

            Assert.IsNotNull(result);
            Assert.IsFalse(result.IsVerified, "1/x = 0 cannot have verified real solutions.");
            Assert.IsNotNull(result.VerificationResidual);
        }

        [TestMethod]
        public void RestrictedAst_RejectsHostileCodeInjection()
        {
            string[] maliciousInputs = new[]
            {
                "__import__('os').system('calc')",
                "eval(\"2 + 2\")",
                "exec(\"import sys\")",
                "System.IO.File.Delete(\"test.txt\")",
                "2 + 2; rm -rf /",
                "function(){ return 42; }()"
            };

            foreach (var input in maliciousInputs)
            {
                bool threw = false;
                try
                {
                    _parser.Parse(input);
                }
                catch (Exception)
                {
                    threw = true;
                }

                Assert.IsTrue(threw, $"Malicious input '{input}' must be rejected by AST parser.");
            }
        }

        [TestMethod]
        public void RestrictedAst_MaxDepthLimit_PreventsRecursionBombs()
        {
            // Create a deeply nested expression with depth > 32
            string nested = new string('(', 40) + "1" + new string(')', 40);

            Assert.Throws<InvalidOperationException>(() =>
            {
                _parser.Parse(nested);
            });
        }

        [TestMethod]
        public void RestrictedAst_MaxNodeLimit_PreventsDos()
        {
            // Create an expression with over 256 nodes: "1 + 1 + 1 + ... + 1"
            string large = string.Join(" + ", new string[300]);
            large = large.Replace("  ", " 1 ");

            Assert.Throws<InvalidOperationException>(() =>
            {
                _parser.Parse(large);
            });
        }

        [TestMethod]
        public void OcrNormalization_CleansOcrMisreadsAndUnicodeOperators()
        {
            // 1. OCR misread 'l' adjacent to digit: "2x + 5 = l5" -> "2x + 5 = 15"
            var ast1 = _parser.Parse("2x + 5 = l5");
            Assert.AreEqual("2 \\cdot x + 5 = 15", ast1.ToSafeLatex());

            // 2. Unicode symbols: '×', '−', '²'
            var ast2 = _parser.Parse("x² − 9 = 0");
            Assert.AreEqual("{x}^{2} - 9 = 0", ast2.ToSafeLatex());
        }

        [TestMethod]
        public void SafeLatexFormatting_ProducesValidTeX()
        {
            var ast = _parser.Parse("(x + 1) / (x - 1)");
            string latex = ast.ToSafeLatex();

            Assert.AreEqual("\\frac{x + 1}{x - 1}", latex);
        }

        [TestMethod]
        public async Task MathSolverService_SolvesAndEmitsValidJsonContract()
        {
            var result = await _solver.SolveAsync("3x - 9 = 0");

            Assert.IsNotNull(result);
            Assert.IsTrue(result.IsVerified);
            Assert.IsTrue(result.SolutionSet.Exists(s => s.Contains("3")));

            // Contract JSON Serialization verification
            string json = JsonSerializer.Serialize(result);
            Assert.IsFalse(string.IsNullOrWhiteSpace(json));

            var roundTripped = JsonSerializer.Deserialize<MathResult>(json);
            Assert.IsNotNull(roundTripped);
            Assert.AreEqual(result.IsVerified, roundTripped.IsVerified);
            Assert.AreEqual(result.SolutionSet.Count, roundTripped.SolutionSet.Count);
        }
    }
}
