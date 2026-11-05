// Ports go-jsonnet ast/ast.go.
//
// Go's Node interface plus the embedded NodeBase struct become the abstract class Node.
// Go value structs that are mutated in place through slices (ObjectField, LocalBind, ...)
// become classes; Clone copies them explicitly. Go nil slices are empty lists here.

namespace JsonnetSharp.Ast
{
    // Node represents a node in the AST. The fields are those of Go's NodeBase.
    internal abstract class Node
    {
        // The fodder that precedes the first token of the node. If the node is
        // left-recursive, i.e. the first token is actually a token of a sub-expression,
        // then Fodder is null.
        public Fodder? Fodder;
        // The surrounding context of a node (e.g. a function it's in), built only when a trace prints it.
        public NodeContext? LazyCtx;

        public string? Ctx => LazyCtx?.ToString();
        public string[] FreeVars = System.Array.Empty<string>();
        public LocationRange LocRange;

        // Copies the NodeBase fields (Go: NodeBase: other.NodeBase).
        public void SetNodeBase(Node other)
        {
            Fodder = other.Fodder;
            LazyCtx = other.LazyCtx;
            FreeVars = other.FreeVars;
            LocRange = other.LocRange;
        }

        // NewNodeBaseLoc
        public void SetNodeBase(LocationRange loc, Fodder? fodder)
        {
            LocRange = loc;
            Fodder = fodder;
        }

        internal Node ShallowCopy() => (Node)MemberwiseClone();
    }

    // IfSpec represents an if-specification in a comprehension.
    internal sealed class IfSpec
    {
        public Node Expr = null!;
        public Fodder? IfFodder;
    }

    // ForSpec represents a for-specification in a comprehension.
    // expr for x in arr1 for y in arr2 for z in arr3
    // is represented as ForSpec(z, outer=ForSpec(y, outer=ForSpec(x, outer=null))).
    // Ifs are attached to the ForSpec on their left.
    internal sealed class ForSpec
    {
        public Fodder? ForFodder;
        public Fodder? VarFodder;
        public List<IfSpec> Conditions = new();
        public ForSpec? Outer;
        public Node Expr = null!;
        public string VarName = "";
        public Fodder? InFodder;

        internal ForSpec ShallowCopy() => (ForSpec)MemberwiseClone();
    }

    // Apply represents a function call
    internal sealed class Apply : Node
    {
        public Node Target = null!;
        public Fodder? FodderLeft;
        public Arguments Arguments = new();
        public Fodder? FodderRight;
        public Fodder? TailStrictFodder;
        // Always false if there were no arguments.
        public bool TrailingComma;
        public bool TailStrict;
    }

    // NamedArgument represents a named argument to function call x=1.
    internal sealed class NamedArgument
    {
        public Fodder? NameFodder;
        public string Name = "";
        public Fodder? EqFodder;
        public Node Arg = null!;
        public Fodder? CommaFodder;
    }

    // CommaSeparatedExpr represents an element of a comma-separated list of expressions.
    internal sealed class CommaSeparatedExpr
    {
        public Node Expr = null!;
        public Fodder? CommaFodder;
    }

    // Arguments represents positional and named arguments to a function call f(x, y, z=1).
    internal sealed class Arguments
    {
        public List<CommaSeparatedExpr> Positional = new();
        public List<NamedArgument> Named = new();
    }

    // ApplyBrace represents e { }.  Desugared to e + { }.
    internal sealed class ApplyBrace : Node
    {
        public Node Left = null!;
        public Node Right = null!;
    }

    // Array represents array constructors [1, 2, 3].
    internal sealed class Array : Node
    {
        public List<CommaSeparatedExpr> Elements = new();
        public Fodder? CloseFodder;
        // Always false if there were no elements.
        public bool TrailingComma;
    }

    // ArrayComp represents array comprehensions (which are like Python list comprehensions)
    internal sealed class ArrayComp : Node
    {
        public Node Body = null!;
        public Fodder? TrailingCommaFodder;
        public ForSpec Spec = new();
        public Fodder? CloseFodder;
        public bool TrailingComma;
    }

    // Assert represents an assert expression (not an object-level assert).
    // After parsing, Message can be null indicating that no message was specified.
    // This AST is eliminated by desugaring.
    internal sealed class Assert : Node
    {
        public Node Cond = null!;
        public Node? Message;
        public Node Rest = null!;
        public Fodder? ColonFodder;
        public Fodder? SemicolonFodder;
    }

    // BinaryOp represents a binary operator.
    internal enum BinaryOp
    {
        BopMult,
        BopDiv,
        BopPercent,

        BopPlus,
        BopMinus,

        BopShiftL,
        BopShiftR,

        BopGreater,
        BopGreaterEq,
        BopLess,
        BopLessEq,
        BopIn,

        BopManifestEqual,
        BopManifestUnequal,

        BopBitwiseAnd,
        BopBitwiseXor,
        BopBitwiseOr,

        BopAnd,
        BopOr,
    }

    // UnaryOp represents a unary operator.
    internal enum UnaryOp
    {
        UopNot,
        UopBitwiseNot,
        UopPlus,
        UopMinus,
    }

    internal static class Operators
    {
        private static readonly string[] BopStrings =
        {
            "*", "/", "%",
            "+", "-",
            "<<", ">>",
            ">", ">=", "<", "<=", "in",
            "==", "!=",
            "&", "^", "|",
            "&&", "||",
        };

        private static readonly string[] UopStrings = { "!", "~", "+", "-" };

        // BopMap is a map from binary operator token strings to BinaryOp values.
        public static readonly Dictionary<string, BinaryOp> BopMap = BuildMap<BinaryOp>(BopStrings);

        // UopMap is a map from unary operator token strings to UnaryOp values.
        public static readonly Dictionary<string, UnaryOp> UopMap = BuildMap<UnaryOp>(UopStrings);

        private static Dictionary<string, T> BuildMap<T>(string[] strings)
            where T : struct
        {
            Dictionary<string, T> map = new(StringComparer.Ordinal);
            for (int i = 0; i < strings.Length; i++)
            {
                map[strings[i]] = (T)Enum.ToObject(typeof(T), i);
            }
            return map;
        }

        // Go: BinaryOp.String()
        public static string ToOpString(this BinaryOp b)
        {
            if (b < 0 || (int)b >= BopStrings.Length)
            {
                throw new InvalidOperationException("INTERNAL ERROR: Unrecognised binary operator: " + (int)b);
            }
            return BopStrings[(int)b];
        }

        // Go: UnaryOp.String()
        public static string ToOpString(this UnaryOp u)
        {
            if (u < 0 || (int)u >= UopStrings.Length)
            {
                throw new InvalidOperationException("INTERNAL ERROR: Unrecognised unary operator: " + (int)u);
            }
            return UopStrings[(int)u];
        }
    }

    // Binary represents binary operators.
    internal sealed class Binary : Node
    {
        public Node Right = null!;
        public Node Left = null!;
        public Fodder? OpFodder;
        public BinaryOp Op;
    }

    // Conditional represents if/then/else.
    // After parsing, BranchFalse can be null indicating that no else branch was specified.
    // The desugarer fills this in with a LiteralNull.
    internal sealed class Conditional : Node
    {
        public Node Cond = null!;
        public Node BranchTrue = null!;
        public Node? BranchFalse;
        public Fodder? ThenFodder;
        public Fodder? ElseFodder;
    }

    // Dollar represents the $ keyword
    internal sealed class Dollar : Node
    {
    }

    // Error represents the error e.
    internal sealed class Error : Node
    {
        public Node Expr = null!;
    }

    // Function represents a function definition
    internal sealed class Function : Node
    {
        public Fodder? ParenLeftFodder;
        public Fodder? ParenRightFodder;
        public Node Body = null!;
        public List<Parameter> Parameters = new();
        // Always false if there were no parameters.
        public bool TrailingComma;
    }

    // Parameter represents a parameter of function.
    // If DefaultArg is set, it's an optional named parameter.
    // Otherwise, it's a positional parameter and EqFodder is not used.
    internal sealed class Parameter
    {
        public Fodder? NameFodder;
        public string Name = "";
        public Fodder? CommaFodder;
        public Fodder? EqFodder;
        public Node? DefaultArg;
        public LocationRange LocRange;

        internal Parameter ShallowCopy() => (Parameter)MemberwiseClone();
    }

    // CommaSeparatedID represents an element of a comma-separated list of identifiers.
    internal sealed class CommaSeparatedID
    {
        public Fodder? NameFodder;
        public string Name = "";
        public Fodder? CommaFodder;
    }

    // Import represents import "file".
    internal sealed class Import : Node
    {
        public LiteralString File = null!;
    }

    // ImportStr represents importstr "file".
    internal sealed class ImportStr : Node
    {
        public LiteralString File = null!;
    }

    // ImportBin represents importbin "file".
    internal sealed class ImportBin : Node
    {
        public LiteralString File = null!;
    }

    // Index represents both e[e] and the syntax sugar e.f.
    // One of IndexExpr and Id will be null before desugaring. After desugaring Id will be null.
    internal sealed class Index : Node
    {
        public Node Target = null!;
        // Go: Index.Index (renamed because a C# member cannot share its type's name).
        public Node? IndexExpr;
        // When IndexExpr is being used, this is the fodder before the ']'.
        // When Id is being used, this is the fodder before the id.
        public Fodder? RightBracketFodder;
        // When IndexExpr is being used, this is the fodder before the '['.
        // When Id is being used, this is the fodder before the '.'.
        public Fodder? LeftBracketFodder;
        public string? Id;
    }

    // Slice represents an array slice a[begin:end:step].
    internal sealed class Slice : Node
    {
        public Node Target = null!;
        public Fodder? LeftBracketFodder;
        // Each of these can be null
        public Node? BeginIndex;
        public Fodder? EndColonFodder;
        public Node? EndIndex;
        public Fodder? StepColonFodder;
        public Node? Step;
        public Fodder? RightBracketFodder;
    }

    // LocalBind is a helper class for Local
    internal sealed class LocalBind
    {
        public Fodder? VarFodder;
        // If Fun is set then its body == Body.
        public Node Body = null!;
        public Fodder? EqFodder;
        public string Variable = "";
        // The fodder before the closing ',' or ';' (whichever it is)
        public Fodder? CloseFodder;
        // There is no base fodder in Fun because there was no `function` keyword.
        public Function? Fun;
        public LocationRange LocRange;

        internal LocalBind ShallowCopy() => (LocalBind)MemberwiseClone();
    }

    // Local represents local x = e; e.  After desugaring, functionSugar is false.
    internal sealed class Local : Node
    {
        public List<LocalBind> Binds = new();
        public Node Body = null!;
    }

    // LiteralBoolean represents true and false
    internal sealed class LiteralBoolean : Node
    {
        public bool Value;
    }

    // LiteralNull represents the null keyword
    internal sealed class LiteralNull : Node
    {
    }

    // LiteralNumber represents a JSON number
    internal sealed class LiteralNumber : Node
    {
        public string OriginalString = "";
    }

    // LiteralStringKind represents the kind of a literal string.
    internal enum LiteralStringKind
    {
        StringSingle,
        StringDouble,
        StringBlock,
        VerbatimStringDouble,
        VerbatimStringSingle,
    }

    internal static class LiteralStringKindExtensions
    {
        // FullyEscaped returns true iff the literal string kind may contain escape
        // sequences that require unescaping.
        public static bool FullyEscaped(this LiteralStringKind k) => k switch
        {
            LiteralStringKind.StringSingle or LiteralStringKind.StringDouble => true,
            LiteralStringKind.StringBlock or LiteralStringKind.VerbatimStringDouble or LiteralStringKind.VerbatimStringSingle => false,
            _ => throw new InvalidOperationException("Unknown string kind: " + (int)k),
        };
    }

    // LiteralString represents a JSON string
    internal sealed class LiteralString : Node
    {
        public string Value = "";
        public string BlockIndent = "";
        public string BlockTermIndent = "";
        public LiteralStringKind Kind;
    }

    // ObjectFieldKind represents the kind of an object field.
    internal enum ObjectFieldKind
    {
        // <f1> 'assert' <expr2> '[' <opF> ':' <expr3> ']' <commaF>, where expr3 can be null
        ObjectAssert,
        // <f1> <id> <colon> <expr2> <commaF>
        ObjectFieldID,
        // <f1> '[' <expr1> <f2> ']' <colon> <expr2> <commaF>
        ObjectFieldExpr,
        // <expr1> <colon> <expr2> <commaF>
        ObjectFieldStr,
        // <f1> 'local' <f2> <id> '=' <expr2> <commaF>
        ObjectLocal,
    }

    // ObjectFieldHide represents the visibility of an object field.
    internal enum ObjectFieldHide
    {
        ObjectFieldHidden, // f:: e
        ObjectFieldInherit, // f: e
        ObjectFieldVisible, // f::: e
    }

    // ObjectField represents a field of an object or object comprehension.
    internal sealed class ObjectField
    {
        // f(x, y, z): ...  (ignore if kind == ObjectAssert)
        // If Method is set then Expr2 == Method.Body.
        public Function? Method;
        public string? Id;
        public Fodder? Fodder2;
        public Fodder? Fodder1;
        public Fodder? OpFodder;
        public Fodder? CommaFodder;
        public Node? Expr1; // Not in scope of the object
        public Node Expr2 = null!; // In scope of the object (can see self). Always set.
        public Node? Expr3; // In scope of the object (can see self).
        public LocationRange LocRange;
        public ObjectFieldKind Kind;
        public ObjectFieldHide Hide; // (ignore if kind != ObjectFieldID/Expr/Str)
        public bool SuperSugar; // +:  (ignore if kind != ObjectFieldID/Expr/Str)

        // ObjectFieldLocalNoMethod creates a non-method local object field.
        public static ObjectField LocalNoMethod(string id, Node body, LocationRange loc) => new()
        {
            Kind = ObjectFieldKind.ObjectLocal,
            Hide = ObjectFieldHide.ObjectFieldVisible,
            Id = id,
            Expr2 = body,
            LocRange = loc,
        };

        internal ObjectField ShallowCopy() => (ObjectField)MemberwiseClone();
    }

    // Object represents object constructors { f: e ... }.
    // The trailing comma is only allowed if Fields.Count > 0. Converted to DesugaredObject during desugaring.
    internal sealed class Object : Node
    {
        public List<ObjectField> Fields = new();
        public Fodder? CloseFodder;
        public bool TrailingComma;
    }

    // DesugaredObjectField represents a desugared object field.
    internal sealed class DesugaredObjectField
    {
        public Node Name = null!;
        public Node Body = null!;
        public LocationRange LocRange;
        public ObjectFieldHide Hide;
        public bool PlusSuper;

        internal DesugaredObjectField ShallowCopy() => (DesugaredObjectField)MemberwiseClone();
    }

    // DesugaredObject represents object constructors { f: e ... } after desugaring.
    // The assertions either return true or raise an error.
    internal sealed class DesugaredObject : Node
    {
        public List<Node> Asserts = new();
        public List<DesugaredObjectField> Fields = new();
        public List<LocalBind> Locals = new();
    }

    // ObjectComp represents object comprehension { [e]: e for x in e for.. if... }.
    internal sealed class ObjectComp : Node
    {
        public List<ObjectField> Fields = new();
        public Fodder? TrailingCommaFodder;
        public Fodder? CloseFodder;
        public ForSpec Spec = new();
        public bool TrailingComma;
    }

    // Parens represents parentheses ( e )
    internal sealed class Parens : Node
    {
        public Node Inner = null!;
        public Fodder? CloseFodder;
    }

    // Self represents the self keyword.
    internal sealed class Self : Node
    {
    }

    // SuperIndex represents the super[e] and super.f constructs.
    // Either Index or Id will be set before desugaring. After desugaring, Id will be null.
    internal sealed class SuperIndex : Node
    {
        // If super.f, the fodder before the 'f'. If super[e], the fodder before the ']'.
        public Fodder? IDFodder;
        public Node? Index;
        // If super.f, the fodder before the '.'. If super[e], the fodder before the '['.
        public Fodder? DotFodder;
        public string? Id;
    }

    // InSuper represents the e in super construct.
    internal sealed class InSuper : Node
    {
        public Node Index = null!;
        public Fodder? InFodder;
        public Fodder? SuperFodder;
    }

    // Unary represents unary operators.
    internal sealed class Unary : Node
    {
        public Node Expr = null!;
        public UnaryOp Op;
    }

    // Var represents variables.
    internal sealed class Var : Node
    {
        public string Id = "";
    }
}
