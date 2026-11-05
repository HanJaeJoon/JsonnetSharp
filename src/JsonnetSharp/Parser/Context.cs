// Ports go-jsonnet internal/parser/context.go.

using System.Text;
using JsonnetSharp.Ast;

namespace JsonnetSharp
{
    internal sealed partial class Parser
    {
        private const string TopLevelContext = "$";
        private const string Anonymous = "anonymous";

        private static readonly List<Node> NoNodes = new();

        // DirectChildren are children of AST node that are executed in the same context
        // and environment as their parent. It supports ASTs before and after desugaring.
        //
        // They must satisfy the following rules:
        // * (no-delayed-evaluation) They are evaluated when their parent is evaluated or never.
        // * (no-indirect-evaluation) They cannot be evaluated during evaluation of any non-direct children
        // * (same-environment) They must be evaluated in the same environment as their parent
        internal static List<Node> DirectChildren(Node node)
        {
            switch (node)
            {
                case Apply n:
                    return new List<Node> { n.Target };
                case ApplyBrace n:
                    return new List<Node> { n.Left, n.Right };
                case Ast.Array:
                    return NoNodes;
                case Assert n:
                    if (n.Message != null)
                    {
                        return new List<Node> { n.Cond, n.Message, n.Rest };
                    }
                    return new List<Node> { n.Cond, n.Rest };
                case Binary n:
                    return new List<Node> { n.Left, n.Right };
                case Conditional n:
                    if (n.BranchFalse != null)
                    {
                        return new List<Node> { n.Cond, n.BranchTrue, n.BranchFalse };
                    }
                    return new List<Node> { n.Cond, n.BranchTrue };
                case Dollar:
                    return NoNodes;
                case Ast.Error n:
                    return new List<Node> { n.Expr };
                case Function:
                    return NoNodes;
                case Import or ImportStr or ImportBin:
                    return NoNodes;
                case Ast.Index n:
                    if (n.Id != null)
                    {
                        return NoNodes; // non-desugared dot reference
                    }
                    return new List<Node> { n.Target, n.IndexExpr! };
                case Slice n:
                    {
                        List<Node> result = new();
                        if (n.Target != null)
                        {
                            result.Add(n.Target);
                        }
                        if (n.BeginIndex != null)
                        {
                            result.Add(n.BeginIndex);
                        }
                        if (n.EndIndex != null)
                        {
                            result.Add(n.EndIndex);
                        }
                        return result;
                    }
                case Local:
                    return NoNodes;
                case LiteralBoolean or LiteralNull or LiteralNumber or LiteralString:
                    return NoNodes;
                case Ast.Object n:
                    return ObjectFieldsDirectChildren(n.Fields);
                case DesugaredObject n:
                    return DesugaredObjectDirectChildren(n);
                case ArrayComp n:
                    {
                        List<Node> result = new();
                        AddSpecChildren(result, n.Spec);
                        return result;
                    }
                case ObjectComp n:
                    {
                        List<Node> result = ObjectFieldsDirectChildren(n.Fields);
                        AddSpecChildren(result, n.Spec);
                        return result;
                    }
                case Parens n:
                    return new List<Node> { n.Inner };
                case Self:
                    return NoNodes;
                case SuperIndex n:
                    if (n.Id != null)
                    {
                        return NoNodes;
                    }
                    return new List<Node> { n.Index! };
                case InSuper n:
                    return new List<Node> { n.Index };
                case Unary n:
                    return new List<Node> { n.Expr };
                case Var:
                    return NoNodes;
            }
            throw new InvalidOperationException("directChildren: Unknown node " + node.GetType().Name);
        }

        private static void AddSpecChildren(List<Node> result, ForSpec? spec)
        {
            while (spec != null)
            {
                result.Add(spec.Expr);
                foreach (IfSpec ifspec in spec.Conditions)
                {
                    result.Add(ifspec.Expr);
                }
                spec = spec.Outer;
            }
        }

        // thunkChildren are children of AST node that are executed in a new context
        // and capture environment from parent (thunked).
        // It supports ASTs before and after desugaring.
        //
        // Rules:
        // * (same-environment) They must be evaluated in the same environment as their parent
        // * (not-direct) If they can be direct children, they should (and cannot be thunked).
        private static List<Node> ThunkChildren(Node node)
        {
            switch (node)
            {
                case Apply n:
                    {
                        List<Node> nodes = new();
                        foreach (CommaSeparatedExpr arg in n.Arguments.Positional)
                        {
                            nodes.Add(arg.Expr);
                        }
                        foreach (NamedArgument arg in n.Arguments.Named)
                        {
                            nodes.Add(arg.Arg);
                        }
                        return nodes;
                    }
                case Ast.Array n:
                    {
                        List<Node> nodes = new();
                        foreach (CommaSeparatedExpr element in n.Elements)
                        {
                            nodes.Add(element.Expr);
                        }
                        return nodes;
                    }
                case ArrayComp n:
                    return new List<Node> { n.Body };
                case ApplyBrace or Assert or Binary or Conditional or Dollar or Ast.Error or Function
                    or Import or ImportStr or ImportBin or Ast.Index or Slice or Local
                    or LiteralBoolean or LiteralNull or LiteralNumber or LiteralString
                    or DesugaredObject or Ast.Object or ObjectComp or Parens or Self or SuperIndex
                    or InSuper or Unary or Var:
                    return NoNodes;
            }
            throw new InvalidOperationException("thunkChildren: Unknown node " + node.GetType().Name);
        }

        private static List<Node> ObjectFieldsDirectChildren(List<ObjectField> fields)
        {
            List<Node> result = new();
            foreach (ObjectField field in fields)
            {
                if (field.Expr1 != null)
                {
                    result.Add(field.Expr1);
                }
            }
            return result;
        }

        private static List<Node> InObjectFieldsChildren(List<ObjectField> fields)
        {
            List<Node> result = new();
            foreach (ObjectField field in fields)
            {
                if (field.Method != null)
                {
                    result.Add(field.Method);
                }
                else
                {
                    if (field.Expr2 != null)
                    {
                        result.Add(field.Expr2);
                    }
                    if (field.Expr3 != null)
                    {
                        result.Add(field.Expr3);
                    }
                }
            }
            return result;
        }

        private static List<Node> DesugaredObjectDirectChildren(DesugaredObject obj)
        {
            List<Node> result = new();
            foreach (DesugaredObjectField field in obj.Fields)
            {
                if (field.Name == null)
                {
                    throw new InvalidOperationException("Name cannot be nil");
                }
                result.Add(field.Name);
            }
            return result;
        }

        private static List<Node> InDesugaredObjectSpecialChildren(DesugaredObject obj)
        {
            List<Node> result = new(obj.Fields.Count + obj.Locals.Count);
            foreach (DesugaredObjectField field in obj.Fields)
            {
                result.Add(field.Body);
            }
            foreach (LocalBind local in obj.Locals)
            {
                result.Add(local.Body);
            }
            return result;
        }

        // specialChildren returns children that are neither direct nor thunked,
        // e.g. object field body. These nodes are evaluated in a different environment
        // from their parent. It supports ASTs before and after desugaring.
        private static List<Node> SpecialChildren(Node node)
        {
            switch (node)
            {
                case Function n:
                    {
                        List<Node> children = new() { n.Body };
                        foreach (Parameter child in n.Parameters)
                        {
                            if (child.DefaultArg != null)
                            {
                                children.Add(child.DefaultArg);
                            }
                        }
                        return children;
                    }
                case Local n:
                    {
                        List<Node> children = new(n.Binds.Count + 1) { n.Body };
                        foreach (LocalBind bind in n.Binds)
                        {
                            children.Add(bind.Body);
                        }
                        return children;
                    }
                case DesugaredObject n:
                    return InDesugaredObjectSpecialChildren(n);
                case Ast.Object n:
                    return InObjectFieldsChildren(n.Fields);
                case ArrayComp n:
                    return new List<Node> { n.Body };
                case ObjectComp n:
                    return InObjectFieldsChildren(n.Fields);
                case Apply or ApplyBrace or Ast.Array or Assert or Binary or Conditional or Dollar or Ast.Error
                    or Import or ImportStr or ImportBin or Ast.Index or Slice
                    or LiteralBoolean or LiteralNull or LiteralNumber or LiteralString
                    or Self or SuperIndex or InSuper or Unary or Var or Parens:
                    return NoNodes;
            }
            throw new InvalidOperationException("specialChildren: Unknown node " + node.GetType().Name);
        }

        // Children returns all children of a node. It supports ASTs before and after desugaring.
        internal static List<Node> Children(Node node)
        {
            List<Node> result = new();
            result.AddRange(DirectChildren(node));
            result.AddRange(ThunkChildren(node));
            result.AddRange(SpecialChildren(node));
            return result;
        }

        private static NodeContext FunctionContext(string funcName) => new("function <" + funcName + ">");

        private static NodeContext ObjectContext(string objName) => new("object <" + objName + ">");

        // addContext adds context to a node and its whole subtree.
        //
        // context is the surrounding context of a node (e.g. a function it's in)
        //
        // bind is a name that the node is bound to, i.e. if node is a local bind body
        // then bind is its name. For nodes that are not bound to variables `anonymous`
        // should be passed. For example:
        // local x = 2 + 2; x
        // In such case bind for binary node 2 + 2 is "x" and for every other node,
        // including its children, its anonymous.
        private static void AddContext(Node? node, NodeContext context, string bind)
        {
            if (node == null)
            {
                return;
            }
            StackGuard.Ensure(node.LocRange);

            node.LazyCtx = context;

            switch (node)
            {
                case Function n:
                    {
                        NodeContext funContext = FunctionContext(bind);
                        AddContext(n.Body, funContext, Anonymous);
                        foreach (Parameter p in n.Parameters)
                        {
                            if (p.DefaultArg != null)
                            {
                                // Default arguments have the same context as the function body.
                                AddContext(p.DefaultArg, funContext, Anonymous);
                            }
                        }
                        break;
                    }
                case Ast.Object n:
                    AddObjectContext(DirectChildren(n), n.Fields, context, bind);
                    break;
                case ObjectComp n:
                    AddObjectContext(DirectChildren(n), n.Fields, context, bind);
                    break;
                case Local n:
                    foreach (LocalBind b in n.Binds)
                    {
                        NodeContext namedThunkContext = new("thunk <" + b.Variable + "> from <", context);
                        if (b.Fun != null)
                        {
                            AddContext(b.Fun, namedThunkContext, b.Variable);
                        }
                        else
                        {
                            AddContext(b.Body, namedThunkContext, b.Variable);
                        }
                    }
                    AddContext(n.Body, context, bind);
                    break;
                default:
                    {
                        foreach (Node child in DirectChildren(node))
                        {
                            AddContext(child, context, Anonymous);
                        }

                        NodeContext thunkContext = new("thunk from <", context);
                        foreach (Node child in ThunkChildren(node))
                        {
                            AddContext(child, thunkContext, Anonymous);
                        }
                        break;
                    }
            }
        }

        private static void AddObjectContext(List<Node> outOfObject, List<ObjectField> fields, NodeContext context, string bind)
        {
            foreach (Node f in outOfObject)
            {
                // This actually is evaluated outside of object
                AddContext(f, context, Anonymous);
            }

            NodeContext objContext = ObjectContext(bind);
            foreach (Node f in InObjectFieldsChildren(fields))
            {
                AddContext(f, objContext, Anonymous);
            }
        }
    }

    // A context string, "prefix" + inner + ">" when it has an inner context. Go builds every string up front,
    // which is quadratic in the nesting depth; this keeps parsing linear and formats a context on demand.
    internal sealed class NodeContext
    {
        private readonly string _prefix;
        private readonly NodeContext? _inner;
        private string? _text;

        public NodeContext(string text)
        {
            _prefix = text;
            _text = text;
        }

        public NodeContext(string prefix, NodeContext inner)
        {
            _prefix = prefix;
            _inner = inner;
        }

        public override string ToString()
        {
            if (_text == null)
            {
                StringBuilder sb = new();
                int depth = 0;
                NodeContext c = this;
                for (; c._text == null; c = c._inner!)
                {
                    sb.Append(c._prefix);
                    depth++;
                }
                _text = sb.Append(c._text).Append('>', depth).ToString();
            }
            return _text;
        }
    }
}
