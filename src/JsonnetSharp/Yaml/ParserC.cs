// Port of sigs.k8s.io/yaml v1.4.0 goyaml.v2/parserc.go.
//
// The parser implements the following grammar:
//
// stream               ::= STREAM-START implicit_document? explicit_document* STREAM-END
// implicit_document    ::= block_node DOCUMENT-END*
// explicit_document    ::= DIRECTIVE* DOCUMENT-START block_node? DOCUMENT-END*
// block_node_or_indentless_sequence    ::=
//                          ALIAS
//                          | properties (block_content | indentless_block_sequence)?
//                          | block_content
//                          | indentless_block_sequence
// block_node           ::= ALIAS
//                          | properties block_content?
//                          | block_content
// flow_node            ::= ALIAS
//                          | properties flow_content?
//                          | flow_content
// properties           ::= TAG ANCHOR? | ANCHOR TAG?
// block_content        ::= block_collection | flow_collection | SCALAR
// flow_content         ::= flow_collection | SCALAR
// block_collection     ::= block_sequence | block_mapping
// flow_collection      ::= flow_sequence | flow_mapping
// block_sequence       ::= BLOCK-SEQUENCE-START (BLOCK-ENTRY block_node?)* BLOCK-END
// indentless_sequence  ::= (BLOCK-ENTRY block_node?)+
// block_mapping        ::= BLOCK-MAPPING_START
//                          ((KEY block_node_or_indentless_sequence?)?
//                          (VALUE block_node_or_indentless_sequence?)?)*
//                          BLOCK-END
// flow_sequence        ::= FLOW-SEQUENCE-START
//                          (flow_sequence_entry FLOW-ENTRY)*
//                          flow_sequence_entry?
//                          FLOW-SEQUENCE-END
// flow_sequence_entry  ::= flow_node | KEY flow_node? (VALUE flow_node?)?
// flow_mapping         ::= FLOW-MAPPING-START
//                          (flow_mapping_entry FLOW-ENTRY)*
//                          flow_mapping_entry?
//                          FLOW-MAPPING-END
// flow_mapping_entry   ::= flow_node | KEY flow_node? (VALUE flow_node?)?

namespace JsonnetSharp.Yaml;

internal sealed partial class YamlParser
{
    private static readonly YamlTagDirective[] DefaultTagDirectives =
    {
        new(new[] { (byte)'!' }, new[] { (byte)'!' }),
        new(new[] { (byte)'!', (byte)'!' }, "tag:yaml.org,2002:".Select(c => (byte)c).ToArray()),
    };

    // Peek the next token in the token queue.
    private YamlToken? PeekToken()
    {
        if (tokenAvailable || FetchMoreTokens())
        {
            return tokens[tokensHead];
        }
        return null;
    }

    // Remove the next token from the queue (must be called after PeekToken).
    private void SkipToken()
    {
        tokenAvailable = false;
        tokensParsed++;
        streamEndProduced = tokens[tokensHead].Type == YamlTokenType.StreamEnd;
        tokensHead++;
    }

    // Get the next event.
    public bool Parse(out YamlEvent ev)
    {
        // Erase the event object.
        ev = YamlEvent.Empty;

        // No events after the end of the stream or error.
        if (streamEndProduced || Error != YamlErrorType.None || state == YamlParserState.End)
        {
            return true;
        }

        // Generate the next event.
        return StateMachine(ref ev);
    }

    // Set parser error.
    private bool SetParserError(string problem, YamlMark problemMark)
    {
        Error = YamlErrorType.Parser;
        Problem = problem;
        ProblemMark = problemMark;
        return false;
    }

    private bool SetParserErrorContext(string context, YamlMark contextMark, string problem, YamlMark problemMark)
    {
        Error = YamlErrorType.Parser;
        Context = context;
        ContextMark = contextMark;
        Problem = problem;
        ProblemMark = problemMark;
        return false;
    }

    private YamlParserState PopState() => Pop(states);

    // State dispatcher.
    private bool StateMachine(ref YamlEvent ev) => state switch
    {
        YamlParserState.StreamStart => ParseStreamStart(ref ev),
        YamlParserState.ImplicitDocumentStart => ParseDocumentStart(ref ev, true),
        YamlParserState.DocumentStart => ParseDocumentStart(ref ev, false),
        YamlParserState.DocumentContent => ParseDocumentContent(ref ev),
        YamlParserState.DocumentEnd => ParseDocumentEnd(ref ev),
        YamlParserState.BlockNode => ParseNode(ref ev, true, false),
        YamlParserState.BlockNodeOrIndentlessSequence => ParseNode(ref ev, true, true),
        YamlParserState.FlowNode => ParseNode(ref ev, false, false),
        YamlParserState.BlockSequenceFirstEntry => ParseBlockSequenceEntry(ref ev, true),
        YamlParserState.BlockSequenceEntry => ParseBlockSequenceEntry(ref ev, false),
        YamlParserState.IndentlessSequenceEntry => ParseIndentlessSequenceEntry(ref ev),
        YamlParserState.BlockMappingFirstKey => ParseBlockMappingKey(ref ev, true),
        YamlParserState.BlockMappingKey => ParseBlockMappingKey(ref ev, false),
        YamlParserState.BlockMappingValue => ParseBlockMappingValue(ref ev),
        YamlParserState.FlowSequenceFirstEntry => ParseFlowSequenceEntry(ref ev, true),
        YamlParserState.FlowSequenceEntry => ParseFlowSequenceEntry(ref ev, false),
        YamlParserState.FlowSequenceEntryMappingKey => ParseFlowSequenceEntryMappingKey(ref ev),
        YamlParserState.FlowSequenceEntryMappingValue => ParseFlowSequenceEntryMappingValue(ref ev),
        YamlParserState.FlowSequenceEntryMappingEnd => ParseFlowSequenceEntryMappingEnd(ref ev),
        YamlParserState.FlowMappingFirstKey => ParseFlowMappingKey(ref ev, true),
        YamlParserState.FlowMappingKey => ParseFlowMappingKey(ref ev, false),
        YamlParserState.FlowMappingValue => ParseFlowMappingValue(ref ev, false),
        YamlParserState.FlowMappingEmptyValue => ParseFlowMappingValue(ref ev, true),
        _ => throw new InvalidOperationException("invalid parser state"),
    };

    private static YamlEvent Simple(YamlEventType type, YamlMark start, YamlMark end) =>
        new() { Type = type, StartMark = start, EndMark = end };

    // Parse the production:
    // stream   ::= STREAM-START implicit_document? explicit_document* STREAM-END
    //              ************
    private bool ParseStreamStart(ref YamlEvent ev)
    {
        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }
        if (token.Type != YamlTokenType.StreamStart)
        {
            return SetParserError("did not find expected <stream-start>", token.StartMark);
        }
        state = YamlParserState.ImplicitDocumentStart;
        ev = Simple(YamlEventType.StreamStart, token.StartMark, token.EndMark);
        SkipToken();
        return true;
    }

    // Parse the productions:
    // implicit_document    ::= block_node DOCUMENT-END*
    //                          *
    // explicit_document    ::= DIRECTIVE* DOCUMENT-START block_node? DOCUMENT-END*
    //                          *************************
    private bool ParseDocumentStart(ref YamlEvent ev, bool isImplicit)
    {
        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }

        // Parse extra document end indicators.
        if (!isImplicit)
        {
            while (token.Type == YamlTokenType.DocumentEnd)
            {
                SkipToken();
                token = PeekToken();
                if (token == null)
                {
                    return false;
                }
            }
        }

        if (isImplicit && token.Type != YamlTokenType.VersionDirective &&
            token.Type != YamlTokenType.TagDirective &&
            token.Type != YamlTokenType.DocumentStart &&
            token.Type != YamlTokenType.StreamEnd)
        {
            // Parse an implicit document.
            if (!ProcessDirectives())
            {
                return false;
            }
            states.Add(YamlParserState.DocumentEnd);
            state = YamlParserState.BlockNode;

            ev = Simple(YamlEventType.DocumentStart, token.StartMark, token.EndMark);
        }
        else if (token.Type != YamlTokenType.StreamEnd)
        {
            // Parse an explicit document.
            YamlMark startMark = token.StartMark;
            if (!ProcessDirectives())
            {
                return false;
            }
            token = PeekToken();
            if (token == null)
            {
                return false;
            }
            if (token.Type != YamlTokenType.DocumentStart)
            {
                SetParserError("did not find expected <document start>", token.StartMark);
                return false;
            }
            states.Add(YamlParserState.DocumentEnd);
            state = YamlParserState.DocumentContent;
            YamlMark endMark = token.EndMark;

            ev = Simple(YamlEventType.DocumentStart, startMark, endMark);
            SkipToken();
        }
        else
        {
            // Parse the stream end.
            state = YamlParserState.End;
            ev = Simple(YamlEventType.StreamEnd, token.StartMark, token.EndMark);
            SkipToken();
        }

        return true;
    }

    // Parse the productions:
    // explicit_document    ::= DIRECTIVE* DOCUMENT-START block_node? DOCUMENT-END*
    //                                                    ***********
    private bool ParseDocumentContent(ref YamlEvent ev)
    {
        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }
        if (token.Type == YamlTokenType.VersionDirective ||
            token.Type == YamlTokenType.TagDirective ||
            token.Type == YamlTokenType.DocumentStart ||
            token.Type == YamlTokenType.DocumentEnd ||
            token.Type == YamlTokenType.StreamEnd)
        {
            state = PopState();
            return ProcessEmptyScalar(ref ev, token.StartMark);
        }
        return ParseNode(ref ev, true, false);
    }

    // Parse the productions:
    // implicit_document    ::= block_node DOCUMENT-END*
    //                                     *************
    // explicit_document    ::= DIRECTIVE* DOCUMENT-START block_node? DOCUMENT-END*
    private bool ParseDocumentEnd(ref YamlEvent ev)
    {
        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }

        YamlMark startMark = token.StartMark;
        YamlMark endMark = token.StartMark;

        bool isImplicit = true;
        if (token.Type == YamlTokenType.DocumentEnd)
        {
            endMark = token.EndMark;
            SkipToken();
            isImplicit = false;
        }

        tagDirectives.Clear();

        state = YamlParserState.DocumentStart;
        ev = Simple(YamlEventType.DocumentEnd, startMark, endMark);
        ev.Implicit = isImplicit;
        return true;
    }

    // Parse the productions:
    // block_node_or_indentless_sequence    ::=
    //                          ALIAS
    //                          *****
    //                          | properties (block_content | indentless_block_sequence)?
    //                            **********  *
    //                          | block_content | indentless_block_sequence
    //                            *
    // block_node           ::= ALIAS
    //                          *****
    //                          | properties block_content?
    //                            ********** *
    //                          | block_content
    //                            *
    // flow_node            ::= ALIAS
    //                          *****
    //                          | properties flow_content?
    //                            ********** *
    //                          | flow_content
    //                            *
    // properties           ::= TAG ANCHOR? | ANCHOR TAG?
    //                          *************************
    // block_content        ::= block_collection | flow_collection | SCALAR
    //                                                               ******
    // flow_content         ::= flow_collection | SCALAR
    //                                            ******
    private bool ParseNode(ref YamlEvent ev, bool block, bool indentlessSequence)
    {
        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }

        if (token.Type == YamlTokenType.Alias)
        {
            state = PopState();
            ev = Simple(YamlEventType.Alias, token.StartMark, token.EndMark);
            ev.Anchor = token.Value;
            SkipToken();
            return true;
        }

        YamlMark startMark = token.StartMark;
        YamlMark endMark = token.StartMark;

        bool tagToken = false;
        byte[]? tagHandle = null;
        byte[]? tagSuffix = null;
        byte[]? anchor = null;
        YamlMark tagMark = default;
        if (token.Type == YamlTokenType.Anchor)
        {
            anchor = token.Value;
            startMark = token.StartMark;
            endMark = token.EndMark;
            SkipToken();
            token = PeekToken();
            if (token == null)
            {
                return false;
            }
            if (token.Type == YamlTokenType.Tag)
            {
                tagToken = true;
                tagHandle = token.Value;
                tagSuffix = token.Suffix;
                tagMark = token.StartMark;
                endMark = token.EndMark;
                SkipToken();
                token = PeekToken();
                if (token == null)
                {
                    return false;
                }
            }
        }
        else if (token.Type == YamlTokenType.Tag)
        {
            tagToken = true;
            tagHandle = token.Value;
            tagSuffix = token.Suffix;
            startMark = token.StartMark;
            tagMark = token.StartMark;
            endMark = token.EndMark;
            SkipToken();
            token = PeekToken();
            if (token == null)
            {
                return false;
            }
            if (token.Type == YamlTokenType.Anchor)
            {
                anchor = token.Value;
                endMark = token.EndMark;
                SkipToken();
                token = PeekToken();
                if (token == null)
                {
                    return false;
                }
            }
        }

        byte[]? tag = null;
        if (tagToken)
        {
            if (tagHandle == null || tagHandle.Length == 0)
            {
                tag = tagSuffix;
            }
            else
            {
                foreach (YamlTagDirective directive in tagDirectives)
                {
                    if (directive.Handle.SequenceEqual(tagHandle))
                    {
                        byte[] suffix = tagSuffix ?? Array.Empty<byte>();
                        tag = new byte[directive.Prefix.Length + suffix.Length];
                        Buffer.BlockCopy(directive.Prefix, 0, tag, 0, directive.Prefix.Length);
                        Buffer.BlockCopy(suffix, 0, tag, directive.Prefix.Length, suffix.Length);
                        break;
                    }
                }
                if (tag == null || tag.Length == 0)
                {
                    SetParserErrorContext("while parsing a node", startMark, "found undefined tag handle", tagMark);
                    return false;
                }
            }
        }

        bool isImplicit = tag == null || tag.Length == 0;
        if (indentlessSequence && token.Type == YamlTokenType.BlockEntry)
        {
            endMark = token.EndMark;
            state = YamlParserState.IndentlessSequenceEntry;
            ev = new YamlEvent
            {
                Type = YamlEventType.SequenceStart,
                StartMark = startMark,
                EndMark = endMark,
                Anchor = anchor,
                Tag = tag,
                Implicit = isImplicit,
            };
            return true;
        }
        if (token.Type == YamlTokenType.Scalar)
        {
            bool plainImplicit = false;
            bool quotedImplicit = false;
            endMark = token.EndMark;
            if ((isImplicit && token.Style == YamlScalarStyle.Plain) || (tag != null && tag.Length == 1 && tag[0] == '!'))
            {
                plainImplicit = true;
            }
            else if (isImplicit)
            {
                quotedImplicit = true;
            }
            state = PopState();

            ev = new YamlEvent
            {
                Type = YamlEventType.Scalar,
                StartMark = startMark,
                EndMark = endMark,
                Anchor = anchor,
                Tag = tag,
                Value = token.Value,
                Implicit = plainImplicit,
                QuotedImplicit = quotedImplicit,
                Style = token.Style,
            };
            SkipToken();
            return true;
        }
        if (token.Type == YamlTokenType.FlowSequenceStart || token.Type == YamlTokenType.FlowMappingStart ||
            block && (token.Type == YamlTokenType.BlockSequenceStart || token.Type == YamlTokenType.BlockMappingStart))
        {
            endMark = token.EndMark;
            bool sequence = token.Type == YamlTokenType.FlowSequenceStart || token.Type == YamlTokenType.BlockSequenceStart;
            state = token.Type switch
            {
                YamlTokenType.FlowSequenceStart => YamlParserState.FlowSequenceFirstEntry,
                YamlTokenType.FlowMappingStart => YamlParserState.FlowMappingFirstKey,
                YamlTokenType.BlockSequenceStart => YamlParserState.BlockSequenceFirstEntry,
                _ => YamlParserState.BlockMappingFirstKey,
            };
            ev = new YamlEvent
            {
                Type = sequence ? YamlEventType.SequenceStart : YamlEventType.MappingStart,
                StartMark = startMark,
                EndMark = endMark,
                Anchor = anchor,
                Tag = tag,
                Implicit = isImplicit,
            };
            return true;
        }
        if ((anchor != null && anchor.Length > 0) || (tag != null && tag.Length > 0))
        {
            state = PopState();

            ev = new YamlEvent
            {
                Type = YamlEventType.Scalar,
                StartMark = startMark,
                EndMark = endMark,
                Anchor = anchor,
                Tag = tag,
                Implicit = isImplicit,
                QuotedImplicit = false,
                Style = YamlScalarStyle.Plain,
            };
            return true;
        }

        string context = block ? "while parsing a block node" : "while parsing a flow node";
        SetParserErrorContext(context, startMark, "did not find expected node content", token.StartMark);
        return false;
    }

    // Parse the productions:
    // block_sequence ::= BLOCK-SEQUENCE-START (BLOCK-ENTRY block_node?)* BLOCK-END
    //                    ********************  *********** *             *********
    private bool ParseBlockSequenceEntry(ref YamlEvent ev, bool first)
    {
        YamlToken? token;
        if (first)
        {
            token = PeekToken();
            marks.Add(token!.StartMark);
            SkipToken();
        }

        token = PeekToken();
        if (token == null)
        {
            return false;
        }

        if (token.Type == YamlTokenType.BlockEntry)
        {
            YamlMark entryMark = token.EndMark;
            SkipToken();
            token = PeekToken();
            if (token == null)
            {
                return false;
            }
            if (token.Type != YamlTokenType.BlockEntry && token.Type != YamlTokenType.BlockEnd)
            {
                states.Add(YamlParserState.BlockSequenceEntry);
                return ParseNode(ref ev, true, false);
            }
            state = YamlParserState.BlockSequenceEntry;
            return ProcessEmptyScalar(ref ev, entryMark);
        }
        if (token.Type == YamlTokenType.BlockEnd)
        {
            state = PopState();
            Pop(marks);

            ev = Simple(YamlEventType.SequenceEnd, token.StartMark, token.EndMark);

            SkipToken();
            return true;
        }

        YamlMark contextMark = Pop(marks);
        return SetParserErrorContext("while parsing a block collection", contextMark,
            "did not find expected '-' indicator", token.StartMark);
    }

    // Parse the productions:
    // indentless_sequence  ::= (BLOCK-ENTRY block_node?)+
    //                           *********** *
    private bool ParseIndentlessSequenceEntry(ref YamlEvent ev)
    {
        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }

        if (token.Type == YamlTokenType.BlockEntry)
        {
            YamlMark entryMark = token.EndMark;
            SkipToken();
            token = PeekToken();
            if (token == null)
            {
                return false;
            }
            if (token.Type != YamlTokenType.BlockEntry &&
                token.Type != YamlTokenType.Key &&
                token.Type != YamlTokenType.Value &&
                token.Type != YamlTokenType.BlockEnd)
            {
                states.Add(YamlParserState.IndentlessSequenceEntry);
                return ParseNode(ref ev, true, false);
            }
            state = YamlParserState.IndentlessSequenceEntry;
            return ProcessEmptyScalar(ref ev, entryMark);
        }
        state = PopState();

        // [Go] Shouldn't the end mark be token.end_mark?
        ev = Simple(YamlEventType.SequenceEnd, token.StartMark, token.StartMark);
        return true;
    }

    // Parse the productions:
    // block_mapping        ::= BLOCK-MAPPING_START
    //                          *******************
    //                          ((KEY block_node_or_indentless_sequence?)?
    //                            *** *
    //                          (VALUE block_node_or_indentless_sequence?)?)*
    //
    //                          BLOCK-END
    //                          *********
    private bool ParseBlockMappingKey(ref YamlEvent ev, bool first)
    {
        YamlToken? token;
        if (first)
        {
            token = PeekToken();
            marks.Add(token!.StartMark);
            SkipToken();
        }

        token = PeekToken();
        if (token == null)
        {
            return false;
        }

        if (token.Type == YamlTokenType.Key)
        {
            YamlMark keyMark = token.EndMark;
            SkipToken();
            token = PeekToken();
            if (token == null)
            {
                return false;
            }
            if (token.Type != YamlTokenType.Key &&
                token.Type != YamlTokenType.Value &&
                token.Type != YamlTokenType.BlockEnd)
            {
                states.Add(YamlParserState.BlockMappingValue);
                return ParseNode(ref ev, true, true);
            }
            state = YamlParserState.BlockMappingValue;
            return ProcessEmptyScalar(ref ev, keyMark);
        }
        if (token.Type == YamlTokenType.BlockEnd)
        {
            state = PopState();
            Pop(marks);
            ev = Simple(YamlEventType.MappingEnd, token.StartMark, token.EndMark);
            SkipToken();
            return true;
        }

        YamlMark contextMark = Pop(marks);
        return SetParserErrorContext("while parsing a block mapping", contextMark,
            "did not find expected key", token.StartMark);
    }

    // Parse the productions:
    // block_mapping        ::= BLOCK-MAPPING_START
    //
    //                          ((KEY block_node_or_indentless_sequence?)?
    //
    //                          (VALUE block_node_or_indentless_sequence?)?)*
    //                           ***** *
    //                          BLOCK-END
    private bool ParseBlockMappingValue(ref YamlEvent ev)
    {
        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }
        if (token.Type == YamlTokenType.Value)
        {
            YamlMark valueMark = token.EndMark;
            SkipToken();
            token = PeekToken();
            if (token == null)
            {
                return false;
            }
            if (token.Type != YamlTokenType.Key &&
                token.Type != YamlTokenType.Value &&
                token.Type != YamlTokenType.BlockEnd)
            {
                states.Add(YamlParserState.BlockMappingKey);
                return ParseNode(ref ev, true, true);
            }
            state = YamlParserState.BlockMappingKey;
            return ProcessEmptyScalar(ref ev, valueMark);
        }
        state = YamlParserState.BlockMappingKey;
        return ProcessEmptyScalar(ref ev, token.StartMark);
    }

    // Parse the productions:
    // flow_sequence        ::= FLOW-SEQUENCE-START
    //                          *******************
    //                          (flow_sequence_entry FLOW-ENTRY)*
    //                           *                   **********
    //                          flow_sequence_entry?
    //                          *
    //                          FLOW-SEQUENCE-END
    //                          *****************
    // flow_sequence_entry  ::= flow_node | KEY flow_node? (VALUE flow_node?)?
    //                          *
    private bool ParseFlowSequenceEntry(ref YamlEvent ev, bool first)
    {
        YamlToken? token;
        if (first)
        {
            token = PeekToken();
            marks.Add(token!.StartMark);
            SkipToken();
        }
        token = PeekToken();
        if (token == null)
        {
            return false;
        }
        if (token.Type != YamlTokenType.FlowSequenceEnd)
        {
            if (!first)
            {
                if (token.Type == YamlTokenType.FlowEntry)
                {
                    SkipToken();
                    token = PeekToken();
                    if (token == null)
                    {
                        return false;
                    }
                }
                else
                {
                    YamlMark contextMark = Pop(marks);
                    return SetParserErrorContext("while parsing a flow sequence", contextMark,
                        "did not find expected ',' or ']'", token.StartMark);
                }
            }

            if (token.Type == YamlTokenType.Key)
            {
                state = YamlParserState.FlowSequenceEntryMappingKey;
                ev = Simple(YamlEventType.MappingStart, token.StartMark, token.EndMark);
                ev.Implicit = true;
                SkipToken();
                return true;
            }
            if (token.Type != YamlTokenType.FlowSequenceEnd)
            {
                states.Add(YamlParserState.FlowSequenceEntry);
                return ParseNode(ref ev, false, false);
            }
        }

        state = PopState();
        Pop(marks);

        ev = Simple(YamlEventType.SequenceEnd, token.StartMark, token.EndMark);

        SkipToken();
        return true;
    }

    // Parse the productions:
    // flow_sequence_entry  ::= flow_node | KEY flow_node? (VALUE flow_node?)?
    //                                      *** *
    private bool ParseFlowSequenceEntryMappingKey(ref YamlEvent ev)
    {
        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }
        if (token.Type != YamlTokenType.Value &&
            token.Type != YamlTokenType.FlowEntry &&
            token.Type != YamlTokenType.FlowSequenceEnd)
        {
            states.Add(YamlParserState.FlowSequenceEntryMappingValue);
            return ParseNode(ref ev, false, false);
        }
        YamlMark keyMark = token.EndMark;
        SkipToken();
        state = YamlParserState.FlowSequenceEntryMappingValue;
        return ProcessEmptyScalar(ref ev, keyMark);
    }

    // Parse the productions:
    // flow_sequence_entry  ::= flow_node | KEY flow_node? (VALUE flow_node?)?
    //                                                      ***** *
    private bool ParseFlowSequenceEntryMappingValue(ref YamlEvent ev)
    {
        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }
        if (token.Type == YamlTokenType.Value)
        {
            SkipToken();
            // [Go] This inner token shadows the outer one, so the empty scalar below uses the VALUE token mark.
            YamlToken? inner = PeekToken();
            if (inner == null)
            {
                return false;
            }
            if (inner.Type != YamlTokenType.FlowEntry && inner.Type != YamlTokenType.FlowSequenceEnd)
            {
                states.Add(YamlParserState.FlowSequenceEntryMappingEnd);
                return ParseNode(ref ev, false, false);
            }
        }
        state = YamlParserState.FlowSequenceEntryMappingEnd;
        return ProcessEmptyScalar(ref ev, token.StartMark);
    }

    // Parse the productions:
    // flow_sequence_entry  ::= flow_node | KEY flow_node? (VALUE flow_node?)?
    //                                                                      *
    private bool ParseFlowSequenceEntryMappingEnd(ref YamlEvent ev)
    {
        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }
        state = YamlParserState.FlowSequenceEntry;
        // [Go] Shouldn't the end mark be end_mark?
        ev = Simple(YamlEventType.MappingEnd, token.StartMark, token.StartMark);
        return true;
    }

    // Parse the productions:
    // flow_mapping         ::= FLOW-MAPPING-START
    //                          ******************
    //                          (flow_mapping_entry FLOW-ENTRY)*
    //                           *                  **********
    //                          flow_mapping_entry?
    //                          ******************
    //                          FLOW-MAPPING-END
    //                          ****************
    // flow_mapping_entry   ::= flow_node | KEY flow_node? (VALUE flow_node?)?
    //                          *           *** *
    private bool ParseFlowMappingKey(ref YamlEvent ev, bool first)
    {
        YamlToken? token;
        if (first)
        {
            token = PeekToken();
            marks.Add(token!.StartMark);
            SkipToken();
        }

        token = PeekToken();
        if (token == null)
        {
            return false;
        }

        if (token.Type != YamlTokenType.FlowMappingEnd)
        {
            if (!first)
            {
                if (token.Type == YamlTokenType.FlowEntry)
                {
                    SkipToken();
                    token = PeekToken();
                    if (token == null)
                    {
                        return false;
                    }
                }
                else
                {
                    YamlMark contextMark = Pop(marks);
                    return SetParserErrorContext("while parsing a flow mapping", contextMark,
                        "did not find expected ',' or '}'", token.StartMark);
                }
            }

            if (token.Type == YamlTokenType.Key)
            {
                SkipToken();
                token = PeekToken();
                if (token == null)
                {
                    return false;
                }
                if (token.Type != YamlTokenType.Value &&
                    token.Type != YamlTokenType.FlowEntry &&
                    token.Type != YamlTokenType.FlowMappingEnd)
                {
                    states.Add(YamlParserState.FlowMappingValue);
                    return ParseNode(ref ev, false, false);
                }
                state = YamlParserState.FlowMappingValue;
                return ProcessEmptyScalar(ref ev, token.StartMark);
            }
            if (token.Type != YamlTokenType.FlowMappingEnd)
            {
                states.Add(YamlParserState.FlowMappingEmptyValue);
                return ParseNode(ref ev, false, false);
            }
        }

        state = PopState();
        Pop(marks);
        ev = Simple(YamlEventType.MappingEnd, token.StartMark, token.EndMark);
        SkipToken();
        return true;
    }

    // Parse the productions:
    // flow_mapping_entry   ::= flow_node | KEY flow_node? (VALUE flow_node?)?
    //                                   *                  ***** *
    private bool ParseFlowMappingValue(ref YamlEvent ev, bool empty)
    {
        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }
        if (empty)
        {
            state = YamlParserState.FlowMappingKey;
            return ProcessEmptyScalar(ref ev, token.StartMark);
        }
        if (token.Type == YamlTokenType.Value)
        {
            SkipToken();
            token = PeekToken();
            if (token == null)
            {
                return false;
            }
            if (token.Type != YamlTokenType.FlowEntry && token.Type != YamlTokenType.FlowMappingEnd)
            {
                states.Add(YamlParserState.FlowMappingKey);
                return ParseNode(ref ev, false, false);
            }
        }
        state = YamlParserState.FlowMappingKey;
        return ProcessEmptyScalar(ref ev, token.StartMark);
    }

    // Generate an empty scalar event.
    private static bool ProcessEmptyScalar(ref YamlEvent ev, YamlMark emptyMark)
    {
        ev = new YamlEvent
        {
            Type = YamlEventType.Scalar,
            StartMark = emptyMark,
            EndMark = emptyMark,
            Implicit = true,
            Style = YamlScalarStyle.Plain,
        };
        return true;
    }

    // Parse directives. The version and tag directives are validated but not kept: nothing downstream reads them.
    private bool ProcessDirectives()
    {
        bool hasVersionDirective = false;

        YamlToken? token = PeekToken();
        if (token == null)
        {
            return false;
        }

        while (token.Type == YamlTokenType.VersionDirective || token.Type == YamlTokenType.TagDirective)
        {
            if (token.Type == YamlTokenType.VersionDirective)
            {
                if (hasVersionDirective)
                {
                    SetParserError("found duplicate %YAML directive", token.StartMark);
                    return false;
                }
                if (token.Major != 1 || token.Minor != 1)
                {
                    SetParserError("found incompatible YAML document", token.StartMark);
                    return false;
                }
                hasVersionDirective = true;
            }
            else
            {
                var value = new YamlTagDirective(token.Value!, token.Prefix!);
                if (!AppendTagDirective(value, false, token.StartMark))
                {
                    return false;
                }
            }

            SkipToken();
            token = PeekToken();
            if (token == null)
            {
                return false;
            }
        }

        foreach (YamlTagDirective directive in DefaultTagDirectives)
        {
            if (!AppendTagDirective(directive, true, token.StartMark))
            {
                return false;
            }
        }
        return true;
    }

    // Append a tag directive to the directives stack.
    private bool AppendTagDirective(YamlTagDirective value, bool allowDuplicates, YamlMark directiveMark)
    {
        foreach (YamlTagDirective directive in tagDirectives)
        {
            if (value.Handle.SequenceEqual(directive.Handle))
            {
                if (allowDuplicates)
                {
                    return true;
                }
                return SetParserError("found duplicate %TAG directive", directiveMark);
            }
        }

        tagDirectives.Add(value);
        return true;
    }
}
