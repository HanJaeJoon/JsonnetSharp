// Ports go-jsonnet internal/parser/lexer.go.
//
// The lexer works on the UTF-8 bytes of the input, like Go, so that columns are byte offsets.

using System.Text;
using JsonnetSharp.Ast;

namespace JsonnetSharp
{
    internal enum TokenKind
    {
        // Symbols
        BraceL,
        BraceR,
        BracketL,
        BracketR,
        Comma,
        Dollar,
        Dot,
        ParenL,
        ParenR,
        Semicolon,

        // Arbitrary length lexemes
        Identifier,
        Number,
        Operator,
        StringBlock,
        StringDouble,
        StringSingle,
        VerbatimStringDouble,
        VerbatimStringSingle,

        // Keywords
        Assert,
        Else,
        Error,
        False,
        For,
        Function,
        If,
        Import,
        ImportStr,
        ImportBin,
        In,
        Local,
        NullLit,
        Self,
        Super,
        TailStrict,
        Then,
        True,

        // A special token that holds line/column information about the end of the file.
        EndOfFile,
    }

    internal static class TokenKindExtensions
    {
        private static readonly string[] TokenKindStrings =
        {
            // Symbols
            "\"{\"", "\"}\"", "\"[\"", "\"]\"", "\",\"", "\"$\"", "\".\"", "\"(\"", "\")\"", "\";\"",

            // Arbitrary length lexemes
            "IDENTIFIER", "NUMBER", "OPERATOR", "STRING_BLOCK", "STRING_DOUBLE", "STRING_SINGLE",
            "VERBATIM_STRING_DOUBLE", "VERBATIM_STRING_SINGLE",

            // Keywords
            "assert", "else", "error", "false", "for", "function", "if", "import", "importstr",
            "importbin", "in", "local", "null", "self", "super", "tailstrict", "then", "true",

            "end of file",
        };

        // Go: tokenKind.String()
        public static string ToKindString(this TokenKind tk)
        {
            if (tk < 0 || (int)tk >= TokenKindStrings.Length)
            {
                throw new InvalidOperationException("INTERNAL ERROR: Unknown token kind:: " + (int)tk);
            }
            return TokenKindStrings[(int)tk];
        }

        public static bool HasContent(this TokenKind tk) => tk is >= TokenKind.Identifier and <= TokenKind.VerbatimStringSingle;
    }

    internal sealed class Token
    {
        public Fodder? Fodder; // Any fodder that occurs before this token
        public string Data = ""; // Content of the token if it is not a keyword
        // Extra info for when kind == StringBlock
        public string StringBlockIndent = ""; // The sequence of whitespace that indented the block.
        public string StringBlockTermIndent = ""; // This is always fewer whitespace characters than in StringBlockIndent.
        public LocationRange Loc;
        public TokenKind Kind; // The type of the token

        public override string ToString()
        {
            if (Data.Length == 0)
            {
                return Kind.ToKindString();
            }
            else if (Kind.HasContent())
            {
                return "(" + Kind.ToKindString() + ", \"" + Data + "\")";
            }
            else
            {
                return "\"" + Data + "\"";
            }
        }
    }

    internal sealed class Lexer
    {
        private const int LexEOF = -1;

        private readonly string _importedFilename; // Imported filename, used for resolving relative imports
        private readonly byte[] _input; // The input string, as UTF-8
        private readonly Source _source;

        private readonly List<Token> _tokens = new(); // The tokens that we've generated so far

        // Information about the token we are working on right now
        private Fodder _fodder = new();
        private int _tokenStart;
        private Location _tokenStartLoc;

        // Was the last rune the first rune on a line (ignoring initial whitespace).
        private bool _freshLine;

        // Current position in input
        private int _byteNo; // Byte position of last rune read
        private int _lineNo; // Line number
        private int _lineStart; // Byte position of the last newline

        private Lexer(string diagnosticFilename, string importedFilename, byte[] input)
        {
            _input = input;
            _importedFilename = importedFilename;
            _source = Source.Build(diagnosticFilename, _input);
            _byteNo = 0;
            _lineNo = 1;
            _lineStart = 0;
            _tokenStartLoc = new Location(1, 1);
            _freshLine = true;
        }

        private static bool IsUpper(int r) => r >= 'A' && r <= 'Z';

        private static bool IsLower(int r) => r >= 'a' && r <= 'z';

        private static bool IsNumber(int r) => r >= '0' && r <= '9';

        private static bool IsIdentifierFirst(int r) => IsUpper(r) || IsLower(r) || r == '_';

        private static bool IsIdentifier(int r) => IsIdentifierFirst(r) || IsNumber(r);

        private static bool IsSymbol(int r)
        {
            switch (r)
            {
                case '!':
                case '$':
                case ':':
                case '~':
                case '+':
                case '-':
                case '&':
                case '|':
                case '^':
                case '=':
                case '<':
                case '>':
                case '*':
                case '/':
                case '%':
                    return true;
            }
            return false;
        }

        private static bool IsHorizontalWhitespace(int r) => r == ' ' || r == '\t' || r == '\r';

        private static bool IsWhitespace(int r) => r == '\n' || IsHorizontalWhitespace(r);

        // stripWhitespace strips whitespace from both ends of a string, but only up to
        // margin on the left hand side.  E.g., stripWhitespace("  foo ", 1) == " foo".
        // Whitespace is ASCII, so counting UTF-16 chars equals counting runes here.
        internal static string StripWhitespace(string s, int margin)
        {
            if (s.Length == 0)
            {
                return s;
            }
            int i = 0;
            while (i < s.Length && IsHorizontalWhitespace(s[i]) && i < margin)
            {
                i++;
            }
            int j = s.Length;
            while (j > i && IsHorizontalWhitespace(s[j - 1]))
            {
                j--;
            }
            return s.Substring(i, j - i);
        }

        // Split a string by \n and also strip left (up to margin) & right whitespace from each line.
        internal static List<string> LineSplit(string s, int margin)
        {
            List<string> ret = new();
            int start = 0;
            for (int k = 0; k < s.Length; k++)
            {
                if (s[k] == '\n')
                {
                    ret.Add(StripWhitespace(s.Substring(start, k - start), margin));
                    start = k + 1;
                }
            }
            ret.Add(StripWhitespace(s.Substring(start), margin));
            return ret;
        }

        // Check that b has at least the same whitespace prefix as a and returns the
        // amount of this whitespace, otherwise returns 0.  If a has no whitespace
        // prefix than return 0. a and b are byte ranges starting at aOff and bOff.
        private static int CheckWhitespace(byte[] a, int aOff, byte[] b, int bOff)
        {
            int aLen = a.Length - aOff;
            int bLen = b.Length - bOff;
            int i = 0;
            for (; i < aLen; i++)
            {
                if (a[aOff + i] != ' ' && a[aOff + i] != '\t')
                {
                    // a has run out of whitespace and b matched up to this point.
                    return i;
                }
                if (i >= bLen)
                {
                    // We ran off the edge of b while a still has whitespace.
                    return 0;
                }
                if (a[aOff + i] != b[bOff + i])
                {
                    // a has whitespace but b does not.
                    return 0;
                }
            }
            // We ran off the end of a and b kept up
            return i;
        }

        private bool InputHasPrefix(int pos, string prefix)
        {
            if (_input.Length - pos < prefix.Length)
            {
                return false;
            }
            for (int k = 0; k < prefix.Length; k++)
            {
                if (_input[pos + k] != prefix[k])
                {
                    return false;
                }
            }
            return true;
        }

        // Go keeps the bytes and decodes them later one rune at a time: each invalid byte is one U+FFFD.
        private string InputString(int from, int to)
        {
            StringBuilder sb = new(to - from);
            for (int k = from; k < to;)
            {
                GoUtf8.AppendRune(sb, GoUtf8.DecodeRune(_input, k, to, out int w));
                k += w;
            }
            return sb.ToString();
        }

        // next returns the next rune in the input.
        private int Next()
        {
            if (_byteNo >= _input.Length)
            {
                return LexEOF;
            }
            int r = GoUtf8.DecodeRune(_input, _byteNo, _input.Length, out int w);
            _byteNo += w;
            if (r == '\n')
            {
                _lineStart = _byteNo;
                _lineNo++;
                _freshLine = true;
            }
            else if (_freshLine)
            {
                if (!IsWhitespace(r))
                {
                    _freshLine = false;
                }
            }
            return r;
        }

        private void AcceptN(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Next();
            }
        }

        // peek returns but does not consume the next rune in the input.
        private int Peek()
        {
            if (_byteNo >= _input.Length)
            {
                return LexEOF;
            }
            return GoUtf8.DecodeRune(_input, _byteNo, _input.Length, out _);
        }

        private Location CurrentLocation() => new(_lineNo, _byteNo - _lineStart + 1);

        // Reset the current working token start to the current cursor position.  This
        // may throw away some characters.  This does not throw away any accumulated fodder.
        private void ResetTokenStart()
        {
            _tokenStart = _byteNo;
            _tokenStartLoc = CurrentLocation();
        }

        private void EmitFullToken(TokenKind kind, string data, string stringBlockIndent, string stringBlockTermIndent)
        {
            _tokens.Add(new Token
            {
                Kind = kind,
                Fodder = _fodder,
                Data = data,
                StringBlockIndent = stringBlockIndent,
                StringBlockTermIndent = stringBlockTermIndent,
                Loc = LocationRange.Make(_importedFilename, _source, _tokenStartLoc, CurrentLocation()),
            });
            _fodder = new Fodder();
        }

        private void EmitToken(TokenKind kind)
        {
            EmitFullToken(kind, InputString(_tokenStart, _byteNo), "", "");
            ResetTokenStart();
        }

        private void AddFodder(FodderKind kind, int blanks, int indent, List<string> comment)
        {
            _fodder.Add(FodderElement.Make(kind, blanks, indent, comment));
        }

        private void AddFodderSafe(FodderKind kind, int blanks, int indent, List<string> comment)
        {
            Fodder? f = _fodder;
            Fodders.Append(ref f, FodderElement.Make(kind, blanks, indent, comment));
            _fodder = f!;
        }

        private StaticError MakeStaticErrorPoint(string msg, Location loc) =>
            new(msg, LocationRange.Make(_importedFilename, _source, loc, loc));

        // lexWhitespace consumes all whitespace and returns the number of \n and number of
        // spaces after last \n.  It also converts \t to spaces.
        private (int NewLines, int Indent) LexWhitespace()
        {
            int r = Peek();
            int indent = 0;
            int newLines = 0;
            for (; IsWhitespace(r); r = Peek())
            {
                Next();
                switch (r)
                {
                    case '\r':
                        // Ignore.
                        break;

                    case '\n':
                        indent = 0;
                        newLines++;
                        break;

                    case ' ':
                        indent++;
                        break;

                    // This only works for \t at the beginning of lines, but we strip it everywhere else
                    // anyway.
                    case '\t':
                        indent += 8;
                        break;
                }
            }
            return (newLines, indent);
        }

        // lexUntilNewLine consumes all text until the end of the line and returns the
        // number of newlines after that as well as the next indent.
        private (string Text, int Blanks, int Indent) LexUntilNewline()
        {
            // Compute 'text'.
            int start = _byteNo;
            int lastNonSpace = start;
            for (int r = Peek(); r != LexEOF && r != '\n'; r = Peek())
            {
                Next();
                if (!IsHorizontalWhitespace(r))
                {
                    lastNonSpace = _byteNo;
                }
            }
            // Trim whitespace off the end.
            string text = InputString(start, lastNonSpace);

            // Consume the '\n' and following indent.
            (int newLines, int indent) = LexWhitespace();
            int blanks = 0;
            if (newLines > 0)
            {
                blanks = newLines - 1;
            }
            return (text, blanks, indent);
        }

        private enum NumLexState
        {
            NumBegin,
            NumAfterZero,
            NumAfterOneToNine,
            NumAfterIntUnderscore,
            NumAfterDot,
            NumAfterDigit,
            NumAfterFracUnderscore,
            NumAfterE,
            NumAfterExpSign,
            NumAfterExpDigit,
            NumAfterExpUnderscore,
        }

        // lexNumber will consume a number and emit a token.  It is assumed
        // that the next rune to be served by the lexer will be a leading digit.
        // Negative numbers are lexed as a unary operator applied to a numeric literal.
        private void LexNumber()
        {
            StringBuilder cb = new();
            NumLexState state = NumLexState.NumBegin;

            while (true)
            {
                int r = Peek();
                bool done = false;
                switch (state)
                {
                    case NumLexState.NumBegin:
                        if (r == '0')
                        {
                            state = NumLexState.NumAfterZero;
                        }
                        else if (r >= '1' && r <= '9')
                        {
                            state = NumLexState.NumAfterOneToNine;
                        }
                        else
                        {
                            // The caller should ensure the first rune is a digit.
                            throw new InvalidOperationException("Couldn't lex number");
                        }
                        break;
                    case NumLexState.NumAfterZero:
                        switch (r)
                        {
                            case '.':
                                state = NumLexState.NumAfterDot;
                                break;
                            case 'e':
                            case 'E':
                                state = NumLexState.NumAfterE;
                                break;
                            case '_':
                                throw MakeStaticErrorPoint("Couldn't lex number, _ not allowed after leading 0", CurrentLocation());
                            default:
                                done = true;
                                break;
                        }
                        break;
                    case NumLexState.NumAfterOneToNine:
                        if (r == '.')
                        {
                            state = NumLexState.NumAfterDot;
                        }
                        else if (r == 'e' || r == 'E')
                        {
                            state = NumLexState.NumAfterE;
                        }
                        else if (r >= '0' && r <= '9')
                        {
                            state = NumLexState.NumAfterOneToNine;
                        }
                        else if (r == '_')
                        {
                            state = NumLexState.NumAfterIntUnderscore;
                        }
                        else
                        {
                            done = true;
                        }
                        break;
                    case NumLexState.NumAfterIntUnderscore:
                        // The only valid transition out of _ is to a digit.
                        if (r >= '0' && r <= '9')
                        {
                            state = NumLexState.NumAfterOneToNine;
                        }
                        else
                        {
                            throw MakeStaticErrorPoint("Couldn't lex number, junk after '_': " + GoUtf8.QuoteRuneToASCII(r), CurrentLocation());
                        }
                        break;
                    case NumLexState.NumAfterDot:
                        if (r >= '0' && r <= '9')
                        {
                            state = NumLexState.NumAfterDigit;
                        }
                        else
                        {
                            throw MakeStaticErrorPoint("Couldn't lex number, junk after decimal point: " + GoUtf8.QuoteRuneToASCII(r), CurrentLocation());
                        }
                        break;
                    case NumLexState.NumAfterDigit:
                        if (r == 'e' || r == 'E')
                        {
                            state = NumLexState.NumAfterE;
                        }
                        else if (r >= '0' && r <= '9')
                        {
                            state = NumLexState.NumAfterDigit;
                        }
                        else if (r == '_')
                        {
                            state = NumLexState.NumAfterFracUnderscore;
                        }
                        else
                        {
                            done = true;
                        }
                        break;
                    case NumLexState.NumAfterFracUnderscore:
                        // The only valid transition out of _ is to a digit.
                        if (r >= '0' && r <= '9')
                        {
                            state = NumLexState.NumAfterDigit;
                        }
                        else
                        {
                            throw MakeStaticErrorPoint("Couldn't lex number, junk after '_': " + GoUtf8.QuoteRuneToASCII(r), CurrentLocation());
                        }
                        break;
                    case NumLexState.NumAfterE:
                        if (r == '+' || r == '-')
                        {
                            state = NumLexState.NumAfterExpSign;
                        }
                        else if (r >= '0' && r <= '9')
                        {
                            state = NumLexState.NumAfterExpDigit;
                        }
                        else
                        {
                            throw MakeStaticErrorPoint("Couldn't lex number, junk after 'E': " + GoUtf8.QuoteRuneToASCII(r), CurrentLocation());
                        }
                        break;
                    case NumLexState.NumAfterExpSign:
                        if (r >= '0' && r <= '9')
                        {
                            state = NumLexState.NumAfterExpDigit;
                        }
                        else
                        {
                            throw MakeStaticErrorPoint("Couldn't lex number, junk after exponent sign: " + GoUtf8.QuoteRuneToASCII(r), CurrentLocation());
                        }
                        break;
                    case NumLexState.NumAfterExpDigit:
                        if (r >= '0' && r <= '9')
                        {
                            state = NumLexState.NumAfterExpDigit;
                        }
                        else if (r == '_')
                        {
                            state = NumLexState.NumAfterExpUnderscore;
                        }
                        else
                        {
                            done = true;
                        }
                        break;
                    case NumLexState.NumAfterExpUnderscore:
                        // The only valid transition out of _ is to a digit.
                        if (r >= '0' && r <= '9')
                        {
                            state = NumLexState.NumAfterExpDigit;
                        }
                        else
                        {
                            throw MakeStaticErrorPoint("Couldn't lex number, junk after '_': " + GoUtf8.QuoteRuneToASCII(r), CurrentLocation());
                        }
                        break;
                }
                if (done)
                {
                    break;
                }

                if (r != '_')
                {
                    cb.Append((char)r);
                }
                Next();
            }

            EmitFullToken(TokenKind.Number, cb.ToString(), "", "");
            ResetTokenStart();
        }

        // getTokenKindFromID will return a keyword if the identifier string is
        // recognised as one, otherwise it will return Identifier.
        private static TokenKind GetTokenKindFromID(string str) => str switch
        {
            "assert" => TokenKind.Assert,
            "else" => TokenKind.Else,
            "error" => TokenKind.Error,
            "false" => TokenKind.False,
            "for" => TokenKind.For,
            "function" => TokenKind.Function,
            "if" => TokenKind.If,
            "import" => TokenKind.Import,
            "importstr" => TokenKind.ImportStr,
            "importbin" => TokenKind.ImportBin,
            "in" => TokenKind.In,
            "local" => TokenKind.Local,
            "null" => TokenKind.NullLit,
            "self" => TokenKind.Self,
            "super" => TokenKind.Super,
            "tailstrict" => TokenKind.TailStrict,
            "then" => TokenKind.Then,
            "true" => TokenKind.True,
            // Not a keyword, assume it is an identifier
            _ => TokenKind.Identifier,
        };

        // IsValidIdentifier is true if the string could be a valid identifier.
        internal static bool IsValidIdentifier(string str)
        {
            if (str.Length == 0)
            {
                return false;
            }
            // Identifier runes are ASCII, so any non-ASCII char (including surrogates) fails either way.
            for (int i = 0; i < str.Length; i++)
            {
                if (i == 0)
                {
                    if (!IsIdentifierFirst(str[i]))
                    {
                        return false;
                    }
                }
                else if (!IsIdentifier(str[i]))
                {
                    return false;
                }
            }
            return GetTokenKindFromID(str) == TokenKind.Identifier;
        }

        // lexIdentifier will consume an identifier and emit a token.  It is assumed
        // that the next rune to be served by the lexer will not be a leading digit.
        // This may emit a keyword or an identifier.
        private void LexIdentifier()
        {
            int r = Peek();
            if (!IsIdentifierFirst(r))
            {
                throw new InvalidOperationException("Unexpected character in lexIdentifier");
            }
            for (; r != LexEOF; r = Peek())
            {
                if (!IsIdentifier(r))
                {
                    break;
                }
                Next();
            }
            EmitToken(GetTokenKindFromID(InputString(_tokenStart, _byteNo)));
        }

        // lexSymbol will lex a token that starts with a symbol.  This could be a
        // C or C++ comment, block quote or an operator.  This function assumes that the next
        // rune to be served by the lexer will be the first rune of the new token.
        private void LexSymbol()
        {
            // freshLine is reset by next() so cache it here.
            bool freshLine = _freshLine;
            int r = Next();

            // Single line C++ style comment
            if (r == '#' || (r == '/' && Peek() == '/'))
            {
                (string comment, int blanks, int indent) = LexUntilNewline();
                FodderKind k = freshLine ? FodderKind.Paragraph : FodderKind.LineEnd;
                AddFodder(k, blanks, indent, new List<string> { (char)r + comment });
                return;
            }

            // C style comment (could be interstitial or paragraph comment)
            if (r == '/' && Peek() == '*')
            {
                int margin = _byteNo - _lineStart - 1;
                Location commentStartLoc = _tokenStartLoc;

                Next(); // consume the initial '*'
                for (r = Next(); r != '*' || Peek() != '/'; r = Next())
                {
                    if (r == LexEOF)
                    {
                        throw MakeStaticErrorPoint("Multi-line comment has no terminating */", commentStartLoc);
                    }
                }

                Next(); // Consume trailing '/'
                // Includes the "/*" and "*/".
                string comment = InputString(_tokenStart, _byteNo);

                (int newLinesAfter, int indentAfter) = LexWhitespace();
                if (comment.IndexOf('\n') < 0)
                {
                    AddFodder(FodderKind.Interstitial, 0, 0, new List<string> { comment });
                    if (newLinesAfter > 0)
                    {
                        AddFodder(FodderKind.LineEnd, newLinesAfter - 1, indentAfter, new List<string>());
                    }
                }
                else
                {
                    List<string> lines = LineSplit(comment, margin);
                    if (lines[0][0] != '/')
                    {
                        throw new InvalidOperationException("Invalid parsing of C style comment [" + string.Join(" ", lines) + "]");
                    }
                    // Little hack to support FodderParagraphs with * down the LHS:
                    // Add a space to lines that start with a '*'
                    bool allStar = true;
                    foreach (string l in lines)
                    {
                        if (l.Length == 0 || l[0] != '*')
                        {
                            allStar = false;
                        }
                    }
                    if (allStar)
                    {
                        for (int i = 0; i < lines.Count; i++)
                        {
                            if (lines[i][0] == '*')
                            {
                                lines[i] = " " + lines[i];
                            }
                        }
                    }
                    if (newLinesAfter == 0)
                    {
                        // Ensure a line end after the paragraph.
                        newLinesAfter = 1;
                        indentAfter = 0;
                    }
                    AddFodderSafe(FodderKind.Paragraph, newLinesAfter - 1, indentAfter, lines);
                }
                return;
            }

            if (r == '|' && InputHasPrefix(_byteNo, "||"))
            {
                Location commentStartLoc = _tokenStartLoc;
                AcceptN(2); // Skip "||"

                bool chompTrailingNl = false;
                if (Peek() == '-')
                {
                    chompTrailingNl = true;
                    Next();
                }

                StringBuilder cb = new();

                // Skip whitespace
                for (r = Next(); r == ' ' || r == '\t' || r == '\r'; r = Next())
                {
                }

                // Skip \n
                if (r != '\n')
                {
                    throw MakeStaticErrorPoint("Text block requires new line after |||.", commentStartLoc);
                }

                // Process leading blank lines before calculating stringBlockIndent
                for (r = Peek(); r == '\n'; r = Peek())
                {
                    Next();
                    cb.Append('\n');
                }
                int numWhiteSpace = CheckWhitespace(_input, _byteNo, _input, _byteNo);
                byte[] stringBlockIndentBytes = new byte[numWhiteSpace];
                System.Array.Copy(_input, _byteNo, stringBlockIndentBytes, 0, numWhiteSpace);
                string stringBlockIndent = Encoding.ASCII.GetString(stringBlockIndentBytes);
                if (numWhiteSpace == 0)
                {
                    throw MakeStaticErrorPoint("Text block's first line must start with whitespace", commentStartLoc);
                }

                while (true)
                {
                    if (numWhiteSpace <= 0)
                    {
                        throw new InvalidOperationException("Unexpected value for numWhiteSpace");
                    }
                    AcceptN(numWhiteSpace);
                    for (r = Next(); r != '\n'; r = Next())
                    {
                        if (r == LexEOF)
                        {
                            throw MakeStaticErrorPoint("Unexpected EOF", commentStartLoc);
                        }
                        GoUtf8.AppendRune(cb, r);
                    }
                    cb.Append('\n');

                    // Skip any blank lines
                    for (r = Peek(); r == '\n'; r = Peek())
                    {
                        Next();
                        cb.Append('\n');
                    }

                    // Look at the next line
                    numWhiteSpace = CheckWhitespace(stringBlockIndentBytes, 0, _input, _byteNo);
                    if (numWhiteSpace == 0)
                    {
                        // End of the text block
                        StringBuilder stringBlockTermIndent = new();
                        for (r = Peek(); r == ' ' || r == '\t'; r = Peek())
                        {
                            Next();
                            stringBlockTermIndent.Append((char)r);
                        }
                        if (!InputHasPrefix(_byteNo, "|||"))
                        {
                            throw MakeStaticErrorPoint("Text block not terminated with |||", commentStartLoc);
                        }
                        AcceptN(3); // Skip '|||'

                        string str = cb.ToString();
                        if (chompTrailingNl)
                        {
                            str = str.Substring(0, str.Length - 1);
                        }

                        EmitFullToken(TokenKind.StringBlock, str, stringBlockIndent, stringBlockTermIndent.ToString());
                        ResetTokenStart();
                        return;
                    }
                }
            }

            // Assume any string of symbols is a single operator.
            for (r = Peek(); IsSymbol(r); r = Peek())
            {
                // Not allowed // in operators. Like Go, this checks the rune itself, so any '/' ends the operator.
                if (r == '/' && InputHasPrefix(_byteNo, "/"))
                {
                    break;
                }
                // Not allowed /* in operators
                if (r == '/' && InputHasPrefix(_byteNo, "*"))
                {
                    break;
                }
                // Not allowed ||| in operators (accounts for |||-)
                if (r == '|' && InputHasPrefix(_byteNo, "||"))
                {
                    break;
                }
                Next();
            }

            // Operators are not allowed to end with + - ~ ! unless they are one rune long.
            // So, wind it back if we need to, but stop at the first rune.
            // Like Go, r is read only once, so e.g. "==-" winds back to "=".
            r = _input[_byteNo - 1];
            for (; _byteNo > _tokenStart + 1; _byteNo--)
            {
                if (r == '+' || r == '-' || r == '~' || r == '!' || r == '$')
                {
                    continue;
                }
                break;
            }

            if (_byteNo - _tokenStart == 1 && _input[_tokenStart] == '$')
            {
                EmitToken(TokenKind.Dollar);
            }
            else
            {
                EmitToken(TokenKind.Operator);
            }
        }

        // Lex returns a list of tokens recognised in input.
        internal static List<Token> Lex(string diagnosticFilename, string importedFilename, string input) =>
            Lex(diagnosticFilename, importedFilename, GoUtf8.Utf8.GetBytes(input));

        // input is a Go string: raw bytes, possibly not UTF-8 (an imported file). Columns count its bytes.
        internal static List<Token> Lex(string diagnosticFilename, string importedFilename, byte[] input)
        {
            Lexer l = new(diagnosticFilename, importedFilename, input);
            l.Run();
            return l._tokens;
        }

        private void Run()
        {
            while (true)
            {
                (int newLines, int indent) = LexWhitespace();
                // If it's the end of the file, discard final whitespace.
                if (Peek() == LexEOF)
                {
                    Next();
                    ResetTokenStart();
                    break;
                }
                if (newLines > 0)
                {
                    // Otherwise store whitespace in fodder.
                    int blanks = newLines - 1;
                    AddFodder(FodderKind.LineEnd, blanks, indent, new List<string>());
                }
                ResetTokenStart(); // Don't include whitespace in actual token.
                int r = Peek();
                switch (r)
                {
                    case '{':
                        Next();
                        EmitToken(TokenKind.BraceL);
                        break;
                    case '}':
                        Next();
                        EmitToken(TokenKind.BraceR);
                        break;
                    case '[':
                        Next();
                        EmitToken(TokenKind.BracketL);
                        break;
                    case ']':
                        Next();
                        EmitToken(TokenKind.BracketR);
                        break;
                    case ',':
                        Next();
                        EmitToken(TokenKind.Comma);
                        break;
                    case '.':
                        Next();
                        EmitToken(TokenKind.Dot);
                        break;
                    case '(':
                        Next();
                        EmitToken(TokenKind.ParenL);
                        break;
                    case ')':
                        Next();
                        EmitToken(TokenKind.ParenR);
                        break;
                    case ';':
                        Next();
                        EmitToken(TokenKind.Semicolon);
                        break;

                    case '0':
                    case '1':
                    case '2':
                    case '3':
                    case '4':
                    case '5':
                    case '6':
                    case '7':
                    case '8':
                    case '9':
                        LexNumber();
                        break;

                    // String literals
                    case '"':
                    case '\'':
                        LexQuotedString(r, r == '"' ? TokenKind.StringDouble : TokenKind.StringSingle);
                        break;

                    case '@':
                        LexVerbatimString();
                        break;

                    default:
                        if (IsIdentifierFirst(r))
                        {
                            LexIdentifier();
                        }
                        else if (IsSymbol(r) || r == '#')
                        {
                            LexSymbol();
                        }
                        else
                        {
                            throw MakeStaticErrorPoint("Could not lex the character " + GoUtf8.QuoteRuneToASCII(r), CurrentLocation());
                        }
                        break;
                }
            }

            // We are currently at the EOF.  Emit a special token to capture any trailing fodder
            EmitToken(TokenKind.EndOfFile);
        }

        private void LexQuotedString(int quote, TokenKind kind)
        {
            Location stringStartLoc = CurrentLocation();
            Next();
            for (int r = Next(); ; r = Next())
            {
                if (r == LexEOF)
                {
                    throw MakeStaticErrorPoint("Unterminated String", stringStartLoc);
                }
                if (r == quote)
                {
                    // Don't include the quotes in the token data
                    EmitFullToken(kind, InputString(_tokenStart + 1, _byteNo - 1), "", "");
                    ResetTokenStart();
                    break;
                }
                if (r == '\\' && Peek() != LexEOF)
                {
                    Next();
                }
            }
        }

        private void LexVerbatimString()
        {
            Location stringStartLoc = CurrentLocation();
            Next();
            // Verbatim string literals.
            // ' and " quoting is interpreted here, unlike non-verbatim strings
            // where it is done later by jsonnet_string_unescape.
            StringBuilder data = new();
            int quot = Next();
            TokenKind kind;
            if (quot == '"')
            {
                kind = TokenKind.VerbatimStringDouble;
            }
            else if (quot == '\'')
            {
                kind = TokenKind.VerbatimStringSingle;
            }
            else
            {
                throw MakeStaticErrorPoint(
                    "Couldn't lex verbatim string, junk after '@': " + quot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    stringStartLoc);
            }
            for (int r = Next(); ; r = Next())
            {
                if (r == LexEOF)
                {
                    throw MakeStaticErrorPoint("Unterminated String", stringStartLoc);
                }
                else if (r == quot)
                {
                    if (Peek() == quot)
                    {
                        Next();
                        GoUtf8.AppendRune(data, r);
                    }
                    else
                    {
                        EmitFullToken(kind, data.ToString(), "", "");
                        ResetTokenStart();
                        break;
                    }
                }
                else
                {
                    GoUtf8.AppendRune(data, r);
                }
            }
        }
    }
}
