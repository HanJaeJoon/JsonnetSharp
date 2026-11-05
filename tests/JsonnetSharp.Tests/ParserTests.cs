// Ports go-jsonnet internal/parser/parser_test.go and internal/program/static_analyzer_test.go,
// plus desugarer/analyzer error checks taken from the reference go-jsonnet 0.22.0 binary.

using JsonnetSharp.Ast;
using Assert = Xunit.Assert;

namespace JsonnetSharp.Tests
{
    public class ParserTests
    {
        public static TheoryData<string> Tests => new()
        {
            "true",
            "1",
            "1.2e3",
            "!true",
            "null",

            "$.foo.bar",
            "self.foo.bar",
            "super.foo.bar",
            "super[1]",
            "error \"Error!\"",

            "\"world\"",
            "'world'",
            "|||\n   world\n|||",

            "foo(bar)",
            "foo(bar,)",
            "foo(bar) tailstrict",
            "foo(bar=42)",
            "foo(bar=42,)",
            "foo(bar, baz=42)",
            "foo.bar",
            "foo[bar]",

            "true || false",
            "0 && 1 || 0",
            "0 && (1 || 0)",

            "function(x) x",
            "function(x=5) x",
            "function(x, y=5) x",
            "function(a=5, b) [a, b]",

            "local foo = \"bar\"; foo",
            "local foo(bar) = bar; foo(1)",
            "{ local foo = \"bar\", baz: 1}",
            "{ local foo(bar) = bar, baz: foo(1)}",

            "{ foo(bar, baz): bar+baz }",

            "{ [\"foo\" + \"bar\"]: 3 }",
            "{ [\"field\" + x]: x for x in [1, 2, 3] }",
            "{ local y = x, [\"field\" + x]: x for x in [1, 2, 3] }",
            "{ [\"field\" + x]: x for x in [1, 2, 3] if x <= 2 }",
            "{ [\"field\" + x + y]: x + y for x in [1, 2, 3] if x <= 2 for y in [4, 5, 6]}",

            "[]",
            "[a, b, c]",
            "[x for x in [1,2,3] ]",
            "[x for x in [1,2,3] if x <= 2]",
            "[x+y for x in [1,2,3] if x <= 2 for y in [4, 5, 6]]",

            "{}",
            "{ hello: \"world\" }",
            "{ hello +: \"world\" }",
            "{\n  hello: \"world\",\n\t\"name\":: joe,\n\t'mood'::: \"happy\",\n\t|||\n\t  key type\n|||: \"block\",\n}",

            "assert true: 'woah!'; true",
            "{ assert true: 'woah!', foo: bar }",

            "if n > 1 then 'foos' else 'foo'",

            "local foo = function(x) x + 1; true",
            "local foo = function(x=5) x + 1; true",
            "local foo = function(x=5) x + 1; x(x=3)",

            "import 'foo.jsonnet'",
            "importstr 'foo.text'",
            "importbin 'foo.bin'",

            "{a: b} + {c: d}",
            "{a: b}{c: d}",

            // no colons
            "[][0]",
            // one colon
            "[][:]",
            "[][1:]",
            "[][:1]",
            "[][1:2]",
            // two colons
            "[][::]",
            "[][1::]",
            "[][:1:]",
            "[][::1]",
            "[][1:1:]",
            "[][:1:1]",
            "[][1::1]",
            "[][1:1:1]",

            "a in b",
            "{ x: if \"opt\" in super then \"x\" else \"y\" }",
        };

        [Theory]
        [MemberData(nameof(Tests))]
        public void Parse(string s)
        {
            List<Token> tokens = Lexer.Lex("test", "", s);
            Parser.Parse(tokens);
        }

        public static TheoryData<string, string> ErrorTests => new()
        {
            { ",", "test:1:1-2 Unexpected: \",\" while parsing terminal" },
            { "function(a, b c)", "test:1:15-16 Expected a comma before next function parameter, got (IDENTIFIER, \"c\")" },
            { "function(a, 1)", "test:1:13-14 Expected token IDENTIFIER but got (NUMBER, \"1\") while parsing parameter" },
            { "function(,)", "test:1:10-11 Expected token IDENTIFIER but got \",\" while parsing parameter" },
            { "function(a=)", "test:1:12-13 Unexpected: \")\" while parsing terminal" },
            { "function(a=,)", "test:1:12-13 Unexpected: \",\" while parsing terminal" },
            { "a b", "test:1:3-4 Did not expect: (IDENTIFIER, \"b\")" },
            { "foo(a, bar(a b))", "test:1:14-15 Expected a comma before next function argument, got (IDENTIFIER, \"b\")" },

            { "local", "test:1:6 Expected token IDENTIFIER but got end of file" },
            { "local foo = 1, foo = 2; true", "test:1:16-19 Duplicate local var: foo" },
            { "local foo(a b) = a; true", "test:1:13-14 Expected a comma before next function parameter, got (IDENTIFIER, \"b\")" },
            { "local foo(a): a; true", "test:1:13-14 Expected operator = but got (OPERATOR, \":\")" },
            { "local foo(a) = bar(a b); true", "test:1:22-23 Expected a comma before next function argument, got (IDENTIFIER, \"b\")" },
            { "local foo: 1; true", "test:1:10-11 Expected operator = but got (OPERATOR, \":\")" },
            { "local foo = bar(a b); true", "test:1:19-20 Expected a comma before next function argument, got (IDENTIFIER, \"b\")" },

            { "{a b}", "test:1:4-5 Expected token OPERATOR but got (IDENTIFIER, \"b\")" },
            { "{a = b}", "test:1:4-5 Expected one of :, ::, :::, +:, +::, +:::, got: =" },
            { "{a :::: b}", "test:1:4-8 Expected one of :, ::, :::, +:, +::, +:::, got: ::::" },

            { "{assert x for x in [1, 2, 3]}", "test:1:11-14 Object comprehension cannot have asserts" },
            { "{['foo' + x]: true, [x]: x for x in [1, 2, 3]}", "test:1:28-31 Object comprehension can only have one field" },
            { "{foo: x for x in [1, 2, 3]}", "test:1:9-12 Object comprehensions can only have [e] fields" },
            { "{[x]:: true for x in [1, 2, 3]}", "test:1:13-16 Object comprehensions cannot have hidden fields" },
            { "{[x]: true for 1 in [1, 2, 3]}", "test:1:16-17 Expected token IDENTIFIER but got (NUMBER, \"1\")" },
            { "{[x]: true for x at [1, 2, 3]}", "test:1:18-20 Expected token in but got (IDENTIFIER, \"at\")" },
            { "{[x]: true for x in [1, 2 3]}", "test:1:27-28 Expected a comma before next array element" },
            { "{[x]: true for x in [1, 2, 3] if (a b)}", "test:1:37-38 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "{[x]: true for x in [1, 2, 3] if a b}", "test:1:36-37 Expected for, if or \"}\" after for clause, got: (IDENTIFIER, \"b\")" },

            { "{a: b c:d}", "test:1:7-8 Expected a comma before next field" },

            { "{[(x y)]: z}", "test:1:6-7 Expected token \")\" but got (IDENTIFIER, \"y\")" },
            { "{[x y]: z}", "test:1:5-6 Expected token \"]\" but got (IDENTIFIER, \"y\")" },

            { "{foo(x y): z}", "test:1:8-9 Expected a comma before next method parameter, got (IDENTIFIER, \"y\")" },
            { "{foo(x)+: z}", "test:1:2-5 Cannot use +: syntax sugar in a method: foo" },
            { "{foo: 1, foo: 2}", "test:1:10-13 Duplicate field: foo" },
            { "{foo: (1 2)}", "test:1:10-11 Expected token \")\" but got (NUMBER, \"2\")" },

            { "{local 1 = 3, true}", "test:1:8-9 Expected token IDENTIFIER but got (NUMBER, \"1\")" },
            { "{local foo = 1, local foo = 2, true}", "test:1:23-26 Duplicate local var: foo" },
            { "{local foo(a b) = 1, a: true}", "test:1:14-15 Expected a comma before next function parameter, got (IDENTIFIER, \"b\")" },
            { "{local foo(a): 1, a: true}", "test:1:14-15 Expected operator = but got (OPERATOR, \":\")" },
            { "{local foo(a) = (a b), a: true}", "test:1:20-21 Expected token \")\" but got (IDENTIFIER, \"b\")" },

            { "{assert (a b), a: true}", "test:1:12-13 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "{assert a: (a b), a: true}", "test:1:15-16 Expected token \")\" but got (IDENTIFIER, \"b\")" },

            { "{function(a, b) a+b: true}", "test:1:2-10 Unexpected: \"function\" while parsing field definition" },

            { "[(a b), 2, 3]", "test:1:5-6 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "[1, (a b), 2, 3]", "test:1:8-9 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "[a for b in [1 2 3]]", "test:1:16-17 Expected a comma before next array element" },

            { "for", "test:1:1-4 Unexpected: \"for\" while parsing terminal" },
            { "", "test:1:1 Unexpected end of file" },
            { "((a b))", "test:1:5-6 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "a.1", "test:1:3-4 Expected token IDENTIFIER but got (NUMBER, \"1\")" },
            { "super.1", "test:1:7-8 Expected token IDENTIFIER but got (NUMBER, \"1\")" },
            { "super[(a b)]", "test:1:10-11 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "super[a b]", "test:1:9-10 Expected token \"]\" but got (IDENTIFIER, \"b\")" },
            { "super", "test:1:1-6 Expected . or [ after super" },

            { "assert (a b); true", "test:1:11-12 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "assert a: (a b); true", "test:1:14-15 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "assert a: 'foo', true", "test:1:16-17 Expected token \";\" but got \",\"" },
            { "assert a: 'foo'; (a b)", "test:1:21-22 Expected token \")\" but got (IDENTIFIER, \"b\")" },

            { "error (a b)", "test:1:10-11 Expected token \")\" but got (IDENTIFIER, \"b\")" },

            { "if (a b) then c", "test:1:7-8 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "if a b c", "test:1:6-7 Expected token then but got (IDENTIFIER, \"b\")" },
            { "if a then (b c)", "test:1:14-15 Expected token \")\" but got (IDENTIFIER, \"c\")" },
            { "if a then b else (c d)", "test:1:21-22 Expected token \")\" but got (IDENTIFIER, \"d\")" },

            { "function(a) (a b)", "test:1:16-17 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "function a a", "test:1:10-11 Expected ( but got (IDENTIFIER, \"a\")" },

            { "import (a b)", "test:1:11-12 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "import (a+b)", "test:1:8-13 Computed imports are not allowed" },
            { "importstr (a b)", "test:1:14-15 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "importstr (a+b)", "test:1:11-16 Computed imports are not allowed" },
            { "importbin (a b)", "test:1:14-15 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "importbin (a+b)", "test:1:11-16 Computed imports are not allowed" },

            { "local a = b ()", "test:1:15 Expected , or ; but got end of file" },
            { "local a = b; (a b)", "test:1:17-18 Expected token \")\" but got (IDENTIFIER, \"b\")" },

            { "1+ <<", "test:1:4-6 Not a unary operator: <<" },
            { "-(a b)", "test:1:5-6 Expected token \")\" but got (IDENTIFIER, \"b\")" },
            { "1~2", "test:1:2-3 Not a binary operator: ~" },

            { "a[(b c)]", "test:1:6-7 Expected token \")\" but got (IDENTIFIER, \"c\")" },
            { "a[b c]", "test:1:5-6 Expected token \"]\" but got (IDENTIFIER, \"c\")" },
            { "a[]", "test:1:3-4 ast.Index requires an expression" },
            { "a[42:42:42:42]", "test:1:11-12 Invalid slice: too many colons" },
            { "a[42:42::42]", "test:1:8-10 Invalid slice: too many colons" },

            { "a{b c}", "test:1:5-6 Expected token OPERATOR but got (IDENTIFIER, \"c\")" },

            // Not in parser_test.go; checked against the reference binary.
            { "local x = 1 ==-1; x", "test:1:13-14 Not a binary operator: =" },
            { "import |||\n  a\n|||", "test:(1:8)-(3:4) Block string literals not allowed in imports" },
            { "\"\\u12\u00e9\u00e9\"", "test:1:1-11 test:1:1-11 Unicode escape sequence was malformed: \\u12" },
            { "'\\q'", "test:1:1-5 test:1:1-5 Unknown escape sequence in string literal: \\q" },
        };

        [Theory]
        [MemberData(nameof(ErrorTests))]
        public void ParseErrors(string input, string expected)
        {
            List<Token> tokens = Lexer.Lex("test", "", input);
            StaticError err = Assert.Throws<StaticError>(() => Parser.Parse(tokens));
            Assert.Equal(expected, err.Error());
        }

        // go-jsonnet treats any token whose data is ":" as a slice colon, string literals included.
        [Fact]
        public void SliceColonQuirk()
        {
            Node node = Parser.SnippetToRawAST("test", "", "[1,2,3][\":\"]").Node;
            Assert.IsType<Slice>(node);
        }

        [Fact]
        public void ContextNames()
        {
            Node node = Parser.SnippetToRawAST("test", "", "local f(x) = x + 1; { a: f(2) }").Node;
            Local local = Assert.IsType<Local>(node);
            Assert.Equal("$", local.Ctx);
            Assert.Equal("thunk <f> from <$>", local.Binds[0].Fun!.Ctx);
            Assert.Equal("function <f>", local.Binds[0].Fun!.Body.Ctx);
            Ast.Object obj = Assert.IsType<Ast.Object>(local.Body);
            Assert.Equal("object <anonymous>", obj.Fields[0].Expr2.Ctx);
            Apply apply = Assert.IsType<Apply>(obj.Fields[0].Expr2);
            Assert.Equal("thunk from <object <anonymous>>", apply.Arguments.Positional[0].Expr.Ctx);
        }

        [Fact]
        public void StringUnescape()
        {
            LocationRange loc = default;
            Assert.Equal("a\"\n\t\u00e9\U0001F4A9/", Parser.StringUnescape(loc, "a\\\"\\n\\t\\u00e9\\ud83d\\udca9\\/"));
            Assert.Equal("\uFFFD", Parser.StringUnescape(loc, "\\ud83d\\u0041"));
            Assert.Equal("\\\\\\\"\\u0001\u00e9", Parser.StringEscape("\\\"\u0001\u00e9", false));
        }

        private static void RunWithStack(int maxStackSize, Action action)
        {
            Exception? caught = null;
            Thread thread = new(
                () =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        caught = e;
                    }
                },
                maxStackSize);
            thread.Start();
            thread.Join();
            if (caught != null)
            {
                throw caught;
            }
        }

        [Theory]
        [InlineData("[", "]")]
        [InlineData("(", ")")]
        [InlineData("{a:", "}")]
        [InlineData("- ", "")]
        public void DeepNestingIsStaticError(string open, string close)
        {
            const int depth = 200000;
            string input = string.Concat(Enumerable.Repeat(open, depth)) + "1" + string.Concat(Enumerable.Repeat(close, depth));
            RunWithStack(1024 * 1024, () =>
            {
                StaticError err = Assert.Throws<StaticError>(() => Program.SnippetToAST("test", "", input));
                Assert.EndsWith(" Exceeded maximum parse depth limit.", err.Error());
            });
        }

        [Fact]
        public void ModerateNestingParsesOnOneMegabyteStack()
        {
            const int depth = 500;
            string input = string.Concat(Enumerable.Repeat("[", depth)) + "1" + string.Concat(Enumerable.Repeat("]", depth));
            RunWithStack(1024 * 1024, () => Program.SnippetToAST("test", "", input));
        }

        // static_analyzer_test.go
        [Fact]
        public void SimpleNull()
        {
            LiteralNull node = new();
            Program.Analyze(node);
            Assert.Empty(node.FreeVars);
        }

        [Fact]
        public void SimpleLocal()
        {
            Local node = new()
            {
                Binds = new List<LocalBind> { new() { Variable = "x", Body = new LiteralNull() } },
                Body = new Var { Id = "x" },
            };
            Program.Analyze(node);
            Assert.Empty(node.FreeVars);
            Assert.Equal(new[] { "x" }, node.Body.FreeVars);
        }

        [Fact]
        public void FreeVariablesAreSorted()
        {
            Node node = Program.SnippetToAST("test", "", "local b = 1, a = 2; function(c) [c, b, a, std]");
            Local local = Assert.IsType<Local>(node);
            Assert.Equal(new[] { "a", "b", "std" }, local.Body.FreeVars);
        }

        public static TheoryData<string, string> ProgramErrorTests => new()
        {
            { "{a: 1} + $", "test:1:10-11 No top-level object found." },
            { "x", "test:1:1-2 Unknown variable: x" },
            { "self", "test:1:1-5 Can't use self outside of an object." },
            { "super.x", "test:1:1-6 Can't use super outside of an object." },
            { "'x' in super", "test:1:1-13 Can't use super outside of an object." },
            { "{ [x]: 1 for y in [1] }", "test:1:4-5 Unknown variable: x" },
        };

        [Theory]
        [MemberData(nameof(ProgramErrorTests))]
        public void ProgramErrors(string input, string expected)
        {
            StaticError err = Assert.Throws<StaticError>(() => Program.SnippetToAST("test", "", input));
            Assert.Equal(expected, err.Error());
        }

        [Fact]
        public void DesugarShapes()
        {
            Node node = Program.SnippetToAST("test", "", "{ a: 1 % 2, b:: [x for x in [1]], c: self.a[1:] }");
            DesugaredObject obj = Assert.IsType<DesugaredObject>(node);
            Assert.Equal("$", Assert.Single(obj.Locals).Variable);
            Assert.Equal(ObjectFieldHide.ObjectFieldHidden, obj.Fields[1].Hide);
            Apply mod = Assert.IsType<Apply>(obj.Fields[0].Body);
            Ast.Index modTarget = Assert.IsType<Ast.Index>(mod.Target);
            Assert.Equal("mod", Assert.IsType<LiteralString>(modTarget.IndexExpr).Value);
            Assert.Equal("$std", Assert.IsType<Var>(modTarget.Target).Id);
            Apply flatMap = Assert.IsType<Apply>(obj.Fields[1].Body);
            Assert.Equal("$flatMapArray", Assert.IsType<LiteralString>(Assert.IsType<Ast.Index>(flatMap.Target).IndexExpr).Value);
            Apply slice = Assert.IsType<Apply>(obj.Fields[2].Body);
            Assert.Equal(4, slice.Arguments.Positional.Count);
        }
    }
}
