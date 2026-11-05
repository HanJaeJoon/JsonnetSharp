// Ports go-jsonnet ast/clone.go.

using System.Diagnostics.CodeAnalysis;

namespace JsonnetSharp.Ast
{
    internal static class AstCloner
    {
        // Clone creates an independent copy of an AST (Go: ast.Clone).
        [return: NotNullIfNotNull(nameof(node))]
        public static Node? Clone(Node? node)
        {
            if (node == null)
            {
                return null;
            }
            StackGuard.Ensure(node.LocRange);

            Node r = node.ShallowCopy();
            switch (r)
            {
                case Apply n:
                    n.Target = Clone(n.Target);
                    n.Arguments = new Arguments
                    {
                        Positional = CloneCommaSeparatedExprs(n.Arguments.Positional),
                        Named = n.Arguments.Named.ConvertAll(a => new NamedArgument
                        {
                            NameFodder = a.NameFodder,
                            Name = a.Name,
                            EqFodder = a.EqFodder,
                            Arg = Clone(a.Arg),
                            CommaFodder = a.CommaFodder,
                        }),
                    };
                    break;

                case ApplyBrace n:
                    n.Left = Clone(n.Left);
                    n.Right = Clone(n.Right);
                    break;

                case Array n:
                    n.Elements = CloneCommaSeparatedExprs(n.Elements);
                    break;

                case ArrayComp n:
                    n.Body = Clone(n.Body);
                    n.Spec = CloneForSpec(n.Spec);
                    break;

                case Assert n:
                    n.Cond = Clone(n.Cond);
                    n.Message = Clone(n.Message);
                    n.Rest = Clone(n.Rest);
                    break;

                case Binary n:
                    n.Left = Clone(n.Left);
                    n.Right = Clone(n.Right);
                    break;

                case Conditional n:
                    n.Cond = Clone(n.Cond);
                    n.BranchTrue = Clone(n.BranchTrue);
                    n.BranchFalse = Clone(n.BranchFalse);
                    break;

                case Dollar:
                    break;

                case Error n:
                    n.Expr = Clone(n.Expr);
                    break;

                case Function n:
                    n.Parameters = n.Parameters.ConvertAll(p =>
                    {
                        Parameter c = p.ShallowCopy();
                        c.DefaultArg = Clone(c.DefaultArg);
                        return c;
                    });
                    n.Body = Clone(n.Body);
                    break;

                case Import n:
                    n.File = (LiteralString)n.File.ShallowCopy();
                    break;

                case ImportStr n:
                    n.File = (LiteralString)n.File.ShallowCopy();
                    break;

                case ImportBin n:
                    n.File = (LiteralString)n.File.ShallowCopy();
                    break;

                case Index n:
                    n.Target = Clone(n.Target);
                    n.IndexExpr = Clone(n.IndexExpr);
                    break;

                case Slice n:
                    n.Target = Clone(n.Target);
                    n.BeginIndex = Clone(n.BeginIndex);
                    n.EndIndex = Clone(n.EndIndex);
                    n.Step = Clone(n.Step);
                    break;

                case Local n:
                    n.Binds = n.Binds.ConvertAll(b =>
                    {
                        LocalBind c = b.ShallowCopy();
                        if (c.Fun != null)
                        {
                            c.Fun = (Function)Clone(c.Fun);
                        }
                        c.Body = Clone(c.Body);
                        return c;
                    });
                    n.Body = Clone(n.Body);
                    break;

                case LiteralBoolean:
                case LiteralNull:
                case LiteralNumber:
                case LiteralString:
                    break;

                case Object n:
                    n.Fields = n.Fields.ConvertAll(CloneField);
                    break;

                case DesugaredObject n:
                    // Like Go, Asserts and Locals are shared with the original.
                    n.Fields = n.Fields.ConvertAll(f =>
                    {
                        DesugaredObjectField c = f.ShallowCopy();
                        c.Name = Clone(c.Name);
                        c.Body = Clone(c.Body);
                        return c;
                    });
                    break;

                case ObjectComp n:
                    n.Fields = n.Fields.ConvertAll(CloneField);
                    n.Spec = CloneForSpec(n.Spec);
                    break;

                case Parens n:
                    n.Inner = Clone(n.Inner);
                    break;

                case Self:
                    break;

                case SuperIndex n:
                    n.Index = Clone(n.Index);
                    break;

                case InSuper n:
                    n.Index = Clone(n.Index);
                    break;

                case Unary n:
                    n.Expr = Clone(n.Expr);
                    break;

                case Var:
                    break;

                default:
                    throw new InvalidOperationException("ast.Clone() does not recognize ast: " + node.GetType().Name);
            }

            // cloneNodeBase: the context string is immutable, the free variables get a fresh slice.
            r.FreeVars = (string[])r.FreeVars.Clone();
            return r;
        }

        private static ForSpec CloneForSpec(ForSpec spec)
        {
            ForSpec r = spec.ShallowCopy();
            r.Expr = Clone(r.Expr);
            if (r.Outer != null)
            {
                r.Outer = CloneForSpec(r.Outer);
            }
            r.Conditions = r.Conditions.ConvertAll(c => new IfSpec { Expr = Clone(c.Expr), IfFodder = c.IfFodder });
            return r;
        }

        private static ObjectField CloneField(ObjectField field)
        {
            ObjectField r = field.ShallowCopy();
            if (r.Method != null)
            {
                r.Method = (Function)Clone(r.Method);
            }
            r.Expr1 = Clone(r.Expr1);
            r.Expr2 = Clone(r.Expr2);
            r.Expr3 = Clone(r.Expr3);
            return r;
        }

        private static List<CommaSeparatedExpr> CloneCommaSeparatedExprs(List<CommaSeparatedExpr> list) =>
            list.ConvertAll(e => new CommaSeparatedExpr { Expr = Clone(e.Expr), CommaFodder = e.CommaFodder });
    }
}
