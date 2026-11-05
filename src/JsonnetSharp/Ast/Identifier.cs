// Ports go-jsonnet ast/identifier.go.
// Go's ast.Identifier is represented as a plain string and ast.Identifiers as string[].

namespace JsonnetSharp.Ast
{
    // IdentifierSet represents an Identifier set.
    internal sealed class IdentifierSet : HashSet<string>
    {
        public IdentifierSet()
            : base(StringComparer.Ordinal)
        {
        }

        public IdentifierSet(params string[] idents)
            : base(idents, StringComparer.Ordinal)
        {
        }

        private IdentifierSet(IdentifierSet other)
            : base(other, StringComparer.Ordinal)
        {
        }

        // AddIdentifiers adds a slice of identifiers to the set.
        public void AddIdentifiers(IEnumerable<string> idents)
        {
            foreach (string ident in idents)
            {
                Add(ident);
            }
        }

        // ToSlice returns the elements in unspecified order.
        public string[] ToSlice()
        {
            string[] idents = new string[Count];
            CopyTo(idents);
            return idents;
        }

        // ToOrderedSlice returns the elements of the set as a slice ordered like Go strings (bytewise).
        public string[] ToOrderedSlice()
        {
            string[] idents = ToSlice();
            // Ordinal UTF-16 order equals UTF-8 byte order here: identifiers are ASCII (plus "$", "$std").
            System.Array.Sort(idents, StringComparer.Ordinal);
            return idents;
        }

        // Clone returns a clone of the set.
        public IdentifierSet Clone() => new(this);
    }
}
