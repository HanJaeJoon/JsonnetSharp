// Ports go-jsonnet internal/parser/lexer_test.go.

using JsonnetSharp.Ast;
using Assert = Xunit.Assert;

namespace JsonnetSharp.Tests
{
    public class LexerTests
    {
        private static Token T(TokenKind kind, string data = "") => new() { Kind = kind, Data = data };

        private static Token Block(string data, string indent, string termIndent) => new()
        {
            Kind = TokenKind.StringBlock,
            Data = data,
            StringBlockIndent = indent,
            StringBlockTermIndent = termIndent,
        };

        private static Token Eof(params FodderElement[] fodder) => new()
        {
            Kind = TokenKind.EndOfFile,
            Fodder = new List<FodderElement>(fodder),
        };

        private static FodderElement F(FodderKind kind, params string[] comment) => new(kind, 0, 0, new List<string>(comment));

        private static bool FodderEqual(List<FodderElement>? f1, List<FodderElement>? f2)
        {
            int n1 = f1?.Count ?? 0;
            int n2 = f2?.Count ?? 0;
            if (n1 != n2)
            {
                return false;
            }
            for (int i = 0; i < n1; i++)
            {
                FodderElement a = f1![i];
                FodderElement b = f2![i];
                if (a.Kind != b.Kind || a.Blanks != b.Blanks || a.Indent != b.Indent || !a.Comment.SequenceEqual(b.Comment))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TokensEqual(List<Token> ts1, List<Token> ts2)
        {
            if (ts1.Count != ts2.Count)
            {
                return false;
            }
            for (int i = 0; i < ts1.Count; i++)
            {
                Token t1 = ts1[i];
                Token t2 = ts2[i];
                if (t1.Kind != t2.Kind || t1.Data != t2.Data || !FodderEqual(t1.Fodder, t2.Fodder)
                    || t1.StringBlockIndent != t2.StringBlockIndent || t1.StringBlockTermIndent != t2.StringBlockTermIndent)
                {
                    return false;
                }
            }
            return true;
        }

        private static void SingleTest(string input, string expectedError, params Token[] expected)
        {
            List<Token> testTokens = new(expected);
            if (testTokens.Count == 0 || testTokens[testTokens.Count - 1].Kind != TokenKind.EndOfFile)
            {
                testTokens.Add(T(TokenKind.EndOfFile));
            }
            List<Token>? tokens = null;
            string errString = "";
            try
            {
                tokens = Lexer.Lex("snippet", "", input);
            }
            catch (StaticError err)
            {
                errString = err.Error();
            }
            Assert.Equal(expectedError, errString);
            if (tokens != null)
            {
                Assert.True(
                    TokensEqual(tokens, testTokens),
                    "got " + string.Join(", ", tokens) + " expected " + string.Join(", ", testTokens));
            }
        }

        [Fact] public void Empty() => SingleTest("", "");

        [Fact] public void Whitespace() => SingleTest("  \t\n\r\r\n", "");

        [Fact] public void BraceL() => SingleTest("{", "", T(TokenKind.BraceL, "{"));

        [Fact] public void BraceR() => SingleTest("}", "", T(TokenKind.BraceR, "}"));

        [Fact] public void BracketL() => SingleTest("[", "", T(TokenKind.BracketL, "["));

        [Fact] public void BracketR() => SingleTest("]", "", T(TokenKind.BracketR, "]"));

        [Fact] public void Colon() => SingleTest(":", "", T(TokenKind.Operator, ":"));

        [Fact] public void Colon2() => SingleTest("::", "", T(TokenKind.Operator, "::"));

        [Fact] public void Colon3() => SingleTest(":::", "", T(TokenKind.Operator, ":::"));

        [Fact] public void Arrowright() => SingleTest("->", "", T(TokenKind.Operator, "->"));

        [Fact] public void Lessthanminus() => SingleTest("<-", "", T(TokenKind.Operator, "<"), T(TokenKind.Operator, "-"));

        [Fact] public void Comma() => SingleTest(",", "", T(TokenKind.Comma, ","));

        [Fact] public void Dollar() => SingleTest("$", "", T(TokenKind.Dollar, "$"));

        [Fact] public void Dot() => SingleTest(".", "", T(TokenKind.Dot, "."));

        [Fact] public void ParenL() => SingleTest("(", "", T(TokenKind.ParenL, "("));

        [Fact] public void ParenR() => SingleTest(")", "", T(TokenKind.ParenR, ")"));

        [Fact] public void Semicolon() => SingleTest(";", "", T(TokenKind.Semicolon, ";"));

        [Fact] public void Not1() => SingleTest("!", "", T(TokenKind.Operator, "!"));

        [Fact] public void Not2() => SingleTest("! ", "", T(TokenKind.Operator, "!"));

        [Fact] public void Notequal() => SingleTest("!=", "", T(TokenKind.Operator, "!="));

        [Fact] public void Tilde() => SingleTest("~", "", T(TokenKind.Operator, "~"));

        [Fact] public void Plus() => SingleTest("+", "", T(TokenKind.Operator, "+"));

        [Fact] public void Minus() => SingleTest("-", "", T(TokenKind.Operator, "-"));

        // go-jsonnet reads the last operator rune only once while winding back, so "==-" lexes as "=" "=" "-".
        [Fact] public void OperatorWindBackQuirk() =>
            SingleTest("==-", "", T(TokenKind.Operator, "="), T(TokenKind.Operator, "="), T(TokenKind.Operator, "-"));

        public static TheoryData<string, string, string[]> NumberCases => new()
        {
            { "0", "", new[] { "0" } },
            { "1", "", new[] { "1" } },
            { "1.0", "", new[] { "1.0" } },
            { "0.10", "", new[] { "0.10" } },
            { "0e100", "", new[] { "0e100" } },
            { "1e100", "", new[] { "1e100" } },
            { "1.1e100", "", new[] { "1.1e100" } },
            { "1.1e-100", "", new[] { "1.1e-100" } },
            { "1.1e+100", "", new[] { "1.1e+100" } },
            { "0100", "", new[] { "0", "100" } },
            { "10+10", "", new[] { "10", "+", "10" } },
            { "1.2.3.4", "", new[] { "1.2", ".", "3.4" } },
            { "1e2.34", "", new[] { "1e2", ".", "34" } },
            { "1.+3", "snippet:1:3 Couldn't lex number, junk after decimal point: '+'", new string[0] },
            { "1e!", "snippet:1:3 Couldn't lex number, junk after 'E': '!'", new string[0] },
            { "1e+!", "snippet:1:4 Couldn't lex number, junk after exponent sign: '!'", new string[0] },
            { "1.", "snippet:1:3 Couldn't lex number, junk after decimal point: '\\ufffd'", new string[0] },

            // TestNumberSeparators
            { "123_456", "", new[] { "123456" } },
            { "1_750_000", "", new[] { "1750000" } },
            { "1_2_3", "", new[] { "123" } },
            { "3.141_592", "", new[] { "3.141592" } },
            { "01_100", "", new[] { "0", "1100" } },
            { "1_200.0", "", new[] { "1200.0" } },
            { "0e1_01", "", new[] { "0e101" } },
            { "10_10e3", "", new[] { "1010e3" } },
            { "2_3e1_2", "", new[] { "23e12" } },
            { "1.1_2e100", "", new[] { "1.12e100" } },
            { "1.1e-10_1", "", new[] { "1.1e-101" } },
            { "9.109_383_56e-31", "", new[] { "9.10938356e-31" } },
            { "1_2.3_4.5_6.7_8", "", new[] { "12.34", ".", "56.78" } },
            { "1e2_3e4", "", new[] { "1e23", "e4" } },
            { "0_5", "snippet:1:2 Couldn't lex number, _ not allowed after leading 0", new string[0] },
            { "123456_!", "snippet:1:8 Couldn't lex number, junk after '_': '!'", new string[0] },
            { "123__456", "snippet:1:5 Couldn't lex number, junk after '_': '_'", new string[0] },
            { "1_200_.0", "snippet:1:7 Couldn't lex number, junk after '_': '.'", new string[0] },
            { "1_200._0", "snippet:1:7 Couldn't lex number, junk after decimal point: '_'", new string[0] },
            { "1_200_e2", "snippet:1:7 Couldn't lex number, junk after '_': 'e'", new string[0] },
            { "1_200e_2", "snippet:1:7 Couldn't lex number, junk after 'E': '_'", new string[0] },
            { "200e-_2", "snippet:1:6 Couldn't lex number, junk after exponent sign: '_'", new string[0] },
            { "200e+_2", "snippet:1:6 Couldn't lex number, junk after exponent sign: '_'", new string[0] },
        };

        [Theory]
        [MemberData(nameof(NumberCases))]
        public void Number(string input, string err, string[] data)
        {
            Token[] expected = data.Select(d => d switch
            {
                "." => T(TokenKind.Dot, d),
                "+" => T(TokenKind.Operator, d),
                _ when char.IsLetter(d[0]) => T(TokenKind.Identifier, d),
                _ => T(TokenKind.Number, d),
            }).ToArray();
            SingleTest(input, err, expected);
        }

        [Fact] public void Doublestring1() => SingleTest("\"hi\"", "", T(TokenKind.StringDouble, "hi"));

        [Fact] public void Doublestring2() => SingleTest("\"hi\n\"", "", T(TokenKind.StringDouble, "hi\n"));

        [Fact] public void Doublestring3() => SingleTest("\"hi\\\"\"", "", T(TokenKind.StringDouble, "hi\\\""));

        [Fact] public void Doublestring4() => SingleTest("\"hi\\\n\"", "", T(TokenKind.StringDouble, "hi\\\n"));

        [Fact] public void Doublestring5() => SingleTest("\"hi", "snippet:1:1 Unterminated String");

        [Fact] public void Singlestring1() => SingleTest("'hi'", "", T(TokenKind.StringSingle, "hi"));

        [Fact] public void Singlestring2() => SingleTest("'hi\n'", "", T(TokenKind.StringSingle, "hi\n"));

        [Fact] public void Singlestring3() => SingleTest("'hi\\''", "", T(TokenKind.StringSingle, "hi\\'"));

        [Fact] public void Singlestring4() => SingleTest("'hi\\\n'", "", T(TokenKind.StringSingle, "hi\\\n"));

        [Fact] public void Singlestring5() => SingleTest("'hi", "snippet:1:1 Unterminated String");

        [Theory]
        [InlineData("assert", "Assert")]
        [InlineData("else", "Else")]
        [InlineData("error", "Error")]
        [InlineData("false", "False")]
        [InlineData("for", "For")]
        [InlineData("function", "Function")]
        [InlineData("if", "If")]
        [InlineData("import", "Import")]
        [InlineData("importstr", "ImportStr")]
        [InlineData("importbin", "ImportBin")]
        [InlineData("in", "In")]
        [InlineData("local", "Local")]
        [InlineData("null", "NullLit")]
        [InlineData("self", "Self")]
        [InlineData("super", "Super")]
        [InlineData("tailstrict", "TailStrict")]
        [InlineData("then", "Then")]
        [InlineData("true", "True")]
        public void Keyword(string input, string kind) => SingleTest(input, "", T((TokenKind)Enum.Parse(typeof(TokenKind), kind), input));

        [Fact] public void Identifier() => SingleTest("foobar123", "", T(TokenKind.Identifier, "foobar123"));

        [Fact] public void Identifiers() =>
            SingleTest("foo bar123", "", T(TokenKind.Identifier, "foo"), T(TokenKind.Identifier, "bar123"));

        [Fact] public void IdentifierUnderscore() => SingleTest("_123", "", T(TokenKind.Identifier, "_123"));

        [Fact] public void CppComment() => SingleTest("// hi", "", Eof(F(FodderKind.Paragraph, "// hi")));

        [Fact] public void HashComment() => SingleTest("# hi", "", Eof(F(FodderKind.Paragraph, "# hi")));

        [Fact] public void CComment() => SingleTest("/* hi */", "", Eof(F(FodderKind.Interstitial, "/* hi */")));

        [Fact] public void CCommentTooShort() => SingleTest("/*/", "snippet:1:1 Multi-line comment has no terminating */");

        [Fact] public void CCommentMinimal() => SingleTest("/**/", "", Eof(F(FodderKind.Interstitial, "/**/")));

        [Fact] public void CCommentJustSlash() => SingleTest("/*/*/", "", Eof(F(FodderKind.Interstitial, "/*/*/")));

        [Fact] public void CCommentSpaceSlash() => SingleTest("/* /*/", "", Eof(F(FodderKind.Interstitial, "/* /*/")));

        [Fact] public void CCommentManyLines() =>
            SingleTest("/*\n\n*/", "", Eof(F(FodderKind.LineEnd), F(FodderKind.Paragraph, "/*", "", "*/")));

        [Fact] public void CCommentNoTerm() => SingleTest("/* hi", "snippet:1:1 Multi-line comment has no terminating */");

        [Fact] public void BlockStringSpaces() =>
            SingleTest("|||\n  test\n    more\n  |||\n    foo\n|||", "", Block("test\n  more\n|||\n  foo\n", "  ", ""));

        [Fact] public void BlockStringTabs() =>
            SingleTest("|||\n\ttest\n\t  more\n\t|||\n\t  foo\n|||", "", Block("test\n  more\n|||\n  foo\n", "\t", ""));

        [Fact] public void BlockStringMixed() =>
            SingleTest(
                "|||\n\t  \ttest\n\t  \t  more\n\t  \t|||\n\t  \t  foo\n|||",
                "",
                Block("test\n  more\n|||\n  foo\n", "\t  \t", ""));

        [Fact] public void BlockStringBlanks() =>
            SingleTest(
                "|||\n\n  test\n\n\n    more\n  |||\n    foo\n|||",
                "",
                Block("\ntest\n\n\n  more\n|||\n  foo\n", "  ", ""));

        [Fact] public void BlockStringChomp() => SingleTest("|||-\n  a\n  b\n |||", "", Block("a\nb", "  ", " "));

        [Fact] public void BlockStringBadIndent() => SingleTest("|||\n  test\n foo\n|||", "snippet:1:1 Text block not terminated with |||");

        [Fact] public void BlockStringEof() => SingleTest("|||\n  test", "snippet:1:1 Unexpected EOF");

        [Fact] public void BlockStringNotTerm() => SingleTest("|||\n  test\n", "snippet:1:1 Text block not terminated with |||");

        [Fact] public void BlockstringNoWs() => SingleTest("|||\ntest\n|||", "snippet:1:1 Text block's first line must start with whitespace");

        [Fact] public void VerbatimString1() => SingleTest("@\"\"", "", T(TokenKind.VerbatimStringDouble, ""));

        [Fact] public void VerbatimString2() => SingleTest("@''", "", T(TokenKind.VerbatimStringSingle, ""));

        [Fact] public void VerbatimString3() => SingleTest("@\"\"\"\"", "", T(TokenKind.VerbatimStringDouble, "\""));

        [Fact] public void VerbatimString4() => SingleTest("@''''", "", T(TokenKind.VerbatimStringSingle, "'"));

        [Fact] public void VerbatimString5() => SingleTest("@\"\\n\"", "", T(TokenKind.VerbatimStringDouble, "\\n"));

        [Fact] public void VerbatimString6() => SingleTest("@\"''\"", "", T(TokenKind.VerbatimStringDouble, "''"));

        [Fact] public void VerbatimStringUnterminated() => SingleTest("@\"blah blah", "snippet:1:1 Unterminated String");

        [Fact] public void VerbatimStringJunk() => SingleTest("@blah blah", "snippet:1:1 Couldn't lex verbatim string, junk after '@': 98");

        [Theory]
        [InlineData("*")]
        [InlineData("/")]
        [InlineData("%")]
        [InlineData("&")]
        [InlineData("|")]
        [InlineData("^")]
        [InlineData("=")]
        [InlineData("<")]
        [InlineData(">")]
        [InlineData(">==|")]
        public void Op(string input) => SingleTest(input, "", T(TokenKind.Operator, input));

        [Fact] public void Junk() => SingleTest("\U0001F4A9", "snippet:1:1 Could not lex the character '\\U0001f4a9'");

        // Columns are UTF-8 byte offsets, as in Go.
        [Fact] public void JunkAfterMultibyte() => SingleTest("\"\u00e9\" + \u00e9x", "snippet:1:8 Could not lex the character '\\u00e9'");

        [Fact]
        public void TokenLocations()
        {
            List<Token> tokens = Lexer.Lex("f", "", "a\n  \"\u00e9\u00e9\" + 1");
            Assert.Equal("f:1:1-2", tokens[0].Loc.ToString());
            Assert.Equal("f:2:3-9", tokens[1].Loc.ToString());
        }
    }
}
