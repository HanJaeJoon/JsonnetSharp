// Ports go-jsonnet internal/program/static_analyzer.go.
//
// Go threads the first error through analysisState; throwing StaticError stops at the same
// first error in the same visiting order.

using JsonnetSharp.Ast;

namespace JsonnetSharp
{
    internal static partial class Program
    {
        private static void VisitNext(Node a, bool inObject, IdentifierSet vars, IdentifierSet freeVars)
        {
            AnalyzeVisit(a, inObject, vars);
            freeVars.AddIdentifiers(a.FreeVars);
        }

        private static IdentifierSet EnterLocal(List<LocalBind> binds, IdentifierSet vars, bool inObject, IdentifierSet freeVars)
        {
            IdentifierSet newVars = vars.Clone();
            foreach (LocalBind bind in binds)
            {
                newVars.Add(bind.Variable);
            }
            // Binds in local can be mutually or even self recursive
            foreach (LocalBind bind in binds)
            {
                VisitNext(bind.Body, inObject, newVars, freeVars);
            }
            return newVars;
        }

        private static void AnalyzeVisit(Node a, bool inObject, IdentifierSet vars)
        {
            StackGuard.Ensure(a.LocRange);
            IdentifierSet s = new();

            switch (a)
            {
                case Apply n:
                    VisitNext(n.Target, inObject, vars, s);
                    foreach (CommaSeparatedExpr arg in n.Arguments.Positional)
                    {
                        VisitNext(arg.Expr, inObject, vars, s);
                    }
                    foreach (NamedArgument arg in n.Arguments.Named)
                    {
                        VisitNext(arg.Arg, inObject, vars, s);
                    }
                    break;
                case Ast.Array n:
                    foreach (CommaSeparatedExpr elem in n.Elements)
                    {
                        VisitNext(elem.Expr, inObject, vars, s);
                    }
                    break;
                case Binary n:
                    VisitNext(n.Left, inObject, vars, s);
                    VisitNext(n.Right, inObject, vars, s);
                    break;
                case Conditional n:
                    VisitNext(n.Cond, inObject, vars, s);
                    VisitNext(n.BranchTrue, inObject, vars, s);
                    VisitNext(n.BranchFalse!, inObject, vars, s);
                    break;
                case Ast.Error n:
                    VisitNext(n.Expr, inObject, vars, s);
                    break;
                case Function n:
                    {
                        IdentifierSet newVars = vars.Clone();
                        foreach (Parameter param in n.Parameters)
                        {
                            newVars.Add(param.Name);
                        }
                        foreach (Parameter param in n.Parameters)
                        {
                            if (param.DefaultArg != null)
                            {
                                VisitNext(param.DefaultArg, inObject, newVars, s);
                            }
                        }
                        VisitNext(n.Body, inObject, newVars, s);
                        // Parameters are free inside the body, but not visible here or outside
                        foreach (Parameter param in n.Parameters)
                        {
                            s.Remove(param.Name);
                        }
                        break;
                    }
                case Import or ImportStr or ImportBin:
                    // nothing to do here
                    break;
                case InSuper n:
                    if (!inObject)
                    {
                        throw new StaticError("Can't use super outside of an object.", n.LocRange);
                    }
                    VisitNext(n.Index, inObject, vars, s);
                    break;
                case SuperIndex n:
                    if (!inObject)
                    {
                        throw new StaticError("Can't use super outside of an object.", n.LocRange);
                    }
                    VisitNext(n.Index!, inObject, vars, s);
                    break;
                case Ast.Index n:
                    VisitNext(n.Target, inObject, vars, s);
                    VisitNext(n.IndexExpr!, inObject, vars, s);
                    break;
                case Local n:
                    {
                        IdentifierSet newVars = EnterLocal(n.Binds, vars, inObject, s);
                        VisitNext(n.Body, inObject, newVars, s);

                        // Any usage of newly created variables inside are considered free
                        // but they are not here or outside
                        foreach (LocalBind bind in n.Binds)
                        {
                            s.Remove(bind.Variable);
                        }
                        break;
                    }
                case LiteralBoolean or LiteralNull or LiteralNumber or LiteralString:
                    // nothing to do here
                    break;
                case DesugaredObject n:
                    {
                        IdentifierSet newVars = EnterLocal(n.Locals, vars, true, s);
                        foreach (DesugaredObjectField field in n.Fields)
                        {
                            VisitNext(field.Body, true, newVars, s);
                        }
                        foreach (Node assert in n.Asserts)
                        {
                            VisitNext(assert, true, newVars, s);
                        }

                        // Object local vars are not free outside of object
                        foreach (LocalBind bind in n.Locals)
                        {
                            s.Remove(bind.Variable);
                        }

                        // Field names are calculated *outside* of the object
                        foreach (DesugaredObjectField field in n.Fields)
                        {
                            VisitNext(field.Name, inObject, vars, s);
                        }
                        break;
                    }
                case Self n:
                    if (!inObject)
                    {
                        throw new StaticError("Can't use self outside of an object.", n.LocRange);
                    }
                    break;
                case Unary n:
                    VisitNext(n.Expr, inObject, vars, s);
                    break;
                case Var n:
                    if (!vars.Contains(n.Id))
                    {
                        throw new StaticError("Unknown variable: " + n.Id, n.LocRange);
                    }
                    s.Add(n.Id);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected node " + a.GetType().Name);
            }
            a.FreeVars = s.ToOrderedSlice();
        }

        // analyze checks variable references (these could be checked statically in Jsonnet).
        // It enriches the AST with additional information about free variables in every node,
        // so it is necessary to always run it before executing the AST.
        internal static void Analyze(Node node) => AnalyzeVisit(node, false, new IdentifierSet("std", "$std"));
    }
}
