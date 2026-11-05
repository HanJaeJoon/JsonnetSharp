// Ports go-jsonnet ast/fodder.go.

// Go's ast.Fodder is a slice of FodderElement; a null Fodder plays the role of a nil slice.
global using Fodder = System.Collections.Generic.List<JsonnetSharp.Ast.FodderElement>;

namespace JsonnetSharp.Ast
{
    internal enum FodderKind
    {
        // A line ending; the next token, paragraph, or interstitial goes on a new line.
        // A single comment string is allowed, which flows before the new line.
        LineEnd,
        // A /* C-style */ comment in the middle of a line. There is exactly one comment string.
        Interstitial,
        // A comment consisting of at least one line.
        Paragraph,
    }

    // FodderElement is a single piece of fodder.
    internal sealed class FodderElement
    {
        public List<string> Comment;
        public FodderKind Kind;
        public int Blanks;
        public int Indent;

        public FodderElement(FodderKind kind, int blanks, int indent, List<string> comment)
        {
            Kind = kind;
            Blanks = blanks;
            Indent = indent;
            Comment = comment;
        }

        // MakeFodderElement is a helper function that checks some preconditions.
        public static FodderElement Make(FodderKind kind, int blanks, int indent, List<string> comment)
        {
            if (kind == FodderKind.LineEnd && comment.Count > 1)
            {
                throw new InvalidOperationException("FodderLineEnd but comment == [" + string.Join(" ", comment) + "].");
            }
            if (kind == FodderKind.Interstitial && blanks > 0)
            {
                throw new InvalidOperationException("FodderInterstitial but blanks == " + blanks);
            }
            if (kind == FodderKind.Interstitial && indent > 0)
            {
                throw new InvalidOperationException("FodderInterstitial but indent == " + blanks);
            }
            if (kind == FodderKind.Interstitial && comment.Count != 1)
            {
                throw new InvalidOperationException("FodderInterstitial but comment == [" + string.Join(" ", comment) + "].");
            }
            if (kind == FodderKind.Paragraph && comment.Count == 0)
            {
                throw new InvalidOperationException("FodderParagraph but comment was empty");
            }
            return new FodderElement(kind, blanks, indent, comment);
        }
    }

    internal static class Fodders
    {
        // FodderHasCleanEndline is true if the fodder doesn't end with an interstitial.
        public static bool HasCleanEndline(Fodder? fodder) =>
            fodder is { Count: > 0 } && fodder[fodder.Count - 1].Kind != FodderKind.Interstitial;

        // FodderAppend appends to the fodder but preserves constraints.
        public static void Append(ref Fodder? a, FodderElement elem)
        {
            if (HasCleanEndline(a) && elem.Kind == FodderKind.LineEnd)
            {
                if (elem.Comment.Count > 0)
                {
                    // The line end had a comment, so create a single line paragraph for it.
                    a!.Add(FodderElement.Make(FodderKind.Paragraph, elem.Blanks, elem.Indent, elem.Comment));
                }
                else
                {
                    // Merge it into the previous line end.
                    FodderElement back = a![a.Count - 1];
                    back.Indent = elem.Indent;
                    back.Blanks += elem.Blanks;
                }
            }
            else
            {
                a ??= new Fodder();
                if (!HasCleanEndline(a) && elem.Kind == FodderKind.Paragraph)
                {
                    a.Add(FodderElement.Make(FodderKind.LineEnd, 0, elem.Indent, new List<string>()));
                }
                a.Add(elem);
            }
        }

        // FodderConcat concats the two fodders but also preserves constraints.
        // Namely, a LineEnd is not allowed to follow a Paragraph or a LineEnd.
        public static Fodder? Concat(Fodder? a, Fodder? b)
        {
            if (a is not { Count: > 0 })
            {
                return b;
            }
            if (b is not { Count: > 0 })
            {
                return a;
            }
            Fodder? r = a;
            // Carefully add the first element of b.
            Append(ref r, b[0]);
            // Add the rest of b.
            for (int i = 1; i < b.Count; i++)
            {
                r!.Add(b[i]);
            }
            return r;
        }

        // FodderMoveFront moves b to the front of a.
        public static void MoveFront(ref Fodder? a, ref Fodder? b)
        {
            a = Concat(b, a);
            b = new Fodder();
        }

        // FodderEnsureCleanNewline adds a LineEnd to the fodder if necessary.
        public static void EnsureCleanNewline(ref Fodder? fodder)
        {
            if (!HasCleanEndline(fodder))
            {
                Append(ref fodder, FodderElement.Make(FodderKind.LineEnd, 0, 0, new List<string>()));
            }
        }

        // FodderElementCountNewlines returns the number of new line chars represented by the fodder element.
        public static int ElementCountNewlines(FodderElement elem) => elem.Kind switch
        {
            FodderKind.Interstitial => 0,
            FodderKind.LineEnd => 1,
            FodderKind.Paragraph => elem.Comment.Count + elem.Blanks,
            _ => throw new InvalidOperationException("Unknown FodderElement kind " + (int)elem.Kind),
        };

        // FodderCountNewlines returns the number of new line chars represented by the fodder.
        public static int CountNewlines(Fodder? fodder)
        {
            int sum = 0;
            if (fodder != null)
            {
                foreach (FodderElement elem in fodder)
                {
                    sum += ElementCountNewlines(elem);
                }
            }
            return sum;
        }
    }
}
