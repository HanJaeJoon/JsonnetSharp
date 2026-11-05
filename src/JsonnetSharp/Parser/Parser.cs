// Ports go-jsonnet internal/parser/parser.go.
//
// Go returns errors.StaticError values; here they are thrown as StaticError.

using JsonnetSharp.Ast;

namespace JsonnetSharp
{
    internal sealed partial class Parser
    {
        private readonly List<Token> _t;
        private int _currT;

        private Parser(List<Token> t)
        {
            _t = t;
        }

        private static StaticError MakeUnexpectedError(Token t, string while_) =>
            new StaticError("Unexpected: " + t, t.Loc).WithContext(while_);

        private static LocationRange LocFromTokens(Token begin, Token end) => LocationRange.Between(begin.Loc, end.Loc);

        private static LocationRange LocFromTokenAST(Token begin, Node end) => LocationRange.Between(begin.Loc, end.LocRange);

        private Token Pop() => _t[_currT++];

        private static StaticError UnexpectedTokenError(TokenKind tk, Token t)
        {
            if (tk == t.Kind)
            {
                throw new InvalidOperationException("Unexpectedly expected token kind");
            }
            return new StaticError("Expected token " + tk.ToKindString() + " but got " + t, t.Loc);
        }

        private Token PopExpect(TokenKind tk)
        {
            Token t = Pop();
            if (t.Kind != tk)
            {
                throw UnexpectedTokenError(tk, t);
            }
            return t;
        }

        private Token PopExpectOp(string op)
        {
            Token t = Pop();
            if (t.Kind != TokenKind.Operator || t.Data != op)
            {
                throw new StaticError("Expected operator " + op + " but got " + t, t.Loc);
            }
            return t;
        }

        private Token Peek() => _t[_currT];

        private Token DoublePeek() => _t[_currT + 1];

        // parseArgument parses either <f1> id <f2> = expr or just expr.
        private (Fodder? IdFodder, string? Id, Fodder? EqFodder, Node Expr) ParseArgument()
        {
            Fodder? idFodder = null;
            string? id = null;
            Fodder? eqFodder = null;
            if (Peek().Kind == TokenKind.Identifier && DoublePeek().Kind == TokenKind.Operator && DoublePeek().Data == "=")
            {
                Token ident = Pop();
                id = ident.Data;
                idFodder = ident.Fodder;
                Token eq = Pop();
                eqFodder = eq.Fodder;
            }
            Node expr = Parse(Precedence.MaxPrecedence);
            return (idFodder, id, eqFodder, expr);
        }

        private (Token End, Arguments Args, bool GotComma) ParseArguments(string elementKind)
        {
            Arguments args = new();
            bool gotComma = false;
            bool namedArgumentAdded = false;
            bool first = true;
            while (true)
            {
                Fodder? commaFodder = new();
                Token next = Peek();

                if (next.Kind == TokenKind.ParenR)
                {
                    // gotComma can be true or false here.
                    return (Pop(), args, gotComma);
                }

                if (!first && !gotComma)
                {
                    throw new StaticError("Expected a comma before next " + elementKind + ", got " + next, next.Loc);
                }

                (Fodder? idFodder, string? id, Fodder? eqFodder, Node expr) = ParseArgument();

                if (Peek().Kind == TokenKind.Comma)
                {
                    Token comma = Pop();
                    gotComma = true;
                    commaFodder = comma.Fodder;
                }
                else
                {
                    gotComma = false;
                }

                if (id == null)
                {
                    if (namedArgumentAdded)
                    {
                        throw new StaticError("Positional argument after a named argument is not allowed", next.Loc);
                    }
                    CommaSeparatedExpr el = new() { Expr = expr };
                    if (gotComma)
                    {
                        el.CommaFodder = commaFodder;
                    }
                    args.Positional.Add(el);
                }
                else
                {
                    namedArgumentAdded = true;
                    args.Named.Add(new NamedArgument
                    {
                        NameFodder = idFodder,
                        Name = id,
                        EqFodder = eqFodder,
                        Arg = expr,
                        CommaFodder = commaFodder,
                    });
                }

                first = false;
            }
        }

        // parseParameter parses either <f1> id <f2> = expr or just <f1> id.
        private Parameter ParseParameter()
        {
            Parameter ret = new();
            Token ident = Pop();
            if (ident.Kind != TokenKind.Identifier)
            {
                throw UnexpectedTokenError(TokenKind.Identifier, ident).WithContext("parsing parameter");
            }
            ret.Name = ident.Data;
            ret.NameFodder = ident.Fodder;
            ret.LocRange = ident.Loc;
            if (Peek().Kind == TokenKind.Operator && Peek().Data == "=")
            {
                Token eq = Pop();
                ret.EqFodder = eq.Fodder;
                ret.DefaultArg = Parse(Precedence.MaxPrecedence);
                ret.LocRange = LocFromTokenAST(ident, ret.DefaultArg);
            }
            return ret;
        }

        private (Token ParenR, List<Parameter> Params, bool GotComma) ParseParameters(string elementKind)
        {
            Token parenR;
            List<Parameter> parameters = new();
            bool gotComma = false;
            bool first = true;
            while (true)
            {
                Token next = Peek();

                if (next.Kind == TokenKind.ParenR)
                {
                    // gotComma can be true or false here.
                    parenR = Pop();
                    break;
                }

                if (!first && !gotComma)
                {
                    throw new StaticError("Expected a comma before next " + elementKind + ", got " + next, next.Loc);
                }

                Parameter param = ParseParameter();

                if (Peek().Kind == TokenKind.Comma)
                {
                    Token comma = Pop();
                    param.CommaFodder = comma.Fodder;
                    gotComma = true;
                }
                else
                {
                    gotComma = false;
                }
                parameters.Add(param);

                first = false;
            }

            return (parenR, parameters, gotComma);
        }

        private Token ParseBind(List<LocalBind> binds)
        {
            Token varID = PopExpect(TokenKind.Identifier);
            foreach (LocalBind b in binds)
            {
                if (b.Variable == varID.Data)
                {
                    throw new StaticError("Duplicate local var: " + varID.Data, varID.Loc);
                }
            }

            Function? fun = null;
            if (Peek().Kind == TokenKind.ParenL)
            {
                Token parenL = Pop();
                (Token parenR, List<Parameter> parameters, bool gotComma) = ParseParameters("function parameter");
                fun = new Function
                {
                    ParenLeftFodder = parenL.Fodder,
                    Parameters = parameters,
                    TrailingComma = gotComma,
                    ParenRightFodder = parenR.Fodder,
                    // Body gets filled in later.
                };
            }

            Token eqToken = PopExpectOp("=");
            Node body = Parse(Precedence.MaxPrecedence);

            Token delim = Pop();
            if (delim.Kind != TokenKind.Semicolon && delim.Kind != TokenKind.Comma)
            {
                throw new StaticError("Expected , or ; but got " + delim, delim.Loc);
            }

            if (fun != null)
            {
                fun.SetNodeBase(LocFromTokenAST(varID, body), null);
                fun.Body = body;
            }
            binds.Add(new LocalBind
            {
                VarFodder = varID.Fodder,
                Variable = varID.Data,
                EqFodder = eqToken.Fodder,
                Body = body,
                Fun = fun,
                CloseFodder = delim.Fodder,
                LocRange = LocFromTokenAST(varID, body),
            });

            return delim;
        }

        private (Fodder? OpFodder, bool PlusSugar, ObjectFieldHide Hide) ParseObjectAssignmentOp()
        {
            Token op = PopExpect(TokenKind.Operator);
            Fodder? opFodder = op.Fodder;
            string opStr = op.Data;
            bool plusSugar = false;
            if (opStr[0] == '+')
            {
                plusSugar = true;
                opStr = opStr.Substring(1);
            }

            int numColons = 0;
            foreach (char c in opStr)
            {
                if (c != ':')
                {
                    throw new StaticError("Expected one of :, ::, :::, +:, +::, +:::, got: " + op.Data, op.Loc);
                }
                numColons++;
            }

            ObjectFieldHide hide = numColons switch
            {
                1 => ObjectFieldHide.ObjectFieldInherit,
                2 => ObjectFieldHide.ObjectFieldHidden,
                3 => ObjectFieldHide.ObjectFieldVisible,
                _ => throw new StaticError("Expected one of :, ::, :::, +:, +::, +:::, got: " + op.Data, op.Loc),
            };

            return (opFodder, plusSugar, hide);
        }

        private (Node Node, Token Last) ParseObjectRemainderComp(List<ObjectField> fields, bool gotComma, Token tok, Token next)
        {
            int numFields = 0;
            int numAsserts = 0;
            ObjectField? field = null;
            foreach (ObjectField f in fields)
            {
                if (f.Kind == ObjectFieldKind.ObjectLocal)
                {
                    continue;
                }
                if (f.Kind == ObjectFieldKind.ObjectAssert)
                {
                    numAsserts++;
                    continue;
                }
                numFields++;
                field = f;
            }

            if (numAsserts > 0)
            {
                throw new StaticError("Object comprehension cannot have asserts", next.Loc);
            }
            if (numFields != 1)
            {
                throw new StaticError("Object comprehension can only have one field", next.Loc);
            }
            if (field!.Hide != ObjectFieldHide.ObjectFieldInherit)
            {
                throw new StaticError("Object comprehensions cannot have hidden fields", next.Loc);
            }
            if (field.Kind != ObjectFieldKind.ObjectFieldExpr)
            {
                throw new StaticError("Object comprehensions can only have [e] fields", next.Loc);
            }
            (ForSpec spec, Token last) = ParseComprehensionSpecs(next, TokenKind.BraceR);
            ObjectComp comp = new()
            {
                Fields = fields,
                TrailingComma = gotComma,
                Spec = spec,
                CloseFodder = last.Fodder,
            };
            comp.SetNodeBase(LocFromTokens(tok, last), tok.Fodder);
            return (comp, last);
        }

        private ObjectField ParseObjectRemainderField(HashSet<string> literalFields, Token next)
        {
            ObjectFieldKind kind;
            Fodder? fodder1 = null;
            Node? expr1 = null;
            string? id = null;
            Fodder? fodder2 = null;

            switch (next.Kind)
            {
                case TokenKind.Identifier:
                    kind = ObjectFieldKind.ObjectFieldID;
                    id = next.Data;
                    fodder1 = next.Fodder;
                    break;
                case TokenKind.StringDouble:
                case TokenKind.StringSingle:
                case TokenKind.StringBlock:
                case TokenKind.VerbatimStringDouble:
                case TokenKind.VerbatimStringSingle:
                    kind = ObjectFieldKind.ObjectFieldStr;
                    expr1 = TokenStringToAst(next);
                    break;
                default:
                    fodder1 = next.Fodder;
                    kind = ObjectFieldKind.ObjectFieldExpr;
                    expr1 = Parse(Precedence.MaxPrecedence);
                    Token bracketR = PopExpect(TokenKind.BracketR);
                    fodder2 = bracketR.Fodder;
                    break;
            }

            bool isMethod = false;
            bool methComma = false;
            Token? parenL = null;
            Token? parenR = null;
            List<Parameter>? parameters = null;
            if (Peek().Kind == TokenKind.ParenL)
            {
                parenL = Pop();
                (parenR, parameters, methComma) = ParseParameters("method parameter");
                isMethod = true;
            }

            (Fodder? opFodder, bool plusSugar, ObjectFieldHide hide) = ParseObjectAssignmentOp();

            if (plusSugar && isMethod)
            {
                throw new StaticError("Cannot use +: syntax sugar in a method: " + next.Data, next.Loc);
            }

            if (kind != ObjectFieldKind.ObjectFieldExpr)
            {
                if (!literalFields.Add(next.Data))
                {
                    throw new StaticError("Duplicate field: " + next.Data, next.Loc);
                }
            }

            Node body = Parse(Precedence.MaxPrecedence);

            Function? method = null;
            if (isMethod)
            {
                method = new Function
                {
                    ParenLeftFodder = parenL!.Fodder,
                    Parameters = parameters!,
                    TrailingComma = methComma,
                    ParenRightFodder = parenR!.Fodder,
                    Body = body,
                };
            }

            Fodder? commaFodder = null;
            if (Peek().Kind == TokenKind.Comma)
            {
                commaFodder = Peek().Fodder;
            }

            return new ObjectField
            {
                Kind = kind,
                Hide = hide,
                SuperSugar = plusSugar,
                Method = method,
                Fodder1 = fodder1,
                Expr1 = expr1,
                Id = id,
                Fodder2 = fodder2,
                OpFodder = opFodder,
                Expr2 = body,
                CommaFodder = commaFodder,
                LocRange = LocFromTokenAST(next, body),
            };
        }

        private ObjectField ParseObjectRemainderLocal(IdentifierSet binds, Token next)
        {
            Token varID = PopExpect(TokenKind.Identifier);

            string id = varID.Data;

            if (binds.Contains(id))
            {
                throw new StaticError("Duplicate local var: " + id, varID.Loc);
            }

            bool isMethod = false;
            bool funcComma = false;
            Token? parenL = null;
            Token? parenR = null;
            List<Parameter>? parameters = null;
            if (Peek().Kind == TokenKind.ParenL)
            {
                parenL = Pop();
                isMethod = true;
                (parenR, parameters, funcComma) = ParseParameters("function parameter");
            }
            Token opToken = PopExpectOp("=");

            Node body = Parse(Precedence.MaxPrecedence);

            Function? method = null;
            if (isMethod)
            {
                method = new Function
                {
                    ParenLeftFodder = parenL!.Fodder,
                    Parameters = parameters!,
                    ParenRightFodder = parenR!.Fodder,
                    TrailingComma = funcComma,
                    Body = body,
                };
            }

            binds.Add(id);

            Fodder? commaFodder = null;
            if (Peek().Kind == TokenKind.Comma)
            {
                commaFodder = Peek().Fodder;
            }

            return new ObjectField
            {
                Kind = ObjectFieldKind.ObjectLocal,
                Hide = ObjectFieldHide.ObjectFieldVisible,
                SuperSugar = false,
                Method = method,
                Fodder1 = next.Fodder,
                Fodder2 = varID.Fodder,
                Id = id,
                OpFodder = opToken.Fodder,
                Expr2 = body,
                CommaFodder = commaFodder,
                LocRange = LocFromTokenAST(varID, body),
            };
        }

        private ObjectField ParseObjectRemainderAssert(Token next)
        {
            Node cond = Parse(Precedence.MaxPrecedence);
            Node lastAST = cond; // for determining location
            Node? msg = null;
            Fodder? colonFodder = null;
            if (Peek().Kind == TokenKind.Operator && Peek().Data == ":")
            {
                Token colonToken = Pop();
                colonFodder = colonToken.Fodder;
                msg = Parse(Precedence.MaxPrecedence);
                lastAST = msg;
            }

            Fodder? commaFodder = null;
            if (Peek().Kind == TokenKind.Comma)
            {
                commaFodder = Peek().Fodder;
            }

            return new ObjectField
            {
                Kind = ObjectFieldKind.ObjectAssert,
                Hide = ObjectFieldHide.ObjectFieldVisible,
                Fodder1 = next.Fodder,
                Expr2 = cond,
                OpFodder = colonFodder,
                Expr3 = msg,
                CommaFodder = commaFodder,
                LocRange = LocFromTokenAST(next, lastAST),
            };
        }

        // Parse object or object comprehension without leading brace
        private (Node Node, Token Last) ParseObjectRemainder(Token tok)
        {
            List<ObjectField> fields = new();
            HashSet<string> literalFields = new(StringComparer.Ordinal);
            IdentifierSet binds = new();

            bool gotComma = false;
            bool first = true;

            Token next = Pop();

            while (true)
            {
                if (next.Kind == TokenKind.BraceR)
                {
                    Ast.Object obj = new()
                    {
                        Fields = fields,
                        TrailingComma = gotComma,
                        CloseFodder = next.Fodder,
                    };
                    obj.SetNodeBase(LocFromTokens(tok, next), tok.Fodder);
                    return (obj, next);
                }

                if (next.Kind == TokenKind.For)
                {
                    // It's a comprehension
                    return ParseObjectRemainderComp(fields, gotComma, tok, next);
                }

                if (!gotComma && !first)
                {
                    throw new StaticError("Expected a comma before next field", next.Loc);
                }

                ObjectField field;
                switch (next.Kind)
                {
                    case TokenKind.BracketL:
                    case TokenKind.Identifier:
                    case TokenKind.StringDouble:
                    case TokenKind.StringSingle:
                    case TokenKind.StringBlock:
                    case TokenKind.VerbatimStringDouble:
                    case TokenKind.VerbatimStringSingle:
                        field = ParseObjectRemainderField(literalFields, next);
                        break;

                    case TokenKind.Local:
                        field = ParseObjectRemainderLocal(binds, next);
                        break;

                    case TokenKind.Assert:
                        field = ParseObjectRemainderAssert(next);
                        break;

                    default:
                        throw MakeUnexpectedError(next, "parsing field definition");
                }
                fields.Add(field);

                next = Pop();
                if (next.Kind == TokenKind.Comma)
                {
                    gotComma = true;
                    next = Pop();
                }
                else
                {
                    gotComma = false;
                }

                first = false;
            }
        }

        // parses for x in expr for y in expr if expr for z in expr ...
        private (ForSpec Spec, Token Last) ParseComprehensionSpecs(Token forToken, TokenKind end)
        {
            ForSpec? outer = null;
            while (true)
            {
                List<IfSpec> ifSpecs = new();

                Token varID = PopExpect(TokenKind.Identifier);
                string id = varID.Data;
                Token inToken = PopExpect(TokenKind.In);
                Node arr = Parse(Precedence.MaxPrecedence);
                ForSpec forSpec = new()
                {
                    ForFodder = forToken.Fodder,
                    VarFodder = varID.Fodder,
                    VarName = id,
                    InFodder = inToken.Fodder,
                    Expr = arr,
                    Outer = outer,
                };

                Token maybeIf = Pop();
                for (; maybeIf.Kind == TokenKind.If; maybeIf = Pop())
                {
                    Node cond = Parse(Precedence.MaxPrecedence);
                    ifSpecs.Add(new IfSpec
                    {
                        IfFodder = maybeIf.Fodder,
                        Expr = cond,
                    });
                }
                forSpec.Conditions = ifSpecs;
                if (maybeIf.Kind == end)
                {
                    return (forSpec, maybeIf);
                }

                if (maybeIf.Kind != TokenKind.For)
                {
                    throw new StaticError(
                        "Expected for, if or " + end.ToKindString() + " after for clause, got: " + maybeIf, maybeIf.Loc);
                }

                // Go recurses here (parseComprehensionSpecsHelper); a loop is equivalent.
                forToken = maybeIf;
                outer = forSpec;
            }
        }

        // Assumes that the leading '[' has already been consumed and passed as tok.
        // Should read up to and consume the trailing ']'
        private Node ParseArray(Token tok)
        {
            if (Peek().Kind == TokenKind.BracketR)
            {
                Token bracketR0 = Pop();
                Ast.Array empty = new() { CloseFodder = bracketR0.Fodder };
                empty.SetNodeBase(LocFromTokens(tok, bracketR0), tok.Fodder);
                return empty;
            }

            Node first = Parse(Precedence.MaxPrecedence);
            bool gotComma = false;
            Fodder? commaFodder = null;
            if (Peek().Kind == TokenKind.Comma)
            {
                Token comma = Pop();
                gotComma = true;
                commaFodder = comma.Fodder;
            }

            if (Peek().Kind == TokenKind.For)
            {
                // It's a comprehension
                Token forToken = Pop();
                (ForSpec spec, Token last) = ParseComprehensionSpecs(forToken, TokenKind.BracketR);
                ArrayComp comp = new()
                {
                    Body = first,
                    TrailingComma = gotComma,
                    TrailingCommaFodder = commaFodder,
                    Spec = spec,
                    CloseFodder = last.Fodder,
                };
                comp.SetNodeBase(LocFromTokens(tok, last), tok.Fodder);
                return comp;
            }

            // Not a comprehension: It can have more elements.
            List<CommaSeparatedExpr> elements = new()
            {
                new CommaSeparatedExpr { Expr = first, CommaFodder = commaFodder },
            };

            Token bracketR;
            while (true)
            {
                Token next = Peek();

                if (next.Kind == TokenKind.BracketR)
                {
                    bracketR = Pop();
                    break;
                }
                if (!gotComma)
                {
                    throw new StaticError("Expected a comma before next array element", next.Loc);
                }
                Node nextElem = Parse(Precedence.MaxPrecedence);

                CommaSeparatedExpr element = new() { Expr = nextElem };
                if (Peek().Kind == TokenKind.Comma)
                {
                    Token comma = Pop();
                    gotComma = true;
                    element.CommaFodder = comma.Fodder;
                }
                else
                {
                    gotComma = false;
                }
                elements.Add(element);
            }

            Ast.Array arr = new()
            {
                Elements = elements,
                TrailingComma = gotComma,
                CloseFodder = bracketR.Fodder,
            };
            arr.SetNodeBase(LocFromTokens(tok, bracketR), tok.Fodder);
            return arr;
        }

        private static LiteralString TokenStringToAst(Token tok)
        {
            LiteralString node = new() { Value = tok.Data };
            node.SetNodeBase(tok.Loc, tok.Fodder);
            bool validate = true;

            switch (tok.Kind)
            {
                case TokenKind.StringSingle:
                    node.Kind = LiteralStringKind.StringSingle;
                    break;
                case TokenKind.StringDouble:
                    node.Kind = LiteralStringKind.StringDouble;
                    break;
                case TokenKind.StringBlock:
                    node.Kind = LiteralStringKind.StringBlock;
                    node.BlockIndent = tok.StringBlockIndent;
                    node.BlockTermIndent = tok.StringBlockTermIndent;
                    validate = false;
                    break;
                case TokenKind.VerbatimStringDouble:
                    node.Kind = LiteralStringKind.VerbatimStringDouble;
                    validate = false;
                    break;
                case TokenKind.VerbatimStringSingle:
                    node.Kind = LiteralStringKind.VerbatimStringSingle;
                    validate = false;
                    break;
                default:
                    throw new InvalidOperationException("Not a string token " + tok);
            }

            if (validate)
            {
                try
                {
                    StringUnescape(node.LocRange, node.Value);
                }
                catch (StaticError err)
                {
                    // Like Go, the message already carries the location, so it appears twice.
                    throw new StaticError(err.Error(), tok.Loc);
                }
            }

            return node;
        }

        // Brackets are dispatched from this small frame because they are what nests deeply.
        private Node ParseTerminal()
        {
            Token tok = Pop();
            return tok.Kind switch
            {
                TokenKind.BraceL => ParseObjectRemainder(tok).Node,
                TokenKind.BracketL => ParseArray(tok),
                TokenKind.ParenL => ParseParens(tok),
                _ => ParseOtherTerminal(tok),
            };
        }

        private Node ParseParens(Token tok)
        {
            Node inner = Parse(Precedence.MaxPrecedence);
            Token tokRight = PopExpect(TokenKind.ParenR);
            return WithBase(new Parens { Inner = inner, CloseFodder = tokRight.Fodder }, LocFromTokens(tok, tokRight), tok.Fodder);
        }

        private Node ParseOtherTerminal(Token tok)
        {
            switch (tok.Kind)
            {
                case TokenKind.Assert:
                case TokenKind.BraceR:
                case TokenKind.BracketR:
                case TokenKind.Comma:
                case TokenKind.Dot:
                case TokenKind.Else:
                case TokenKind.Error:
                case TokenKind.For:
                case TokenKind.Function:
                case TokenKind.If:
                case TokenKind.In:
                case TokenKind.Import:
                case TokenKind.ImportStr:
                case TokenKind.ImportBin:
                case TokenKind.Local:
                case TokenKind.Operator:
                case TokenKind.ParenR:
                case TokenKind.Semicolon:
                case TokenKind.TailStrict:
                case TokenKind.Then:
                    throw MakeUnexpectedError(tok, "parsing terminal");

                case TokenKind.EndOfFile:
                    throw new StaticError("Unexpected end of file", tok.Loc);

                // Literals
                case TokenKind.Number:
                    return WithBase(new LiteralNumber { OriginalString = tok.Data }, tok.Loc, tok.Fodder);
                case TokenKind.StringDouble:
                case TokenKind.StringSingle:
                case TokenKind.StringBlock:
                case TokenKind.VerbatimStringDouble:
                case TokenKind.VerbatimStringSingle:
                    return TokenStringToAst(tok);
                case TokenKind.False:
                    return WithBase(new LiteralBoolean { Value = false }, tok.Loc, tok.Fodder);
                case TokenKind.True:
                    return WithBase(new LiteralBoolean { Value = true }, tok.Loc, tok.Fodder);
                case TokenKind.NullLit:
                    return WithBase(new LiteralNull(), tok.Loc, tok.Fodder);

                // Variables
                case TokenKind.Dollar:
                    return WithBase(new Dollar(), tok.Loc, tok.Fodder);
                case TokenKind.Identifier:
                    return WithBase(new Var { Id = tok.Data }, tok.Loc, tok.Fodder);
                case TokenKind.Self:
                    return WithBase(new Self(), tok.Loc, tok.Fodder);
                case TokenKind.Super:
                    {
                        Token next = Pop();
                        Node? index = null;
                        string? id = null;
                        Fodder? idFodder;
                        switch (next.Kind)
                        {
                            case TokenKind.Dot:
                                Token fieldID = PopExpect(TokenKind.Identifier);
                                idFodder = fieldID.Fodder;
                                id = fieldID.Data;
                                break;
                            case TokenKind.BracketL:
                                index = Parse(Precedence.MaxPrecedence);
                                Token bracketR = PopExpect(TokenKind.BracketR);
                                idFodder = bracketR.Fodder;
                                break;
                            default:
                                throw new StaticError("Expected . or [ after super", tok.Loc);
                        }
                        return WithBase(
                            new SuperIndex { DotFodder = next.Fodder, Index = index, IDFodder = idFodder, Id = id },
                            tok.Loc,
                            tok.Fodder);
                    }
            }

            throw new StaticError("INTERNAL ERROR: Unknown tok kind: " + tok.Kind.ToKindString(), tok.Loc);
        }

        private static T WithBase<T>(T node, LocationRange loc, Fodder? fodder)
            where T : Node
        {
            node.SetNodeBase(loc, fodder);
            return node;
        }

        // Go's parse(prec) recurses into parse(prec - 1) down to the terminal (or the unary level)
        // and then runs each level's binary operator loop on the way back up. Every level sees the
        // same begin token, so the descent is unrolled into a loop here: same tokens consumed in the
        // same order, same errors, but one frame per nesting instead of one per precedence level.
        private Node Parse(int prec)
        {
            Token begin = Peek();
            StackGuard.Ensure(begin.Loc);

            switch (begin.Kind)
            {
                // These cases have effectively MaxPrecedence as the first
                // call to parse will parse them.
                case TokenKind.Assert:
                case TokenKind.Error:
                case TokenKind.If:
                case TokenKind.Function:
                case TokenKind.Import:
                case TokenKind.ImportStr:
                case TokenKind.ImportBin:
                case TokenKind.Local:
                    return ParseKeyword(begin);
            }

            int level = 0;
            Node lhs;
            if (begin.Kind == TokenKind.Operator)
            {
                // Unary operator
                if (!Operators.UopMap.TryGetValue(begin.Data, out UnaryOp uop))
                {
                    throw new StaticError("Not a unary operator: " + begin.Data, begin.Loc);
                }
                if (prec >= Precedence.UnaryPrecedence)
                {
                    Token op = Pop();
                    Node expr = Parse(Precedence.UnaryPrecedence);
                    level = Precedence.UnaryPrecedence;
                    lhs = WithBase(new Unary { Op = uop, Expr = expr }, LocFromTokenAST(op, expr), begin.Fodder);
                }
                else
                {
                    lhs = ParseTerminal();
                }
            }
            else
            {
                // Base case
                lhs = ParseTerminal();
            }

            for (int p = level + 1; p <= prec; p++)
            {
                lhs = ParseOperators(p, begin, lhs);
            }
            return lhs;
        }

        private Node ParseKeyword(Token begin)
        {
            switch (begin.Kind)
            {
                case TokenKind.Assert:
                    {
                        Pop();
                        Node cond = Parse(Precedence.MaxPrecedence);
                        Node? msg = null;
                        Fodder? colonFodder = null;
                        if (Peek().Kind == TokenKind.Operator && Peek().Data == ":")
                        {
                            Token colon = Pop();
                            colonFodder = colon.Fodder;
                            msg = Parse(Precedence.MaxPrecedence);
                        }
                        Token semicolon = PopExpect(TokenKind.Semicolon);
                        Node rest = Parse(Precedence.MaxPrecedence);
                        return WithBase(
                            new Assert
                            {
                                Cond = cond,
                                ColonFodder = colonFodder,
                                Message = msg,
                                SemicolonFodder = semicolon.Fodder,
                                Rest = rest,
                            },
                            LocFromTokenAST(begin, rest),
                            begin.Fodder);
                    }

                case TokenKind.Error:
                    {
                        Pop();
                        Node expr = Parse(Precedence.MaxPrecedence);
                        return WithBase(new Ast.Error { Expr = expr }, LocFromTokenAST(begin, expr), begin.Fodder);
                    }

                case TokenKind.If:
                    {
                        Pop();
                        Node cond = Parse(Precedence.MaxPrecedence);
                        Token thenToken = PopExpect(TokenKind.Then);
                        Node branchTrue = Parse(Precedence.MaxPrecedence);
                        Node? branchFalse = null;
                        Fodder? elseFodder = null;
                        LocationRange lr = LocFromTokenAST(begin, branchTrue);
                        if (Peek().Kind == TokenKind.Else)
                        {
                            Token elseToken = Pop();
                            elseFodder = elseToken.Fodder;
                            branchFalse = Parse(Precedence.MaxPrecedence);
                            lr = LocFromTokenAST(begin, branchFalse);
                        }
                        return WithBase(
                            new Conditional
                            {
                                Cond = cond,
                                ThenFodder = thenToken.Fodder,
                                BranchTrue = branchTrue,
                                ElseFodder = elseFodder,
                                BranchFalse = branchFalse,
                            },
                            lr,
                            begin.Fodder);
                    }

                case TokenKind.Function:
                    {
                        Pop();
                        Token next = Pop();
                        if (next.Kind != TokenKind.ParenL)
                        {
                            throw new StaticError("Expected ( but got " + next, next.Loc);
                        }
                        (Token parenR, List<Parameter> parameters, bool gotComma) = ParseParameters("function parameter");
                        Node body = Parse(Precedence.MaxPrecedence);
                        return WithBase(
                            new Function
                            {
                                ParenLeftFodder = next.Fodder,
                                Parameters = parameters,
                                TrailingComma = gotComma,
                                ParenRightFodder = parenR.Fodder,
                                Body = body,
                            },
                            LocFromTokenAST(begin, body),
                            begin.Fodder);
                    }

                case TokenKind.Import:
                case TokenKind.ImportStr:
                case TokenKind.ImportBin:
                    {
                        Pop();
                        Node body = Parse(Precedence.MaxPrecedence);
                        if (body is LiteralString lit)
                        {
                            if (lit.Kind == LiteralStringKind.StringBlock)
                            {
                                throw new StaticError("Block string literals not allowed in imports", body.LocRange);
                            }
                            LocationRange loc = LocFromTokenAST(begin, body);
                            return begin.Kind switch
                            {
                                TokenKind.Import => WithBase(new Import { File = lit }, loc, begin.Fodder),
                                TokenKind.ImportStr => WithBase<Node>(new ImportStr { File = lit }, loc, begin.Fodder),
                                _ => WithBase<Node>(new ImportBin { File = lit }, loc, begin.Fodder),
                            };
                        }
                        throw new StaticError("Computed imports are not allowed", body.LocRange);
                    }

                case TokenKind.Local:
                    {
                        Pop();
                        List<LocalBind> binds = new();
                        while (true)
                        {
                            Token delim = ParseBind(binds);
                            if (delim.Kind == TokenKind.Semicolon)
                            {
                                break;
                            }
                        }
                        Node body = Parse(Precedence.MaxPrecedence);
                        return WithBase(new Local { Binds = binds, Body = body }, LocFromTokenAST(begin, body), begin.Fodder);
                    }

                default:
                    throw new InvalidOperationException("ParseKeyword called on " + begin);
            }
        }

        private Node ParseOperators(int prec, Token begin, Node lhs)
        {
            while (true)
            {
                // Then next token must be a binary operator.
                BinaryOp bop = default;

                // Check precedence is correct for this level.  If we're parsing operators
                // with higher precedence, then return lhs and let lower levels deal with
                // the operator.
                switch (Peek().Kind)
                {
                    case TokenKind.In:
                        bop = BinaryOp.BopIn;
                        if (Precedence.BinaryOpPrecedence(bop) != prec)
                        {
                            return lhs;
                        }
                        break;
                    case TokenKind.Operator:
                        if (Peek().Data == ":")
                        {
                            // Special case for the colons in assert. Since COLON is no-longer a
                            // special token, we have to make sure it does not trip the
                            // op_is_binary test below.  It should terminate parsing of the
                            // expression here, returning control to the parsing of the actual
                            // assert AST.
                            return lhs;
                        }
                        if (Peek().Data == "::")
                        {
                            // Special case for [e::]
                            // We need to stop parsing e when we see the :: and
                            // avoid tripping the op_is_binary test below.
                            return lhs;
                        }
                        if (!Operators.BopMap.TryGetValue(Peek().Data, out bop))
                        {
                            throw new StaticError("Not a binary operator: " + Peek().Data, Peek().Loc);
                        }
                        if (Precedence.BinaryOpPrecedence(bop) != prec)
                        {
                            return lhs;
                        }
                        break;

                    case TokenKind.Dot:
                    case TokenKind.BracketL:
                    case TokenKind.ParenL:
                    case TokenKind.BraceL:
                        if (Precedence.ApplyPrecedence != prec)
                        {
                            return lhs;
                        }
                        break;
                    default:
                        return lhs;
                }

                Token op = Pop();
                switch (op.Kind)
                {
                    case TokenKind.BracketL:
                        lhs = ParseIndexOrSlice(begin, op, lhs);
                        break;
                    case TokenKind.Dot:
                        {
                            Token fieldID = PopExpect(TokenKind.Identifier);
                            lhs = WithBase(
                                new Ast.Index
                                {
                                    Target = lhs,
                                    LeftBracketFodder = op.Fodder,
                                    Id = fieldID.Data,
                                    RightBracketFodder = fieldID.Fodder,
                                },
                                LocFromTokens(begin, fieldID),
                                null);
                            break;
                        }
                    case TokenKind.ParenL:
                        {
                            (Token end, Arguments args, bool gotComma) = ParseArguments("function argument");
                            bool tailStrict = false;
                            Fodder? tailStrictFodder = null;
                            if (Peek().Kind == TokenKind.TailStrict)
                            {
                                Token tailStrictTok = Pop();
                                tailStrictFodder = tailStrictTok.Fodder;
                                tailStrict = true;
                            }
                            lhs = WithBase(
                                new Apply
                                {
                                    Target = lhs,
                                    FodderLeft = op.Fodder,
                                    Arguments = args,
                                    TrailingComma = gotComma,
                                    FodderRight = end.Fodder,
                                    TailStrict = tailStrict,
                                    TailStrictFodder = tailStrictFodder,
                                },
                                LocFromTokens(begin, end),
                                null);
                            break;
                        }
                    case TokenKind.BraceL:
                        {
                            (Node obj, Token end) = ParseObjectRemainder(op);
                            lhs = WithBase(new ApplyBrace { Left = lhs, Right = obj }, LocFromTokens(begin, end), null);
                            break;
                        }
                    default:
                        if (op.Kind == TokenKind.In && Peek().Kind == TokenKind.Super)
                        {
                            Token super = Pop();
                            lhs = WithBase(
                                new InSuper { Index = lhs, InFodder = op.Fodder, SuperFodder = super.Fodder },
                                LocFromTokens(begin, super),
                                null);
                        }
                        else
                        {
                            Node rhs = Parse(prec - 1);
                            lhs = WithBase(
                                new Binary { Left = lhs, OpFodder = op.Fodder, Op = bop, Right = rhs },
                                LocFromTokenAST(begin, rhs),
                                null);
                        }
                        break;
                }
            }
        }

        // handle slice
        private Node ParseIndexOrSlice(Token begin, Token op, Node lhs)
        {
            Node?[] indexes = new Node?[3];
            Fodder?[] fodders = new Fodder?[3];
            int colonsConsumed = 0;

            Token? end = null;
            bool readyForNextIndex = true;
            Fodder? rightBracketFodder = null;
            while (colonsConsumed < 3)
            {
                // Like Go, the colon checks look at the token data only, so a string ":" counts.
                if (Peek().Kind == TokenKind.BracketR)
                {
                    end = Pop();
                    rightBracketFodder = end.Fodder;
                    break;
                }
                else if (Peek().Data == ":")
                {
                    end = Pop();
                    fodders[colonsConsumed] = end.Fodder;
                    colonsConsumed++;
                    readyForNextIndex = true;
                }
                else if (Peek().Data == "::")
                {
                    end = Pop();
                    fodders[colonsConsumed] = end.Fodder;
                    colonsConsumed += 2;
                    readyForNextIndex = true;
                }
                else if (readyForNextIndex)
                {
                    indexes[colonsConsumed] = Parse(Precedence.MaxPrecedence);
                    readyForNextIndex = false;
                }
                else
                {
                    throw UnexpectedTokenError(TokenKind.BracketR, Peek());
                }
            }
            if (colonsConsumed > 2)
            {
                // example: target[42:42:42:42]
                throw new StaticError("Invalid slice: too many colons", end!.Loc);
            }
            if (colonsConsumed == 0 && readyForNextIndex)
            {
                // example: target[]
                throw new StaticError("ast.Index requires an expression", end!.Loc);
            }
            bool isSlice = colonsConsumed > 0;

            if (isSlice)
            {
                return WithBase(
                    new Slice
                    {
                        Target = lhs,
                        LeftBracketFodder = op.Fodder,
                        BeginIndex = indexes[0],
                        EndColonFodder = fodders[0],
                        EndIndex = indexes[1],
                        StepColonFodder = fodders[1],
                        Step = indexes[2],
                        RightBracketFodder = rightBracketFodder,
                    },
                    LocFromTokens(begin, end!),
                    null);
            }
            return WithBase(
                new Ast.Index
                {
                    Target = lhs,
                    LeftBracketFodder = op.Fodder,
                    IndexExpr = indexes[0],
                    RightBracketFodder = rightBracketFodder,
                },
                LocFromTokens(begin, end!),
                null);
        }

        // Parse parses a list of tokens into a parse tree.  Any fodder after the final token is
        // returned as well.
        internal static (Node Node, Fodder? Fodder) Parse(List<Token> t)
        {
            Parser p = new(t);
            Node expr = p.Parse(Precedence.MaxPrecedence);
            Token eof = p.Peek();

            if (eof.Kind != TokenKind.EndOfFile)
            {
                throw new StaticError("Did not expect: " + eof, eof.Loc);
            }

            AddContext(expr, new NodeContext(TopLevelContext), Anonymous);

            return (expr, eof.Fodder);
        }

        // SnippetToRawAST converts a Jsonnet code snippet to an AST (without any transformations).
        // Any fodder after the final token is returned as well.
        internal static (Node Node, Fodder? Fodder) SnippetToRawAST(string diagnosticFilename, string importedFilename, string snippet) =>
            Parse(Lexer.Lex(diagnosticFilename, importedFilename, snippet));

        internal static (Node Node, Fodder? Fodder) SnippetToRawAST(string diagnosticFilename, string importedFilename, byte[] snippet) =>
            Parse(Lexer.Lex(diagnosticFilename, importedFilename, snippet));
    }
}
