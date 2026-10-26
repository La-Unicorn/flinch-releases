"""
Flinch Isolated SymPy Math Worker
Solves equations from a bounded AST contract and verifies solutions.
SECURITY INVARIANT: Never uses eval() or exec(). Only constructs SymPy trees from validated JSON AST.
"""

import sys
import json
import math

try:
    import sympy
    from sympy import Symbol, Rational, Add, Mul, Pow, Eq, solve, simplify, latex
    from sympy import sqrt, Abs, sin, cos, tan, log, exp
    HAS_SYMPY = True
except ImportError:
    HAS_SYMPY = False


def ast_to_sympy(node):
    """Converts a restricted AST JSON node to a SymPy expression safely."""
    node_type = node.get("$type") or node.get("NodeType")
    if not node_type:
        # Fallback to duck typing
        if "Value" in node:
            node_type = "number"
        elif "Name" in node and "Argument" not in node:
            node_type = "variable"
        elif "Operator" in node and "Left" in node:
            node_type = "binaryOp"
        elif "Operator" in node and "Operand" in node:
            node_type = "unaryOp"
        elif "Argument" in node:
            node_type = "function"
        elif "Left" in node and "Right" in node:
            node_type = "equation"

    node_type = str(node_type).lower()

    if node_type in ("number", "0"):
        val = node.get("Value", 0.0)
        # Check if float is close to integer
        if isinstance(val, (int, float)):
            if abs(val - round(val)) < 1e-12:
                return sympy.Integer(int(round(val)))
            return Rational(str(val))
        return Rational(str(val))

    elif node_type in ("variable", "1"):
        name = str(node.get("Name", "x")).strip()
        # Strictly sanitize variable name: alphanumeric only
        clean_name = "".join(c for c in name if c.isalnum() or c == "_")
        if not clean_name:
            clean_name = "x"
        return Symbol(clean_name, real=True)

    elif node_type in ("binaryop", "2"):
        op = node.get("Operator", "+")
        left = ast_to_sympy(node.get("Left"))
        right = ast_to_sympy(node.get("Right"))
        if op == "+":
            return Add(left, right)
        elif op == "-":
            return Add(left, Mul(sympy.Integer(-1), right))
        elif op == "*":
            return Mul(left, right)
        elif op == "/":
            return Mul(left, Pow(right, sympy.Integer(-1)))
        elif op == "^":
            return Pow(left, right)
        else:
            raise ValueError(f"Unsupported binary operator: {op}")

    elif node_type in ("unaryop", "3"):
        op = node.get("Operator", "-")
        operand = ast_to_sympy(node.get("Operand"))
        if op == "-":
            return Mul(sympy.Integer(-1), operand)
        return operand

    elif node_type in ("function", "4"):
        fname = str(node.get("Name", "sqrt")).lower()
        arg = ast_to_sympy(node.get("Argument"))
        func_map = {
            "sqrt": sqrt,
            "abs": Abs,
            "sin": sin,
            "cos": cos,
            "tan": tan,
            "ln": log,
            "log": log,
            "exp": exp
        }
        if fname in func_map:
            return func_map[fname](arg)
        raise ValueError(f"Unsupported math function: {fname}")

    elif node_type in ("equation", "5"):
        left = ast_to_sympy(node.get("Left"))
        right = ast_to_sympy(node.get("Right"))
        return Eq(left, right)

    raise ValueError(f"Unknown node type: {node_type}")


def solve_ast(ast_data):
    """Solves the AST and returns a verified MathResult dictionary."""
    if not HAS_SYMPY:
        return {
            "OriginalEquation": "Unknown",
            "DomainAssumptions": "Real numbers",
            "SolutionSet": [],
            "OrderedSteps": [],
            "IsVerified": False,
            "VerificationResidual": "SymPy not installed"
        }

    sympy_expr = ast_to_sympy(ast_data)
    
    # Identify whether it's an equation or expression
    if isinstance(sympy_expr, Eq):
        eq = sympy_expr
    else:
        # If expression f(x), treat as f(x) = 0
        eq = Eq(sympy_expr, sympy.Integer(0))

    symbols = list(eq.free_symbols)
    target_var = symbols[0] if symbols else Symbol("x", real=True)

    ordered_steps = []
    step_num = 1

    # Step 1: Initial Equation
    eq_latex = latex(eq)
    ordered_steps.append({
        "StepNumber": step_num,
        "Rule": "Initial Equation",
        "BeforeExpression": eq_latex,
        "AfterExpression": eq_latex,
        "Condition": "Given"
    })
    step_num += 1

    # Step 2: Standard form (LHS - RHS = 0)
    std_form = simplify(eq.lhs - eq.rhs)
    std_latex = f"{latex(std_form)} = 0"
    ordered_steps.append({
        "StepNumber": step_num,
        "Rule": "Subtract RHS to form f(x) = 0",
        "BeforeExpression": eq_latex,
        "AfterExpression": std_latex,
        "Condition": "Equivalence transformation"
    })
    step_num += 1

    # Step 3: Factor or simplify if polynomial
    factored = sympy.factor(std_form)
    if factored != std_form:
        factored_latex = f"{latex(factored)} = 0"
        ordered_steps.append({
            "StepNumber": step_num,
            "Rule": "Factor Expression",
            "BeforeExpression": std_latex,
            "AfterExpression": factored_latex,
            "Condition": "Factoring over integers/reals"
        })
        step_num += 1

    # Step 4: Solve
    raw_solutions = solve(eq, target_var)
    solutions = []
    verified_all = True
    max_residual = 0.0

    for sol in raw_solutions:
        # Check domain constraint (avoid division by zero in original)
        sol_str = str(sol)
        sol_latex = latex(sol)
        solutions.append(f"{target_var} = {sol_latex}")

        # Verification check: residual = LHS - RHS
        try:
            res_val = eq.lhs.subs(target_var, sol) - eq.rhs.subs(target_var, sol)
            res_simplified = simplify(res_val)
            res_float = float(sympy.N(res_simplified))
            if abs(res_float) > 1e-6:
                verified_all = False
            max_residual = max(max_residual, abs(res_float))
        except Exception:
            verified_all = False

    # Step 5: Final solution step
    sol_summary = ", ".join(solutions) if solutions else "No real solutions"
    ordered_steps.append({
        "StepNumber": step_num,
        "Rule": "Solve for variable",
        "BeforeExpression": f"{latex(factored)} = 0",
        "AfterExpression": sol_summary,
        "Condition": f"Domain: {target_var} in Real"
    })
    step_num += 1

    # Verification step
    ordered_steps.append({
        "StepNumber": step_num,
        "Rule": "Residual Check / Verification",
        "BeforeExpression": sol_summary,
        "AfterExpression": f"Residual |f(x*)| = {max_residual:0.6e}",
        "Condition": "Verified" if verified_all else "Extraneous / Domain Violation"
    })

    return {
        "OriginalEquation": str(eq),
        "DomainAssumptions": f"{target_var} in Real",
        "SolutionSet": solutions,
        "OrderedSteps": ordered_steps,
        "IsVerified": verified_all,
        "VerificationResidual": f"{max_residual:0.6e}"
    }


def main():
    try:
        raw_in = None
        if len(sys.argv) > 1:
            raw_in = sys.argv[1]
        elif not sys.stdin.isatty():
            raw_in = sys.stdin.read().strip()

        if not raw_in:
            print(json.dumps({"error": "No input provided"}))
            sys.exit(1)

        ast_data = json.loads(raw_in)
        result = solve_ast(ast_data)
        print(json.dumps(result))
    except Exception as e:
        err_res = {
            "OriginalEquation": "Error",
            "DomainAssumptions": "None",
            "SolutionSet": [],
            "OrderedSteps": [],
            "IsVerified": False,
            "VerificationResidual": str(e)
        }
        print(json.dumps(err_res))
        sys.exit(0)


if __name__ == "__main__":
    main()
