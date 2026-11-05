// Port of sigs.k8s.io/yaml v1.4.0 goyaml.v2/scannerc.go.

using System.Globalization;

namespace JsonnetSharp.Yaml;

internal sealed partial class YamlParser
{
    // max_flow_level limits the flow_level.
    private const int MaxFlowLevel = 10000;

    // max_indents limits the indents stack size.
    private const int MaxIndents = 10000;

    private const int MaxNumberLength = 2;

    // Ensure that the buffer contains the required number of characters.
    private bool Cache(int length) => unread >= length || UpdateBuffer(length);

    // Advance the buffer pointer.
    private void Skip()
    {
        mark.Index++;
        mark.Column++;
        unread--;
        bufferPos += Width(buffer[bufferPos]);
    }

    private void SkipLine()
    {
        if (IsCrlf(buffer, bufferPos))
        {
            mark.Index += 2;
            mark.Column = 0;
            mark.Line++;
            unread -= 2;
            bufferPos += 2;
        }
        else if (IsBreak(buffer, bufferPos))
        {
            mark.Index++;
            mark.Column = 0;
            mark.Line++;
            unread--;
            bufferPos += Width(buffer[bufferPos]);
        }
    }

    // Copy a character to a string buffer and advance pointers.
    private void Read(YamlBytes s)
    {
        int w = Width(buffer[bufferPos]);
        if (w == 0)
        {
            throw new InvalidOperationException("invalid character sequence");
        }
        s.AddRange(buffer, bufferPos, w);
        bufferPos += w;
        mark.Index++;
        mark.Column++;
        unread--;
    }

    // Copy a line break character to a string buffer and advance pointers.
    private void ReadLine(YamlBytes s)
    {
        byte[] buf = buffer;
        int pos = bufferPos;
        if (buf[pos] == '\r' && buf[pos + 1] == '\n')
        {
            // CR LF . LF
            s.Add((byte)'\n');
            bufferPos += 2;
            mark.Index++;
            unread--;
        }
        else if (buf[pos] == '\r' || buf[pos] == '\n')
        {
            // CR|LF . LF
            s.Add((byte)'\n');
            bufferPos += 1;
        }
        else if (buf[pos] == 0xC2 && buf[pos + 1] == 0x85)
        {
            // NEL . LF
            s.Add((byte)'\n');
            bufferPos += 2;
        }
        else if (buf[pos] == 0xE2 && buf[pos + 1] == 0x80 && (buf[pos + 2] == 0xA8 || buf[pos + 2] == 0xA9))
        {
            // LS|PS . LS|PS
            s.AddRange(buf, pos, 3);
            bufferPos += 3;
        }
        else
        {
            return;
        }
        mark.Index++;
        mark.Column = 0;
        mark.Line++;
        unread--;
    }

    // Set the scanner error and return false.
    private bool SetScannerError(string context, YamlMark contextMark, string problem)
    {
        Error = YamlErrorType.Scanner;
        Context = context;
        ContextMark = contextMark;
        Problem = problem;
        ProblemMark = mark;
        return false;
    }

    private bool SetScannerTagError(bool directive, YamlMark contextMark, string problem)
    {
        string context = directive ? "while parsing a %TAG directive" : "while parsing a tag";
        return SetScannerError(context, contextMark, problem);
    }

    // Ensure that the tokens queue contains at least one token which can be returned to the Parser.
    private bool FetchMoreTokens()
    {
        // While we need more tokens to fetch, do it.
        while (true)
        {
            if (tokensHead != tokens.Count)
            {
                // If queue is non-empty, check if any potential simple key may occupy the head position.
                if (!simpleKeysByTok.TryGetValue(tokensParsed, out int headTokIdx))
                {
                    break;
                }
                if (!SimpleKeyIsValid(simpleKeys[headTokIdx], out bool valid))
                {
                    return false;
                }
                if (!valid)
                {
                    break;
                }
            }
            // Fetch the next token.
            if (!FetchNextToken())
            {
                return false;
            }
        }

        tokenAvailable = true;
        return true;
    }

    // The dispatcher for token fetchers.
    private bool FetchNextToken()
    {
        // Ensure that the buffer is initialized.
        if (!Cache(1))
        {
            return false;
        }

        // Check if we just started scanning.  Fetch STREAM-START then.
        if (!streamStartProduced)
        {
            return FetchStreamStart();
        }

        // Eat whitespaces and comments until we reach the next token.
        if (!ScanToNextToken())
        {
            return false;
        }

        // Check the indentation level against the current column.
        if (!UnrollIndent(mark.Column))
        {
            return false;
        }

        // Ensure that the buffer contains at least 4 characters.  4 is the length
        // of the longest indicators ('--- ' and '... ').
        if (!Cache(4))
        {
            return false;
        }

        byte[] buf = buffer;
        int pos = bufferPos;

        // Is it the end of the stream?
        if (IsZ(buf, pos))
        {
            return FetchStreamEnd();
        }

        // Is it a directive?
        if (mark.Column == 0 && buf[pos] == '%')
        {
            return FetchDirective();
        }

        // Is it the document start indicator?
        if (mark.Column == 0 && buf[pos] == '-' && buf[pos + 1] == '-' && buf[pos + 2] == '-' && IsBlankZ(buf, pos + 3))
        {
            return FetchDocumentIndicator(YamlTokenType.DocumentStart);
        }

        // Is it the document end indicator?
        if (mark.Column == 0 && buf[pos] == '.' && buf[pos + 1] == '.' && buf[pos + 2] == '.' && IsBlankZ(buf, pos + 3))
        {
            return FetchDocumentIndicator(YamlTokenType.DocumentEnd);
        }

        byte c = buf[pos];
        switch (c)
        {
            case (byte)'[':
                return FetchFlowCollectionStart(YamlTokenType.FlowSequenceStart);
            case (byte)'{':
                return FetchFlowCollectionStart(YamlTokenType.FlowMappingStart);
            case (byte)']':
                return FetchFlowCollectionEnd(YamlTokenType.FlowSequenceEnd);
            case (byte)'}':
                return FetchFlowCollectionEnd(YamlTokenType.FlowMappingEnd);
            case (byte)',':
                return FetchFlowEntry();
        }

        // Is it the block entry indicator?
        if (c == '-' && IsBlankZ(buf, pos + 1))
        {
            return FetchBlockEntry();
        }

        // Is it the key indicator?
        if (c == '?' && (flowLevel > 0 || IsBlankZ(buf, pos + 1)))
        {
            return FetchKey();
        }

        // Is it the value indicator?
        if (c == ':' && (flowLevel > 0 || IsBlankZ(buf, pos + 1)))
        {
            return FetchValue();
        }

        switch (c)
        {
            case (byte)'*':
                return FetchAnchor(YamlTokenType.Alias);
            case (byte)'&':
                return FetchAnchor(YamlTokenType.Anchor);
            case (byte)'!':
                return FetchTag();
            case (byte)'|' when flowLevel == 0:
                return FetchBlockScalar(true);
            case (byte)'>' when flowLevel == 0:
                return FetchBlockScalar(false);
            case (byte)'\'':
                return FetchFlowScalar(true);
            case (byte)'"':
                return FetchFlowScalar(false);
        }

        // Is it a plain scalar?
        //
        // A plain scalar may start with any non-blank characters except
        //
        //      '-', '?', ':', ',', '[', ']', '{', '}',
        //      '#', '&', '*', '!', '|', '>', '\'', '\"',
        //      '%', '@', '`'.
        //
        // In the block context (and, for the '-' indicator, in the flow context
        // too), it may also start with the characters
        //
        //      '-', '?', ':'
        //
        // if it is followed by a non-space character.
        //
        // The last rule is more restrictive than the specification requires.
        if (!(IsBlankZ(buf, pos) || c == '-' || c == '?' || c == ':' || c == ',' || c == '[' || c == ']' || c == '{' ||
              c == '}' || c == '#' || c == '&' || c == '*' || c == '!' || c == '|' || c == '>' || c == '\'' ||
              c == '"' || c == '%' || c == '@' || c == '`') ||
            (c == '-' && !IsBlank(buf, pos + 1)) ||
            (flowLevel == 0 && (c == '?' || c == ':') && !IsBlankZ(buf, pos + 1)))
        {
            return FetchPlainScalar();
        }

        // If we don't determine the token type so far, it is an error.
        return SetScannerError("while scanning for the next token", mark, "found character that cannot start any token");
    }

    private bool SimpleKeyIsValid(YamlSimpleKey simpleKey, out bool valid)
    {
        valid = false;
        if (!simpleKey.Possible)
        {
            return true;
        }

        // The 1.2 specification says:
        //
        //     "If the ? indicator is omitted, parsing needs to see past the
        //     implicit key to recognize it as such. To limit the amount of
        //     lookahead required, the ":" indicator must appear at most 1024
        //     Unicode characters beyond the start of the key. In addition, the key
        //     is restricted to a single line."
        if (simpleKey.Mark.Line < mark.Line || simpleKey.Mark.Index + 1024 < mark.Index)
        {
            // Check if the potential simple key to be removed is required.
            if (simpleKey.Required)
            {
                return SetScannerError("while scanning a simple key", simpleKey.Mark, "could not find expected ':'");
            }
            simpleKey.Possible = false;
            return true;
        }
        valid = true;
        return true;
    }

    // Check if a simple key may start at the current position and add it if needed.
    private bool SaveSimpleKey()
    {
        // A simple key is required at the current position if the scanner is in
        // the block context and the current column coincides with the indentation level.
        bool required = flowLevel == 0 && indent == mark.Column;

        // If the current position may start a simple key, save it.
        if (simpleKeyAllowed)
        {
            var simpleKey = new YamlSimpleKey
            {
                Possible = true,
                Required = required,
                TokenNumber = tokensParsed + (tokens.Count - tokensHead),
                Mark = mark,
            };

            if (!RemoveSimpleKey())
            {
                return false;
            }
            simpleKeys[simpleKeys.Count - 1] = simpleKey;
            simpleKeysByTok[simpleKey.TokenNumber] = simpleKeys.Count - 1;
        }
        return true;
    }

    // Remove a potential simple key at the current flow level.
    private bool RemoveSimpleKey()
    {
        YamlSimpleKey key = simpleKeys[simpleKeys.Count - 1];
        if (key.Possible)
        {
            // If the key is required, it is an error.
            if (key.Required)
            {
                return SetScannerError("while scanning a simple key", key.Mark, "could not find expected ':'");
            }
            // Remove the key from the stack.
            key.Possible = false;
            simpleKeysByTok.Remove(key.TokenNumber);
        }
        return true;
    }

    // Increase the flow level and resize the simple key list if needed.
    private bool IncreaseFlowLevel()
    {
        // Reset the simple key on the next level.
        simpleKeys.Add(new YamlSimpleKey
        {
            TokenNumber = tokensParsed + (tokens.Count - tokensHead),
            Mark = mark,
        });

        // Increase the flow level.
        flowLevel++;
        if (flowLevel > MaxFlowLevel)
        {
            return SetScannerError("while increasing flow level", simpleKeys[simpleKeys.Count - 1].Mark,
                "exceeded max depth of " + MaxFlowLevel.ToString(CultureInfo.InvariantCulture));
        }
        return true;
    }

    // Decrease the flow level.
    private bool DecreaseFlowLevel()
    {
        if (flowLevel > 0)
        {
            flowLevel--;
            int last = simpleKeys.Count - 1;
            simpleKeysByTok.Remove(simpleKeys[last].TokenNumber);
            simpleKeys.RemoveAt(last);
        }
        return true;
    }

    // Push the current indentation level to the stack and set the new level
    // the current column is greater than the indentation level.  In this case,
    // append or insert the specified token into the token queue.
    private bool RollIndent(int column, int number, YamlTokenType type, YamlMark tokenMark)
    {
        // In the flow context, do nothing.
        if (flowLevel > 0)
        {
            return true;
        }

        if (indent < column)
        {
            // Push the current indentation level to the stack and set the new indentation level.
            indents.Add(indent);
            indent = column;
            if (indents.Count > MaxIndents)
            {
                return SetScannerError("while increasing indent level", simpleKeys[simpleKeys.Count - 1].Mark,
                    "exceeded max depth of " + MaxIndents.ToString(CultureInfo.InvariantCulture));
            }

            // Create a token and insert it into the queue.
            var token = new YamlToken { Type = type, StartMark = tokenMark, EndMark = tokenMark };
            if (number > -1)
            {
                number -= tokensParsed;
            }
            InsertToken(number, token);
        }
        return true;
    }

    // Pop indentation levels from the indents stack until the current level
    // becomes less or equal to the column.  For each indentation level, append
    // the BLOCK-END token.
    private bool UnrollIndent(int column)
    {
        // In the flow context, do nothing.
        if (flowLevel > 0)
        {
            return true;
        }

        // Loop through the indentation levels in the stack.
        while (indent > column)
        {
            // Create a token and append it to the queue.
            InsertToken(-1, new YamlToken { Type = YamlTokenType.BlockEnd, StartMark = mark, EndMark = mark });

            // Pop the indentation level.
            indent = Pop(indents);
        }
        return true;
    }

    // Initialize the scanner and produce the STREAM-START token.
    private bool FetchStreamStart()
    {
        // Set the initial indentation.
        indent = -1;

        // Initialize the simple key stack.
        simpleKeys.Add(new YamlSimpleKey());

        simpleKeysByTok = new Dictionary<int, int>();

        // A simple key is allowed at the beginning of the stream.
        simpleKeyAllowed = true;

        // We have started.
        streamStartProduced = true;

        // Create the STREAM-START token and append it to the queue.
        InsertToken(-1, new YamlToken { Type = YamlTokenType.StreamStart, StartMark = mark, EndMark = mark });
        return true;
    }

    // Produce the STREAM-END token and shut down the scanner.
    private bool FetchStreamEnd()
    {
        // Force new line.
        if (mark.Column != 0)
        {
            mark.Column = 0;
            mark.Line++;
        }

        // Reset the indentation level.
        if (!UnrollIndent(-1))
        {
            return false;
        }

        // Reset simple keys.
        if (!RemoveSimpleKey())
        {
            return false;
        }

        simpleKeyAllowed = false;

        // Create the STREAM-END token and append it to the queue.
        InsertToken(-1, new YamlToken { Type = YamlTokenType.StreamEnd, StartMark = mark, EndMark = mark });
        return true;
    }

    // Produce a VERSION-DIRECTIVE or TAG-DIRECTIVE token.
    private bool FetchDirective()
    {
        // Reset the indentation level.
        if (!UnrollIndent(-1))
        {
            return false;
        }

        // Reset simple keys.
        if (!RemoveSimpleKey())
        {
            return false;
        }

        simpleKeyAllowed = false;

        // Create the YAML-DIRECTIVE or TAG-DIRECTIVE token.
        var token = new YamlToken();
        if (!ScanDirective(token))
        {
            return false;
        }
        // Append the token to the queue.
        InsertToken(-1, token);
        return true;
    }

    // Produce the DOCUMENT-START or DOCUMENT-END token.
    private bool FetchDocumentIndicator(YamlTokenType type)
    {
        // Reset the indentation level.
        if (!UnrollIndent(-1))
        {
            return false;
        }

        // Reset simple keys.
        if (!RemoveSimpleKey())
        {
            return false;
        }

        simpleKeyAllowed = false;

        // Consume the token.
        YamlMark startMark = mark;

        Skip();
        Skip();
        Skip();

        YamlMark endMark = mark;

        // Create the DOCUMENT-START or DOCUMENT-END token.
        InsertToken(-1, new YamlToken { Type = type, StartMark = startMark, EndMark = endMark });
        return true;
    }

    // Produce the FLOW-SEQUENCE-START or FLOW-MAPPING-START token.
    private bool FetchFlowCollectionStart(YamlTokenType type)
    {
        // The indicators '[' and '{' may start a simple key.
        if (!SaveSimpleKey())
        {
            return false;
        }

        // Increase the flow level.
        if (!IncreaseFlowLevel())
        {
            return false;
        }

        // A simple key may follow the indicators '[' and '{'.
        simpleKeyAllowed = true;

        // Consume the token.
        YamlMark startMark = mark;
        Skip();
        YamlMark endMark = mark;

        // Create the FLOW-SEQUENCE-START of FLOW-MAPPING-START token.
        InsertToken(-1, new YamlToken { Type = type, StartMark = startMark, EndMark = endMark });
        return true;
    }

    // Produce the FLOW-SEQUENCE-END or FLOW-MAPPING-END token.
    private bool FetchFlowCollectionEnd(YamlTokenType type)
    {
        // Reset any potential simple key on the current flow level.
        if (!RemoveSimpleKey())
        {
            return false;
        }

        // Decrease the flow level.
        if (!DecreaseFlowLevel())
        {
            return false;
        }

        // No simple keys after the indicators ']' and '}'.
        simpleKeyAllowed = false;

        // Consume the token.
        YamlMark startMark = mark;
        Skip();
        YamlMark endMark = mark;

        // Create the FLOW-SEQUENCE-END of FLOW-MAPPING-END token.
        InsertToken(-1, new YamlToken { Type = type, StartMark = startMark, EndMark = endMark });
        return true;
    }

    // Produce the FLOW-ENTRY token.
    private bool FetchFlowEntry()
    {
        // Reset any potential simple keys on the current flow level.
        if (!RemoveSimpleKey())
        {
            return false;
        }

        // Simple keys are allowed after ','.
        simpleKeyAllowed = true;

        // Consume the token.
        YamlMark startMark = mark;
        Skip();
        YamlMark endMark = mark;

        // Create the FLOW-ENTRY token and append it to the queue.
        InsertToken(-1, new YamlToken { Type = YamlTokenType.FlowEntry, StartMark = startMark, EndMark = endMark });
        return true;
    }

    // Produce the BLOCK-ENTRY token.
    private bool FetchBlockEntry()
    {
        // Check if the scanner is in the block context.
        if (flowLevel == 0)
        {
            // Check if we are allowed to start a new entry.
            if (!simpleKeyAllowed)
            {
                return SetScannerError("", mark, "block sequence entries are not allowed in this context");
            }
            // Add the BLOCK-SEQUENCE-START token if needed.
            if (!RollIndent(mark.Column, -1, YamlTokenType.BlockSequenceStart, mark))
            {
                return false;
            }
        }
        // It is an error for the '-' indicator to occur in the flow context,
        // but we let the Parser detect and report about it because the Parser
        // is able to point to the context.

        // Reset any potential simple keys on the current flow level.
        if (!RemoveSimpleKey())
        {
            return false;
        }

        // Simple keys are allowed after '-'.
        simpleKeyAllowed = true;

        // Consume the token.
        YamlMark startMark = mark;
        Skip();
        YamlMark endMark = mark;

        // Create the BLOCK-ENTRY token and append it to the queue.
        InsertToken(-1, new YamlToken { Type = YamlTokenType.BlockEntry, StartMark = startMark, EndMark = endMark });
        return true;
    }

    // Produce the KEY token.
    private bool FetchKey()
    {
        // In the block context, additional checks are required.
        if (flowLevel == 0)
        {
            // Check if we are allowed to start a new key (not nessesary simple).
            if (!simpleKeyAllowed)
            {
                return SetScannerError("", mark, "mapping keys are not allowed in this context");
            }
            // Add the BLOCK-MAPPING-START token if needed.
            if (!RollIndent(mark.Column, -1, YamlTokenType.BlockMappingStart, mark))
            {
                return false;
            }
        }

        // Reset any potential simple keys on the current flow level.
        if (!RemoveSimpleKey())
        {
            return false;
        }

        // Simple keys are allowed after '?' in the block context.
        simpleKeyAllowed = flowLevel == 0;

        // Consume the token.
        YamlMark startMark = mark;
        Skip();
        YamlMark endMark = mark;

        // Create the KEY token and append it to the queue.
        InsertToken(-1, new YamlToken { Type = YamlTokenType.Key, StartMark = startMark, EndMark = endMark });
        return true;
    }

    // Produce the VALUE token.
    private bool FetchValue()
    {
        YamlSimpleKey simpleKey = simpleKeys[simpleKeys.Count - 1];

        // Have we found a simple key?
        if (!SimpleKeyIsValid(simpleKey, out bool valid))
        {
            return false;
        }
        if (valid)
        {
            // Create the KEY token and insert it into the queue.
            InsertToken(simpleKey.TokenNumber - tokensParsed,
                new YamlToken { Type = YamlTokenType.Key, StartMark = simpleKey.Mark, EndMark = simpleKey.Mark });

            // In the block context, we may need to add the BLOCK-MAPPING-START token.
            if (!RollIndent(simpleKey.Mark.Column, simpleKey.TokenNumber, YamlTokenType.BlockMappingStart, simpleKey.Mark))
            {
                return false;
            }

            // Remove the simple key.
            simpleKey.Possible = false;
            simpleKeysByTok.Remove(simpleKey.TokenNumber);

            // A simple key cannot follow another simple key.
            simpleKeyAllowed = false;
        }
        else
        {
            // The ':' indicator follows a complex key.

            // In the block context, extra checks are required.
            if (flowLevel == 0)
            {
                // Check if we are allowed to start a complex value.
                if (!simpleKeyAllowed)
                {
                    return SetScannerError("", mark, "mapping values are not allowed in this context");
                }

                // Add the BLOCK-MAPPING-START token if needed.
                if (!RollIndent(mark.Column, -1, YamlTokenType.BlockMappingStart, mark))
                {
                    return false;
                }
            }

            // Simple keys after ':' are allowed in the block context.
            simpleKeyAllowed = flowLevel == 0;
        }

        // Consume the token.
        YamlMark startMark = mark;
        Skip();
        YamlMark endMark = mark;

        // Create the VALUE token and append it to the queue.
        InsertToken(-1, new YamlToken { Type = YamlTokenType.Value, StartMark = startMark, EndMark = endMark });
        return true;
    }

    // Produce the ALIAS or ANCHOR token.
    private bool FetchAnchor(YamlTokenType type)
    {
        // An anchor or an alias could be a simple key.
        if (!SaveSimpleKey())
        {
            return false;
        }

        // A simple key cannot follow an anchor or an alias.
        simpleKeyAllowed = false;

        // Create the ALIAS or ANCHOR token and append it to the queue.
        var token = new YamlToken();
        if (!ScanAnchor(token, type))
        {
            return false;
        }
        InsertToken(-1, token);
        return true;
    }

    // Produce the TAG token.
    private bool FetchTag()
    {
        // A tag could be a simple key.
        if (!SaveSimpleKey())
        {
            return false;
        }

        // A simple key cannot follow a tag.
        simpleKeyAllowed = false;

        // Create the TAG token and append it to the queue.
        var token = new YamlToken();
        if (!ScanTag(token))
        {
            return false;
        }
        InsertToken(-1, token);
        return true;
    }

    // Produce the SCALAR(...,literal) or SCALAR(...,folded) tokens.
    private bool FetchBlockScalar(bool literal)
    {
        // Remove any potential simple keys.
        if (!RemoveSimpleKey())
        {
            return false;
        }

        // A simple key may follow a block scalar.
        simpleKeyAllowed = true;

        // Create the SCALAR token and append it to the queue.
        var token = new YamlToken();
        if (!ScanBlockScalar(token, literal))
        {
            return false;
        }
        InsertToken(-1, token);
        return true;
    }

    // Produce the SCALAR(...,single-quoted) or SCALAR(...,double-quoted) tokens.
    private bool FetchFlowScalar(bool single)
    {
        // A plain scalar could be a simple key.
        if (!SaveSimpleKey())
        {
            return false;
        }

        // A simple key cannot follow a flow scalar.
        simpleKeyAllowed = false;

        // Create the SCALAR token and append it to the queue.
        var token = new YamlToken();
        if (!ScanFlowScalar(token, single))
        {
            return false;
        }
        InsertToken(-1, token);
        return true;
    }

    // Produce the SCALAR(...,plain) token.
    private bool FetchPlainScalar()
    {
        // A plain scalar could be a simple key.
        if (!SaveSimpleKey())
        {
            return false;
        }

        // A simple key cannot follow a flow scalar.
        simpleKeyAllowed = false;

        // Create the SCALAR token and append it to the queue.
        var token = new YamlToken();
        if (!ScanPlainScalar(token))
        {
            return false;
        }
        InsertToken(-1, token);
        return true;
    }

    // Eat whitespaces and comments until the next token is found.
    private bool ScanToNextToken()
    {
        // Until the next token is not found.
        while (true)
        {
            // Allow the BOM mark to start a line.
            if (!Cache(1))
            {
                return false;
            }
            if (mark.Column == 0 && IsBom(buffer))
            {
                Skip();
            }

            // Eat whitespaces.
            // Tabs are allowed:
            //  - in the flow context
            //  - in the block context, but not at the beginning of the line or
            //  after '-', '?', or ':' (complex value).
            if (!Cache(1))
            {
                return false;
            }

            while (buffer[bufferPos] == ' ' || ((flowLevel > 0 || !simpleKeyAllowed) && buffer[bufferPos] == '\t'))
            {
                Skip();
                if (!Cache(1))
                {
                    return false;
                }
            }

            // Eat a comment until a line break.
            if (buffer[bufferPos] == '#')
            {
                while (!IsBreakZ(buffer, bufferPos))
                {
                    Skip();
                    if (!Cache(1))
                    {
                        return false;
                    }
                }
            }

            // If it is a line break, eat it.
            if (IsBreak(buffer, bufferPos))
            {
                if (!Cache(2))
                {
                    return false;
                }
                SkipLine();

                // In the block context, a new line may start a simple key.
                if (flowLevel == 0)
                {
                    simpleKeyAllowed = true;
                }
            }
            else
            {
                break; // We have found a token.
            }
        }

        return true;
    }

    // Scan a YAML-DIRECTIVE or TAG-DIRECTIVE token.
    //
    // Scope:
    //      %YAML    1.1    # a comment \n
    //      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
    //      %TAG    !yaml!  tag:yaml.org,2002:  \n
    //      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
    private bool ScanDirective(YamlToken token)
    {
        // Eat '%'.
        YamlMark startMark = mark;
        Skip();

        // Scan the directive name.
        if (!ScanDirectiveName(startMark, out byte[] name))
        {
            return false;
        }

        // Is it a YAML directive?
        if (BytesEqual(name, "YAML"))
        {
            // Scan the VERSION directive value.
            if (!ScanVersionDirectiveValue(startMark, out int major, out int minor))
            {
                return false;
            }
            YamlMark endMark = mark;

            // Create a VERSION-DIRECTIVE token.
            token.Type = YamlTokenType.VersionDirective;
            token.StartMark = startMark;
            token.EndMark = endMark;
            token.Major = major;
            token.Minor = minor;
        }
        else if (BytesEqual(name, "TAG"))
        {
            // Scan the TAG directive value.
            if (!ScanTagDirectiveValue(startMark, out byte[] handle, out byte[] prefix))
            {
                return false;
            }
            YamlMark endMark = mark;

            // Create a TAG-DIRECTIVE token.
            token.Type = YamlTokenType.TagDirective;
            token.StartMark = startMark;
            token.EndMark = endMark;
            token.Value = handle;
            token.Prefix = prefix;
        }
        else
        {
            // Unknown directive.
            SetScannerError("while scanning a directive", startMark, "found unknown directive name");
            return false;
        }

        // Eat the rest of the line including any comments.
        if (!Cache(1))
        {
            return false;
        }

        while (IsBlank(buffer, bufferPos))
        {
            Skip();
            if (!Cache(1))
            {
                return false;
            }
        }

        if (buffer[bufferPos] == '#')
        {
            while (!IsBreakZ(buffer, bufferPos))
            {
                Skip();
                if (!Cache(1))
                {
                    return false;
                }
            }
        }

        // Check if we are at the end of the line.
        if (!IsBreakZ(buffer, bufferPos))
        {
            SetScannerError("while scanning a directive", startMark, "did not find expected comment or line break");
            return false;
        }

        // Eat a line break.
        if (IsBreak(buffer, bufferPos))
        {
            if (!Cache(2))
            {
                return false;
            }
            SkipLine();
        }

        return true;
    }

    private static bool BytesEqual(byte[] a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    // Scan the directive name.
    //
    // Scope:
    //      %YAML   1.1     # a comment \n
    //       ^^^^
    //      %TAG    !yaml!  tag:yaml.org,2002:  \n
    //       ^^^
    private bool ScanDirectiveName(YamlMark startMark, out byte[] name)
    {
        name = Array.Empty<byte>();

        // Consume the directive name.
        if (!Cache(1))
        {
            return false;
        }

        var s = new YamlBytes();
        while (IsAlpha(buffer, bufferPos))
        {
            Read(s);
            if (!Cache(1))
            {
                return false;
            }
        }

        // Check if the name is empty.
        if (s.Length == 0)
        {
            SetScannerError("while scanning a directive", startMark, "could not find expected directive name");
            return false;
        }

        // Check for an blank character after the name.
        if (!IsBlankZ(buffer, bufferPos))
        {
            SetScannerError("while scanning a directive", startMark, "found unexpected non-alphabetical character");
            return false;
        }
        name = s.ToArray();
        return true;
    }

    // Scan the value of VERSION-DIRECTIVE.
    //
    // Scope:
    //      %YAML   1.1     # a comment \n
    //           ^^^^^^
    private bool ScanVersionDirectiveValue(YamlMark startMark, out int major, out int minor)
    {
        major = 0;
        minor = 0;

        // Eat whitespaces.
        if (!Cache(1))
        {
            return false;
        }
        while (IsBlank(buffer, bufferPos))
        {
            Skip();
            if (!Cache(1))
            {
                return false;
            }
        }

        // Consume the major version number.
        if (!ScanVersionDirectiveNumber(startMark, out major))
        {
            return false;
        }

        // Eat '.'.
        if (buffer[bufferPos] != '.')
        {
            return SetScannerError("while scanning a %YAML directive", startMark, "did not find expected digit or '.' character");
        }

        Skip();

        // Consume the minor version number.
        return ScanVersionDirectiveNumber(startMark, out minor);
    }

    // Scan the version number of VERSION-DIRECTIVE.
    //
    // Scope:
    //      %YAML   1.1     # a comment \n
    //              ^
    //      %YAML   1.1     # a comment \n
    //                ^
    private bool ScanVersionDirectiveNumber(YamlMark startMark, out int number)
    {
        number = 0;

        // Repeat while the next character is digit.
        if (!Cache(1))
        {
            return false;
        }
        int value = 0;
        int length = 0;
        while (IsDigit(buffer, bufferPos))
        {
            // Check if the number is too long.
            length++;
            if (length > MaxNumberLength)
            {
                return SetScannerError("while scanning a %YAML directive", startMark, "found extremely long version number");
            }
            value = value * 10 + AsDigit(buffer, bufferPos);
            Skip();
            if (!Cache(1))
            {
                return false;
            }
        }

        // Check if the number was present.
        if (length == 0)
        {
            return SetScannerError("while scanning a %YAML directive", startMark, "did not find expected version number");
        }
        number = value;
        return true;
    }

    // Scan the value of a TAG-DIRECTIVE token.
    //
    // Scope:
    //      %TAG    !yaml!  tag:yaml.org,2002:  \n
    //          ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
    private bool ScanTagDirectiveValue(YamlMark startMark, out byte[] handle, out byte[] prefix)
    {
        handle = Array.Empty<byte>();
        prefix = Array.Empty<byte>();

        // Eat whitespaces.
        if (!Cache(1))
        {
            return false;
        }

        while (IsBlank(buffer, bufferPos))
        {
            Skip();
            if (!Cache(1))
            {
                return false;
            }
        }

        // Scan a handle.
        if (!ScanTagHandle(true, startMark, out byte[] handleValue))
        {
            return false;
        }

        // Expect a whitespace.
        if (!Cache(1))
        {
            return false;
        }
        if (!IsBlank(buffer, bufferPos))
        {
            SetScannerError("while scanning a %TAG directive", startMark, "did not find expected whitespace");
            return false;
        }

        // Eat whitespaces.
        while (IsBlank(buffer, bufferPos))
        {
            Skip();
            if (!Cache(1))
            {
                return false;
            }
        }

        // Scan a prefix.
        if (!ScanTagUri(true, null, startMark, out byte[] prefixValue))
        {
            return false;
        }

        // Expect a whitespace or line break.
        if (!Cache(1))
        {
            return false;
        }
        if (!IsBlankZ(buffer, bufferPos))
        {
            SetScannerError("while scanning a %TAG directive", startMark, "did not find expected whitespace or line break");
            return false;
        }

        handle = handleValue;
        prefix = prefixValue;
        return true;
    }

    private bool ScanAnchor(YamlToken token, YamlTokenType type)
    {
        var s = new YamlBytes();

        // Eat the indicator character.
        YamlMark startMark = mark;
        Skip();

        // Consume the value.
        if (!Cache(1))
        {
            return false;
        }

        while (IsAlpha(buffer, bufferPos))
        {
            Read(s);
            if (!Cache(1))
            {
                return false;
            }
        }

        YamlMark endMark = mark;

        // Check if length of the anchor is greater than 0 and it is followed by
        // a whitespace character or one of the indicators:
        //
        //      '?', ':', ',', ']', '}', '%', '@', '`'.
        byte c = buffer[bufferPos];
        if (s.Length == 0 ||
            !(IsBlankZ(buffer, bufferPos) || c == '?' || c == ':' || c == ',' || c == ']' || c == '}' || c == '%' ||
              c == '@' || c == '`'))
        {
            string context = type == YamlTokenType.Anchor ? "while scanning an anchor" : "while scanning an alias";
            SetScannerError(context, startMark, "did not find expected alphabetic or numeric character");
            return false;
        }

        // Create a token.
        token.Type = type;
        token.StartMark = startMark;
        token.EndMark = endMark;
        token.Value = s.ToArray();
        return true;
    }

    // Scan a TAG token.
    private bool ScanTag(YamlToken token)
    {
        byte[] handle;
        byte[] suffix;

        YamlMark startMark = mark;

        // Check if the tag is in the canonical form.
        if (!Cache(2))
        {
            return false;
        }

        if (buffer[bufferPos + 1] == '<')
        {
            // Keep the handle as ''
            handle = Array.Empty<byte>();

            // Eat '!<'
            Skip();
            Skip();

            // Consume the tag value.
            if (!ScanTagUri(false, null, startMark, out suffix))
            {
                return false;
            }

            // Check for '>' and eat it.
            if (buffer[bufferPos] != '>')
            {
                SetScannerError("while scanning a tag", startMark, "did not find the expected '>'");
                return false;
            }

            Skip();
        }
        else
        {
            // The tag has either the '!suffix' or the '!handle!suffix' form.

            // First, try to scan a handle.
            if (!ScanTagHandle(false, startMark, out handle))
            {
                return false;
            }

            // Check if it is, indeed, handle.
            if (handle[0] == '!' && handle.Length > 1 && handle[handle.Length - 1] == '!')
            {
                // Scan the suffix now.
                if (!ScanTagUri(false, null, startMark, out suffix))
                {
                    return false;
                }
            }
            else
            {
                // It wasn't a handle after all.  Scan the rest of the tag.
                if (!ScanTagUri(false, handle, startMark, out suffix))
                {
                    return false;
                }

                // Set the handle to '!'.
                handle = new[] { (byte)'!' };

                // A special case: the '!' tag.  Set the handle to '' and the suffix to '!'.
                if (suffix.Length == 0)
                {
                    (handle, suffix) = (suffix, handle);
                }
            }
        }

        // Check the character which ends the tag.
        if (!Cache(1))
        {
            return false;
        }
        if (!IsBlankZ(buffer, bufferPos))
        {
            SetScannerError("while scanning a tag", startMark, "did not find expected whitespace or line break");
            return false;
        }

        YamlMark endMark = mark;

        // Create a token.
        token.Type = YamlTokenType.Tag;
        token.StartMark = startMark;
        token.EndMark = endMark;
        token.Value = handle;
        token.Suffix = suffix;
        return true;
    }

    // Scan a tag handle.
    private bool ScanTagHandle(bool directive, YamlMark startMark, out byte[] handle)
    {
        handle = Array.Empty<byte>();

        // Check the initial '!' character.
        if (!Cache(1))
        {
            return false;
        }
        if (buffer[bufferPos] != '!')
        {
            SetScannerTagError(directive, startMark, "did not find expected '!'");
            return false;
        }

        var s = new YamlBytes();

        // Copy the '!' character.
        Read(s);

        // Copy all subsequent alphabetical and numerical characters.
        if (!Cache(1))
        {
            return false;
        }
        while (IsAlpha(buffer, bufferPos))
        {
            Read(s);
            if (!Cache(1))
            {
                return false;
            }
        }

        // Check if the trailing character is '!' and copy it.
        if (buffer[bufferPos] == '!')
        {
            Read(s);
        }
        else
        {
            // It's either the '!' tag or not really a tag handle.  If it's a %TAG
            // directive, it's an error.  If it's a tag token, it must be a part of URI.
            if (directive && !(s.Length == 1 && s[0] == '!'))
            {
                SetScannerTagError(directive, startMark, "did not find expected '!'");
                return false;
            }
        }

        handle = s.ToArray();
        return true;
    }

    // Scan a tag.
    private bool ScanTagUri(bool directive, byte[]? head, YamlMark startMark, out byte[] uri)
    {
        uri = Array.Empty<byte>();
        var s = new YamlBytes();
        bool hasTag = head != null && head.Length > 0;

        // Copy the head if needed.
        //
        // Note that we don't copy the leading '!' character.
        if (head != null && head.Length > 1)
        {
            s.AddRange(head, 1, head.Length - 1);
        }

        // Scan the tag.
        if (!Cache(1))
        {
            return false;
        }

        // The set of characters that may appear in URI is as follows:
        //
        //      '0'-'9', 'A'-'Z', 'a'-'z', '_', '-', ';', '/', '?', ':', '@', '&',
        //      '=', '+', '$', ',', '.', '!', '~', '*', '\'', '(', ')', '[', ']',
        //      '%'.
        while (IsUriChar(buffer[bufferPos]) || IsAlpha(buffer, bufferPos))
        {
            // Check if it is a URI-escape sequence.
            if (buffer[bufferPos] == '%')
            {
                if (!ScanUriEscapes(directive, startMark, s))
                {
                    return false;
                }
            }
            else
            {
                Read(s);
            }
            if (!Cache(1))
            {
                return false;
            }
            hasTag = true;
        }

        if (!hasTag)
        {
            SetScannerTagError(directive, startMark, "did not find expected tag URI");
            return false;
        }
        uri = s.ToArray();
        return true;
    }

    private static bool IsUriChar(byte c) =>
        c == ';' || c == '/' || c == '?' || c == ':' || c == '@' || c == '&' || c == '=' || c == '+' || c == '$' ||
        c == ',' || c == '.' || c == '!' || c == '~' || c == '*' || c == '\'' || c == '(' || c == ')' || c == '[' ||
        c == ']' || c == '%';

    // Decode an URI-escape sequence corresponding to a single UTF-8 character.
    private bool ScanUriEscapes(bool directive, YamlMark startMark, YamlBytes s)
    {
        // Decode the required number of characters.
        int w = 1024;
        while (w > 0)
        {
            // Check for a URI-escaped octet.
            if (!Cache(3))
            {
                return false;
            }

            if (!(buffer[bufferPos] == '%' && IsHex(buffer, bufferPos + 1) && IsHex(buffer, bufferPos + 2)))
            {
                return SetScannerTagError(directive, startMark, "did not find URI escaped octet");
            }

            // Get the octet.
            byte octet = (byte)((AsHex(buffer, bufferPos + 1) << 4) + AsHex(buffer, bufferPos + 2));

            // If it is the leading octet, determine the length of the UTF-8 sequence.
            if (w == 1024)
            {
                w = Width(octet);
                if (w == 0)
                {
                    return SetScannerTagError(directive, startMark, "found an incorrect leading UTF-8 octet");
                }
            }
            else
            {
                // Check if the trailing octet is correct.
                if ((octet & 0xC0) != 0x80)
                {
                    return SetScannerTagError(directive, startMark, "found an incorrect trailing UTF-8 octet");
                }
            }

            // Copy the octet and move the pointers.
            s.Add(octet);
            Skip();
            Skip();
            Skip();
            w--;
        }
        return true;
    }

    // Scan a block scalar.
    private bool ScanBlockScalar(YamlToken token, bool literal)
    {
        // Eat the indicator '|' or '>'.
        YamlMark startMark = mark;
        Skip();

        // Scan the additional block scalar indicators.
        if (!Cache(1))
        {
            return false;
        }

        // Check for a chomping indicator.
        int chomping = 0;
        int increment = 0;
        if (buffer[bufferPos] == '+' || buffer[bufferPos] == '-')
        {
            // Set the chomping method and eat the indicator.
            chomping = buffer[bufferPos] == '+' ? +1 : -1;
            Skip();

            // Check for an indentation indicator.
            if (!Cache(1))
            {
                return false;
            }
            if (IsDigit(buffer, bufferPos))
            {
                // Check that the indentation is greater than 0.
                if (buffer[bufferPos] == '0')
                {
                    SetScannerError("while scanning a block scalar", startMark, "found an indentation indicator equal to 0");
                    return false;
                }

                // Get the indentation level and eat the indicator.
                increment = AsDigit(buffer, bufferPos);
                Skip();
            }
        }
        else if (IsDigit(buffer, bufferPos))
        {
            // Do the same as above, but in the opposite order.

            if (buffer[bufferPos] == '0')
            {
                SetScannerError("while scanning a block scalar", startMark, "found an indentation indicator equal to 0");
                return false;
            }
            increment = AsDigit(buffer, bufferPos);
            Skip();

            if (!Cache(1))
            {
                return false;
            }
            if (buffer[bufferPos] == '+' || buffer[bufferPos] == '-')
            {
                chomping = buffer[bufferPos] == '+' ? +1 : -1;
                Skip();
            }
        }

        // Eat whitespaces and comments to the end of the line.
        if (!Cache(1))
        {
            return false;
        }
        while (IsBlank(buffer, bufferPos))
        {
            Skip();
            if (!Cache(1))
            {
                return false;
            }
        }
        if (buffer[bufferPos] == '#')
        {
            while (!IsBreakZ(buffer, bufferPos))
            {
                Skip();
                if (!Cache(1))
                {
                    return false;
                }
            }
        }

        // Check if we are at the end of the line.
        if (!IsBreakZ(buffer, bufferPos))
        {
            SetScannerError("while scanning a block scalar", startMark, "did not find expected comment or line break");
            return false;
        }

        // Eat a line break.
        if (IsBreak(buffer, bufferPos))
        {
            if (!Cache(2))
            {
                return false;
            }
            SkipLine();
        }

        YamlMark endMark = mark;

        // Set the indentation level if it was specified.
        int blockIndent = 0;
        if (increment > 0)
        {
            blockIndent = indent >= 0 ? indent + increment : increment;
        }

        // Scan the leading line breaks and determine the indentation level if needed.
        var s = new YamlBytes();
        var leadingBreak = new YamlBytes();
        var trailingBreaks = new YamlBytes();
        if (!ScanBlockScalarBreaks(ref blockIndent, trailingBreaks, startMark, ref endMark))
        {
            return false;
        }

        // Scan the block scalar content.
        if (!Cache(1))
        {
            return false;
        }
        bool leadingBlank = false;
        while (mark.Column == blockIndent && !IsZ(buffer, bufferPos))
        {
            // We are at the beginning of a non-empty line.

            // Is it a trailing whitespace?
            bool trailingBlank = IsBlank(buffer, bufferPos);

            // Check if we need to fold the leading line break.
            if (!literal && !leadingBlank && !trailingBlank && leadingBreak.Length > 0 && leadingBreak[0] == '\n')
            {
                // Do we need to join the lines by space?
                if (trailingBreaks.Length == 0)
                {
                    s.Add((byte)' ');
                }
            }
            else
            {
                s.AddRange(leadingBreak);
            }
            leadingBreak.Clear();

            // Append the remaining line breaks.
            s.AddRange(trailingBreaks);
            trailingBreaks.Clear();

            // Is it a leading whitespace?
            leadingBlank = IsBlank(buffer, bufferPos);

            // Consume the current line.
            while (!IsBreakZ(buffer, bufferPos))
            {
                Read(s);
                if (!Cache(1))
                {
                    return false;
                }
            }

            // Consume the line break.
            if (!Cache(2))
            {
                return false;
            }

            ReadLine(leadingBreak);

            // Eat the following indentation spaces and line breaks.
            if (!ScanBlockScalarBreaks(ref blockIndent, trailingBreaks, startMark, ref endMark))
            {
                return false;
            }
        }

        // Chomp the tail.
        if (chomping != -1)
        {
            s.AddRange(leadingBreak);
        }
        if (chomping == 1)
        {
            s.AddRange(trailingBreaks);
        }

        // Create a token.
        token.Type = YamlTokenType.Scalar;
        token.StartMark = startMark;
        token.EndMark = endMark;
        token.Value = s.ToArray();
        token.Style = literal ? YamlScalarStyle.Literal : YamlScalarStyle.Folded;
        return true;
    }

    // Scan indentation spaces and line breaks for a block scalar.  Determine the
    // indentation level if needed.
    private bool ScanBlockScalarBreaks(ref int blockIndent, YamlBytes breaks, YamlMark startMark, ref YamlMark endMark)
    {
        endMark = mark;

        // Eat the indentation spaces and line breaks.
        int maxIndent = 0;
        while (true)
        {
            // Eat the indentation spaces.
            if (!Cache(1))
            {
                return false;
            }
            while ((blockIndent == 0 || mark.Column < blockIndent) && IsSpace(buffer, bufferPos))
            {
                Skip();
                if (!Cache(1))
                {
                    return false;
                }
            }
            if (mark.Column > maxIndent)
            {
                maxIndent = mark.Column;
            }

            // Check for a tab character messing the indentation.
            if ((blockIndent == 0 || mark.Column < blockIndent) && IsTab(buffer, bufferPos))
            {
                return SetScannerError("while scanning a block scalar", startMark,
                    "found a tab character where an indentation space is expected");
            }

            // Have we found a non-empty line?
            if (!IsBreak(buffer, bufferPos))
            {
                break;
            }

            // Consume the line break.
            if (!Cache(2))
            {
                return false;
            }
            ReadLine(breaks);
            endMark = mark;
        }

        // Determine the indentation level if needed.
        if (blockIndent == 0)
        {
            blockIndent = maxIndent;
            if (blockIndent < indent + 1)
            {
                blockIndent = indent + 1;
            }
            if (blockIndent < 1)
            {
                blockIndent = 1;
            }
        }
        return true;
    }

    // Scan a quoted scalar.
    private bool ScanFlowScalar(YamlToken token, bool single)
    {
        // Eat the left quote.
        YamlMark startMark = mark;
        Skip();

        // Consume the content of the quoted scalar.
        var s = new YamlBytes();
        var leadingBreak = new YamlBytes();
        var trailingBreaks = new YamlBytes();
        var whitespaces = new YamlBytes();
        while (true)
        {
            // Check that there are no document indicators at the beginning of the line.
            if (!Cache(4))
            {
                return false;
            }

            if (mark.Column == 0 &&
                ((buffer[bufferPos] == '-' && buffer[bufferPos + 1] == '-' && buffer[bufferPos + 2] == '-') ||
                 (buffer[bufferPos] == '.' && buffer[bufferPos + 1] == '.' && buffer[bufferPos + 2] == '.')) &&
                IsBlankZ(buffer, bufferPos + 3))
            {
                SetScannerError("while scanning a quoted scalar", startMark, "found unexpected document indicator");
                return false;
            }

            // Check for EOF.
            if (IsZ(buffer, bufferPos))
            {
                SetScannerError("while scanning a quoted scalar", startMark, "found unexpected end of stream");
                return false;
            }

            // Consume non-blank characters.
            bool leadingBlanks = false;
            while (!IsBlankZ(buffer, bufferPos))
            {
                if (single && buffer[bufferPos] == '\'' && buffer[bufferPos + 1] == '\'')
                {
                    // Is is an escaped single quote.
                    s.Add((byte)'\'');
                    Skip();
                    Skip();
                }
                else if (single && buffer[bufferPos] == '\'')
                {
                    // It is a right single quote.
                    break;
                }
                else if (!single && buffer[bufferPos] == '"')
                {
                    // It is a right double quote.
                    break;
                }
                else if (!single && buffer[bufferPos] == '\\' && IsBreak(buffer, bufferPos + 1))
                {
                    // It is an escaped line break.
                    if (!Cache(3))
                    {
                        return false;
                    }
                    Skip();
                    SkipLine();
                    leadingBlanks = true;
                    break;
                }
                else if (!single && buffer[bufferPos] == '\\')
                {
                    // It is an escape sequence.
                    int codeLength = 0;

                    // Check the escape character.
                    switch (buffer[bufferPos + 1])
                    {
                        case (byte)'0':
                            s.Add(0);
                            break;
                        case (byte)'a':
                            s.Add(0x07);
                            break;
                        case (byte)'b':
                            s.Add(0x08);
                            break;
                        case (byte)'t':
                        case (byte)'\t':
                            s.Add(0x09);
                            break;
                        case (byte)'n':
                            s.Add(0x0A);
                            break;
                        case (byte)'v':
                            s.Add(0x0B);
                            break;
                        case (byte)'f':
                            s.Add(0x0C);
                            break;
                        case (byte)'r':
                            s.Add(0x0D);
                            break;
                        case (byte)'e':
                            s.Add(0x1B);
                            break;
                        case (byte)' ':
                            s.Add(0x20);
                            break;
                        case (byte)'"':
                            s.Add((byte)'"');
                            break;
                        case (byte)'\'':
                            s.Add((byte)'\'');
                            break;
                        case (byte)'\\':
                            s.Add((byte)'\\');
                            break;
                        case (byte)'N': // NEL (#x85)
                            s.Add(0xC2);
                            s.Add(0x85);
                            break;
                        case (byte)'_': // #xA0
                            s.Add(0xC2);
                            s.Add(0xA0);
                            break;
                        case (byte)'L': // LS (#x2028)
                            s.Add(0xE2);
                            s.Add(0x80);
                            s.Add(0xA8);
                            break;
                        case (byte)'P': // PS (#x2029)
                            s.Add(0xE2);
                            s.Add(0x80);
                            s.Add(0xA9);
                            break;
                        case (byte)'x':
                            codeLength = 2;
                            break;
                        case (byte)'u':
                            codeLength = 4;
                            break;
                        case (byte)'U':
                            codeLength = 8;
                            break;
                        default:
                            SetScannerError("while parsing a quoted scalar", startMark, "found unknown escape character");
                            return false;
                    }

                    Skip();
                    Skip();

                    // Consume an arbitrary escape code.
                    if (codeLength > 0)
                    {
                        long value = 0;

                        // Scan the character value.
                        if (!Cache(codeLength))
                        {
                            return false;
                        }
                        for (int k = 0; k < codeLength; k++)
                        {
                            if (!IsHex(buffer, bufferPos + k))
                            {
                                SetScannerError("while parsing a quoted scalar", startMark, "did not find expected hexdecimal number");
                                return false;
                            }
                            value = (value << 4) + AsHex(buffer, bufferPos + k);
                        }

                        // Check the value and write the character.
                        if ((value >= 0xD800 && value <= 0xDFFF) || value > 0x10FFFF)
                        {
                            SetScannerError("while parsing a quoted scalar", startMark, "found invalid Unicode character escape code");
                            return false;
                        }
                        if (value <= 0x7F)
                        {
                            s.Add((byte)value);
                        }
                        else if (value <= 0x7FF)
                        {
                            s.Add((byte)(0xC0 + (value >> 6)));
                            s.Add((byte)(0x80 + (value & 0x3F)));
                        }
                        else if (value <= 0xFFFF)
                        {
                            s.Add((byte)(0xE0 + (value >> 12)));
                            s.Add((byte)(0x80 + ((value >> 6) & 0x3F)));
                            s.Add((byte)(0x80 + (value & 0x3F)));
                        }
                        else
                        {
                            s.Add((byte)(0xF0 + (value >> 18)));
                            s.Add((byte)(0x80 + ((value >> 12) & 0x3F)));
                            s.Add((byte)(0x80 + ((value >> 6) & 0x3F)));
                            s.Add((byte)(0x80 + (value & 0x3F)));
                        }

                        // Advance the pointer.
                        for (int k = 0; k < codeLength; k++)
                        {
                            Skip();
                        }
                    }
                }
                else
                {
                    // It is a non-escaped non-blank character.
                    Read(s);
                }
                if (!Cache(2))
                {
                    return false;
                }
            }

            if (!Cache(1))
            {
                return false;
            }

            // Check if we are at the end of the scalar.
            if (buffer[bufferPos] == (single ? '\'' : '"'))
            {
                break;
            }

            // Consume blank characters.
            while (IsBlank(buffer, bufferPos) || IsBreak(buffer, bufferPos))
            {
                if (IsBlank(buffer, bufferPos))
                {
                    // Consume a space or a tab character.
                    if (!leadingBlanks)
                    {
                        Read(whitespaces);
                    }
                    else
                    {
                        Skip();
                    }
                }
                else
                {
                    if (!Cache(2))
                    {
                        return false;
                    }

                    // Check if it is a first line break.
                    if (!leadingBlanks)
                    {
                        whitespaces.Clear();
                        ReadLine(leadingBreak);
                        leadingBlanks = true;
                    }
                    else
                    {
                        ReadLine(trailingBreaks);
                    }
                }
                if (!Cache(1))
                {
                    return false;
                }
            }

            // Join the whitespaces or fold line breaks.
            if (leadingBlanks)
            {
                // Do we need to fold line breaks?
                if (leadingBreak.Length > 0 && leadingBreak[0] == '\n')
                {
                    if (trailingBreaks.Length == 0)
                    {
                        s.Add((byte)' ');
                    }
                    else
                    {
                        s.AddRange(trailingBreaks);
                    }
                }
                else
                {
                    s.AddRange(leadingBreak);
                    s.AddRange(trailingBreaks);
                }
                trailingBreaks.Clear();
                leadingBreak.Clear();
            }
            else
            {
                s.AddRange(whitespaces);
                whitespaces.Clear();
            }
        }

        // Eat the right quote.
        Skip();
        YamlMark endMark = mark;

        // Create a token.
        token.Type = YamlTokenType.Scalar;
        token.StartMark = startMark;
        token.EndMark = endMark;
        token.Value = s.ToArray();
        token.Style = single ? YamlScalarStyle.SingleQuoted : YamlScalarStyle.DoubleQuoted;
        return true;
    }

    // Scan a plain scalar.
    private bool ScanPlainScalar(YamlToken token)
    {
        var s = new YamlBytes();
        var leadingBreak = new YamlBytes();
        var trailingBreaks = new YamlBytes();
        var whitespaces = new YamlBytes();
        bool leadingBlanks = false;
        int plainIndent = indent + 1;

        YamlMark startMark = mark;
        YamlMark endMark = mark;

        // Consume the content of the plain scalar.
        while (true)
        {
            // Check for a document indicator.
            if (!Cache(4))
            {
                return false;
            }
            if (mark.Column == 0 &&
                ((buffer[bufferPos] == '-' && buffer[bufferPos + 1] == '-' && buffer[bufferPos + 2] == '-') ||
                 (buffer[bufferPos] == '.' && buffer[bufferPos + 1] == '.' && buffer[bufferPos + 2] == '.')) &&
                IsBlankZ(buffer, bufferPos + 3))
            {
                break;
            }

            // Check for a comment.
            if (buffer[bufferPos] == '#')
            {
                break;
            }

            // Consume non-blank characters.
            while (!IsBlankZ(buffer, bufferPos))
            {
                byte c = buffer[bufferPos];

                // Check for indicators that may end a plain scalar.
                if ((c == ':' && IsBlankZ(buffer, bufferPos + 1)) ||
                    (flowLevel > 0 && (c == ',' || c == '?' || c == '[' || c == ']' || c == '{' || c == '}')))
                {
                    break;
                }

                // Check if we need to join whitespaces and breaks.
                if (leadingBlanks || whitespaces.Length > 0)
                {
                    if (leadingBlanks)
                    {
                        // Do we need to fold line breaks?
                        if (leadingBreak[0] == '\n')
                        {
                            if (trailingBreaks.Length == 0)
                            {
                                s.Add((byte)' ');
                            }
                            else
                            {
                                s.AddRange(trailingBreaks);
                            }
                        }
                        else
                        {
                            s.AddRange(leadingBreak);
                            s.AddRange(trailingBreaks);
                        }
                        trailingBreaks.Clear();
                        leadingBreak.Clear();
                        leadingBlanks = false;
                    }
                    else
                    {
                        s.AddRange(whitespaces);
                        whitespaces.Clear();
                    }
                }

                // Copy the character.
                Read(s);

                endMark = mark;
                if (!Cache(2))
                {
                    return false;
                }
            }

            // Is it the end?
            if (!(IsBlank(buffer, bufferPos) || IsBreak(buffer, bufferPos)))
            {
                break;
            }

            // Consume blank characters.
            if (!Cache(1))
            {
                return false;
            }

            while (IsBlank(buffer, bufferPos) || IsBreak(buffer, bufferPos))
            {
                if (IsBlank(buffer, bufferPos))
                {
                    // Check for tab characters that abuse indentation.
                    if (leadingBlanks && mark.Column < plainIndent && IsTab(buffer, bufferPos))
                    {
                        SetScannerError("while scanning a plain scalar", startMark, "found a tab character that violates indentation");
                        return false;
                    }

                    // Consume a space or a tab character.
                    if (!leadingBlanks)
                    {
                        Read(whitespaces);
                    }
                    else
                    {
                        Skip();
                    }
                }
                else
                {
                    if (!Cache(2))
                    {
                        return false;
                    }

                    // Check if it is a first line break.
                    if (!leadingBlanks)
                    {
                        whitespaces.Clear();
                        ReadLine(leadingBreak);
                        leadingBlanks = true;
                    }
                    else
                    {
                        ReadLine(trailingBreaks);
                    }
                }
                if (!Cache(1))
                {
                    return false;
                }
            }

            // Check indentation level.
            if (flowLevel == 0 && mark.Column < plainIndent)
            {
                break;
            }
        }

        // Create a token.
        token.Type = YamlTokenType.Scalar;
        token.StartMark = startMark;
        token.EndMark = endMark;
        token.Value = s.ToArray();
        token.Style = YamlScalarStyle.Plain;

        // Note that we change the 'simple_key_allowed' flag.
        if (leadingBlanks)
        {
            simpleKeyAllowed = true;
        }
        return true;
    }
}
