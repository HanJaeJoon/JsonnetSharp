// Ports go-jsonnet internal/program/desugarer.go.
//
// Go's desugar(astPtr *ast.Node) rewrites through a pointer; here Desugar returns the
// (possibly replaced) node and callers store it back.

using System.Diagnostics.CodeAnalysis;
using JsonnetSharp.Ast;

namespace JsonnetSharp
{
    internal static partial class Program
    {
        private static LiteralString MakeStr(string s) => new() { Value = s, Kind = LiteralStringKind.StringDouble, BlockIndent = "" };

        private static DesugaredObject DesugarFields(Node nodeBase, List<ObjectField> fields, int objLevel)
        {
            foreach (ObjectField field in fields)
            {
                if (field.Method == null)
                {
                    continue;
                }
                field.Expr2 = field.Method;
                field.Method = null;
                // Body of the function already desugared through expr2
            }

            List<Node> asserts = new();
            List<LocalBind> locals = new();
            List<DesugaredObjectField> desugaredFields = new();

            foreach (ObjectField field in fields)
            {
                switch (field.Kind)
                {
                    case ObjectFieldKind.ObjectAssert:
                        {
                            Node msg = field.Expr3 ?? BuildLiteralString("Object assertion failed.");
                            Ast.Error onFailure = new() { LocRange = field.LocRange, Expr = msg };
                            asserts.Add(new Conditional
                            {
                                LocRange = field.LocRange,
                                Cond = field.Expr2,
                                BranchTrue = new LiteralBoolean { Value = true }, // ignored anyway
                                BranchFalse = onFailure,
                            });
                            break;
                        }
                    case ObjectFieldKind.ObjectFieldID:
                        desugaredFields.Add(new DesugaredObjectField
                        {
                            Hide = field.Hide,
                            Name = MakeStr(field.Id!),
                            Body = field.Expr2,
                            PlusSuper = field.SuperSugar,
                            LocRange = field.LocRange,
                        });
                        break;

                    case ObjectFieldKind.ObjectFieldExpr:
                    case ObjectFieldKind.ObjectFieldStr:
                        desugaredFields.Add(new DesugaredObjectField
                        {
                            Hide = field.Hide,
                            Name = field.Expr1!,
                            Body = field.Expr2,
                            PlusSuper = field.SuperSugar,
                            LocRange = field.LocRange,
                        });
                        break;

                    case ObjectFieldKind.ObjectLocal:
                        locals.Add(new LocalBind
                        {
                            Variable = field.Id!,
                            Body = AstCloner.Clone(field.Expr2), // TODO(sbarzowski) not sure if clone is needed
                            LocRange = field.LocRange,
                        });
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected object field kind " + (int)field.Kind);
                }
            }

            // Hidden variable to allow $ binding.
            if (objLevel == 0)
            {
                locals.Add(new LocalBind { Variable = "$", Body = new Self() });
            }

            // Desugar stuff inside
            for (int i = 0; i < asserts.Count; i++)
            {
                asserts[i] = Desugar(asserts[i], objLevel + 1);
            }
            DesugarLocalBinds(locals, objLevel + 1);
            foreach (DesugaredObjectField field in desugaredFields)
            {
                if (field.Name != null)
                {
                    field.Name = Desugar(field.Name, objLevel);
                }
                field.Body = Desugar(field.Body, objLevel + 1);
            }

            DesugaredObject obj = new()
            {
                Asserts = asserts,
                Locals = locals,
                Fields = desugaredFields,
            };
            obj.SetNodeBase(nodeBase);
            return obj;
        }

        private static Node SimpleLambda(Node body, string paramName) => new Function
        {
            Body = body,
            Parameters = new List<Parameter> { new() { Name = paramName } },
        };

        private static Node BuildAnd(Node left, Node right) => new Binary { Op = BinaryOp.BopAnd, Left = left, Right = right };

        // inside is assumed to be already desugared (and cannot be desugared again)
        private static Node DesugarForSpec(Node inside, LocationRange loc, ForSpec forSpec, int objLevel)
        {
            Node body;
            if (forSpec.Conditions.Count > 0)
            {
                Node cond = forSpec.Conditions[0].Expr;
                for (int i = 1; i < forSpec.Conditions.Count; i++)
                {
                    cond = BuildAnd(cond, forSpec.Conditions[i].Expr);
                }
                cond = Desugar(cond, objLevel);
                body = new Conditional
                {
                    Cond = cond,
                    BranchTrue = inside,
                    BranchFalse = new Ast.Array(),
                };
            }
            else
            {
                body = inside;
            }
            Node function = SimpleLambda(body, forSpec.VarName);
            forSpec.Expr = Desugar(forSpec.Expr, objLevel);
            Node current = BuildStdCall("$flatMapArray", loc, function, forSpec.Expr);
            if (forSpec.Outer == null)
            {
                return current;
            }
            return DesugarForSpec(current, loc, forSpec.Outer, objLevel);
        }

        private static Node WrapInArray(Node inside) => new Ast.Array
        {
            Elements = new List<CommaSeparatedExpr> { new() { Expr = inside } },
        };

        private static Node DesugarArrayComp(ArrayComp comp, int objLevel)
        {
            comp.Body = Desugar(comp.Body, objLevel);
            return DesugarForSpec(WrapInArray(comp.Body), comp.LocRange, comp.Spec, objLevel);
        }

        private static Node DesugarObjectComp(ObjectComp comp, int objLevel)
        {
            DesugaredObject obj = DesugarFields(comp, comp.Fields, objLevel);

            // Magic merging which follows doesn't support object locals, so we need
            // to desugar them completely, i.e. put them inside the fields. The locals
            // can be different for each field in a comprehension (unlike locals in
            // "normal" objects which have a fixed value), so it's not even too wasteful.
            if (obj.Locals.Count > 0)
            {
                DesugaredObjectField field = obj.Fields[0];
                field.Body = new Local
                {
                    Body = field.Body,
                    Binds = obj.Locals,
                };
                obj.Locals = new List<LocalBind>();
            }

            if (obj.Fields.Count != 1)
            {
                throw new InvalidOperationException("Wrong number of fields in object comprehension, it should have been caught during parsing");
            }

            Node desugaredArrayComp = DesugarForSpec(WrapInArray(obj), comp.LocRange, comp.Spec, objLevel);

            return BuildStdCall("$objectFlatMerge", comp.LocRange, desugaredArrayComp);
        }

        private static Node BuildLiteralString(string value) => new LiteralString
        {
            Kind = LiteralStringKind.StringDouble,
            Value = value,
        };

        private static Node BuildSimpleIndex(Node obj, string member) => new Ast.Index
        {
            Target = obj,
            IndexExpr = BuildLiteralString(member),
        };

        private static Node BuildStdCall(string builtinName, LocationRange loc, params Node[] args)
        {
            Var std = new() { Id = "$std" };
            Node builtin = BuildSimpleIndex(std, builtinName);
            List<CommaSeparatedExpr> positional = new(args.Length);
            foreach (Node arg in args)
            {
                positional.Add(new CommaSeparatedExpr { Expr = arg });
            }
            return new Apply
            {
                LocRange = loc,
                Target = builtin,
                Arguments = new Arguments { Positional = positional },
            };
        }

        private static void DesugarLocalBinds(List<LocalBind> binds, int objLevel)
        {
            for (int i = 0; i < binds.Count; i++)
            {
                if (binds[i].Fun != null)
                {
                    binds[i] = new LocalBind
                    {
                        Variable = binds[i].Variable,
                        Body = binds[i].Fun!,
                        Fun = null,
                    };
                }
                binds[i].Body = Desugar(binds[i].Body, objLevel);
            }
        }

        [return: NotNullIfNotNull(nameof(node))]
        private static Node? Desugar(Node? node, int objLevel)
        {
            if (node == null)
            {
                return null;
            }
            StackGuard.Ensure(node.LocRange);

            switch (node)
            {
                case Apply n:
                    n.Target = Desugar(n.Target, objLevel);
                    foreach (CommaSeparatedExpr arg in n.Arguments.Positional)
                    {
                        arg.Expr = Desugar(arg.Expr, objLevel);
                    }
                    foreach (NamedArgument arg in n.Arguments.Named)
                    {
                        arg.Arg = Desugar(arg.Arg, objLevel);
                    }
                    return n;

                case ApplyBrace n:
                    {
                        n.Left = Desugar(n.Left, objLevel);
                        n.Right = Desugar(n.Right, objLevel);
                        Binary b = new() { Left = n.Left, Op = BinaryOp.BopPlus, Right = n.Right };
                        b.SetNodeBase(n);
                        return b;
                    }

                case Ast.Array n:
                    foreach (CommaSeparatedExpr element in n.Elements)
                    {
                        element.Expr = Desugar(element.Expr, objLevel);
                    }
                    return n;

                case ArrayComp n:
                    return DesugarArrayComp(n, objLevel);

                case Assert n:
                    {
                        n.Message ??= BuildLiteralString("Assertion failed");
                        Node cond = new Conditional
                        {
                            Cond = n.Cond,
                            BranchTrue = n.Rest,
                            BranchFalse = new Ast.Error { LocRange = n.LocRange, Expr = n.Message },
                        };
                        return Desugar(cond, objLevel);
                    }

                case Binary n:
                    // some operators get replaced by stdlib functions
                    if (n.Op == BinaryOp.BopIn)
                    {
                        // reversed order of arguments
                        return Desugar(BuildStdCall("objectHasAll", n.LocRange, n.Right, n.Left), objLevel);
                    }
                    if (n.Op == BinaryOp.BopPercent)
                    {
                        return Desugar(BuildStdCall("mod", n.LocRange, n.Left, n.Right), objLevel);
                    }
                    n.Left = Desugar(n.Left, objLevel);
                    n.Right = Desugar(n.Right, objLevel);
                    return n;

                case Conditional n:
                    n.Cond = Desugar(n.Cond, objLevel);
                    n.BranchTrue = Desugar(n.BranchTrue, objLevel);
                    n.BranchFalse ??= new LiteralNull();
                    n.BranchFalse = Desugar(n.BranchFalse, objLevel);
                    return n;

                case Dollar n:
                    {
                        if (objLevel == 0)
                        {
                            throw new StaticError("No top-level object found.", n.LocRange);
                        }
                        Var v = new() { Id = "$" };
                        v.SetNodeBase(n);
                        return v;
                    }

                case Ast.Error n:
                    n.Expr = Desugar(n.Expr, objLevel);
                    return n;

                case Function n:
                    foreach (Parameter param in n.Parameters)
                    {
                        if (param.DefaultArg != null)
                        {
                            param.DefaultArg = Desugar(param.DefaultArg, objLevel);
                        }
                    }
                    n.Body = Desugar(n.Body, objLevel);
                    return n;

                case Import n:
                    // Desugaring a LiteralString never replaces it.
                    Desugar(n.File, objLevel);
                    return n;

                case ImportStr n:
                    Desugar(n.File, objLevel);
                    return n;

                case ImportBin n:
                    Desugar(n.File, objLevel);
                    return n;

                case Ast.Index n:
                    n.Target = Desugar(n.Target, objLevel);
                    if (n.Id != null)
                    {
                        if (n.IndexExpr != null)
                        {
                            throw new InvalidOperationException("Node with both Id and Index");
                        }
                        n.IndexExpr = MakeStr(n.Id);
                        n.Id = null;
                    }
                    n.IndexExpr = Desugar(n.IndexExpr, objLevel);
                    return n;

                case Slice n:
                    n.BeginIndex ??= new LiteralNull();
                    n.EndIndex ??= new LiteralNull();
                    n.Step ??= new LiteralNull();
                    return Desugar(BuildStdCall("slice", n.LocRange, n.Target, n.BeginIndex, n.EndIndex, n.Step), objLevel);

                case Local n:
                    DesugarLocalBinds(n.Binds, objLevel);
                    n.Body = Desugar(n.Body, objLevel);
                    return n;

                case LiteralBoolean:
                case LiteralNull:
                case LiteralNumber:
                    // Nothing to do.
                    return node;

                case LiteralString n:
                    if (n.Kind.FullyEscaped())
                    {
                        n.Value = Parser.StringUnescape(n.LocRange, n.Value);
                    }
                    n.Kind = LiteralStringKind.StringDouble;
                    n.BlockIndent = "";
                    return n;

                case Ast.Object n:
                    return DesugarFields(n, n.Fields, objLevel);

                case DesugaredObject:
                    // Desugaring something multiple times is a bad idea.
                    // All functions here should desugar nodes in one go.
                    throw new InvalidOperationException("Desugaring desugared object");

                case ObjectComp n:
                    return DesugarObjectComp(n, objLevel);

                case Parens n:
                    return Desugar(n.Inner, objLevel);

                case Self:
                    // Nothing to do.
                    return node;

                case SuperIndex n:
                    if (n.Id != null)
                    {
                        // Like Go, Kind is left as the zero value (StringSingle).
                        n.Index = new LiteralString { Value = n.Id };
                        n.Id = null;
                    }
                    n.Index = Desugar(n.Index, objLevel);
                    return n;

                case InSuper n:
                    n.Index = Desugar(n.Index, objLevel);
                    return n;

                case Unary n:
                    n.Expr = Desugar(n.Expr, objLevel);
                    return n;

                case Var:
                    // Nothing to do.
                    return node;

                default:
                    throw new InvalidOperationException("Desugarer does not recognize ast: " + node.GetType().Name);
            }
        }

        // desugarAST desugars Jsonnet expressions to reduce the number of constructs
        // the rest of the implementation needs to understand.
        //
        // Note that despite the name, desugar() is not idempotent.  String literals
        // have their escape codes translated to low-level characters during desugaring.
        //
        // Desugaring should happen immediately after parsing, i.e. before static
        // analysis and execution. Temporary variables introduced here should be
        // prefixed with $ to ensure they do not clash with variables used in user code.
        internal static Node DesugarAST(Node ast) => Desugar(ast, 0);
    }
}
