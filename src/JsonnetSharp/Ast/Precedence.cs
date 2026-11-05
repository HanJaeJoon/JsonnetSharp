// Ports go-jsonnet internal/ast/ast.go.

namespace JsonnetSharp.Ast
{
    internal static class Precedence
    {
        public const int MinPrecedence = 1; // Var, Self, Parens, literals
        public const int ApplyPrecedence = 2; // Function calls and indexing.
        public const int UnaryPrecedence = 4; // Logical and bitwise negation, unary + -
        public const int MaxPrecedence = 16; // Local, If, Import, Function, Error

        private static readonly int[] BopPrecedence =
        {
            5, 5, 5, // * / %
            6, 6, // + -
            7, 7, // << >>
            8, 8, 8, 8, 8, // > >= < <= in
            9, 9, // == !=
            10, // &
            11, // ^
            12, // |
            13, // &&
            14, // ||
        };

        public static int BinaryOpPrecedence(BinaryOp bop) => BopPrecedence[(int)bop];

        // ExprPrecedence gives the precedence level of an operation, so that it can be
        // correctly parsed and unparsed/formatted.
        public static int ExprPrecedence(Node node) => node switch
        {
            Apply or ApplyBrace or Index or SuperIndex or Slice => ApplyPrecedence,
            Unary => UnaryPrecedence,
            Binary b => BopPrecedence[(int)b.Op],
            InSuper => BopPrecedence[(int)BinaryOp.BopIn],
            Local or Assert or Error or Import or ImportStr or ImportBin or Conditional => MaxPrecedence,
            _ => MinPrecedence,
        };

        public static int TighterPrecedence(int p) => p == MinPrecedence ? p : p - 1;
    }
}
