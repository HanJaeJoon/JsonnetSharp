// Port of sigs.k8s.io/yaml v1.4.0 goyaml.v2/readerc.go.
// The input always comes from a .NET string encoded as UTF-8, so the UTF-16 decoding branch is not ported.

namespace JsonnetSharp.Yaml;

internal sealed partial class YamlParser
{
    // Set the reader error and return false.
    private bool SetReaderError(string problem, int problemOffset, int value)
    {
        Error = YamlErrorType.Reader;
        Problem = problem;
        ProblemOffset = problemOffset;
        ProblemValue = value;
        return false;
    }

    // Determine the input stream encoding by checking the BOM symbol. If no BOM is found, UTF-8 is assumed.
    private bool DetermineEncoding()
    {
        // Ensure that we had enough bytes in the raw buffer.
        while (!eof && rawBufferLen - rawBufferPos < 3)
        {
            if (!UpdateRawBuffer())
            {
                return false;
            }
        }

        int avail = rawBufferLen - rawBufferPos;
        if (avail >= 3 && rawBuffer[rawBufferPos] == 0xEF && rawBuffer[rawBufferPos + 1] == 0xBB && rawBuffer[rawBufferPos + 2] == 0xBF)
        {
            rawBufferPos += 3;
            offset += 3;
        }
        encoding = YamlEncoding.Utf8;
        return true;
    }

    // Update the raw buffer.
    private bool UpdateRawBuffer()
    {
        // Return if the raw buffer is full.
        if (rawBufferPos == 0 && rawBufferLen == rawBuffer.Length)
        {
            return true;
        }

        // Return on EOF.
        if (eof)
        {
            return true;
        }

        // Move the remaining bytes in the raw buffer to the beginning.
        if (rawBufferPos > 0 && rawBufferPos < rawBufferLen)
        {
            Buffer.BlockCopy(rawBuffer, rawBufferPos, rawBuffer, 0, rawBufferLen - rawBufferPos);
        }
        rawBufferLen -= rawBufferPos;
        rawBufferPos = 0;

        // Call the read handler to fill the buffer.
        int sizeRead = StringReadHandler(rawBuffer, rawBufferLen, rawBuffer.Length - rawBufferLen, out bool atEof);
        rawBufferLen += sizeRead;
        if (atEof)
        {
            eof = true;
        }
        return true;
    }

    // Ensure that the buffer contains at least `length` characters.
    private bool UpdateBuffer(int length)
    {
        // Return if the buffer contains enough characters.
        if (unread >= length)
        {
            return true;
        }

        // Determine the input encoding if it is not known yet.
        if (encoding == YamlEncoding.Any)
        {
            if (!DetermineEncoding())
            {
                return false;
            }
        }

        // Move the unread characters to the beginning of the buffer.
        int bufLen = bufferLen;
        if (bufferPos > 0 && bufferPos < bufLen)
        {
            Buffer.BlockCopy(buffer, bufferPos, buffer, 0, bufLen - bufferPos);
            bufLen -= bufferPos;
            bufferPos = 0;
        }
        else if (bufferPos == bufLen)
        {
            bufLen = 0;
            bufferPos = 0;
        }

        // Fill the buffer until it has enough characters.
        bool first = true;
        while (unread < length)
        {
            // Fill the raw buffer if necessary.
            if (!first || rawBufferPos == rawBufferLen)
            {
                if (!UpdateRawBuffer())
                {
                    bufferLen = bufLen;
                    return false;
                }
            }
            first = false;

            // Decode the raw buffer.
            while (rawBufferPos != rawBufferLen)
            {
                int rawUnread = rawBufferLen - rawBufferPos;

                // Determine the length of the UTF-8 sequence.
                byte octet = rawBuffer[rawBufferPos];
                int width;
                if ((octet & 0x80) == 0x00)
                {
                    width = 1;
                }
                else if ((octet & 0xE0) == 0xC0)
                {
                    width = 2;
                }
                else if ((octet & 0xF0) == 0xE0)
                {
                    width = 3;
                }
                else if ((octet & 0xF8) == 0xF0)
                {
                    width = 4;
                }
                else
                {
                    // The leading octet is invalid.
                    return SetReaderError("invalid leading UTF-8 octet", offset, octet);
                }

                // Check if the raw buffer contains an incomplete character.
                if (width > rawUnread)
                {
                    if (eof)
                    {
                        return SetReaderError("incomplete UTF-8 octet sequence", offset, -1);
                    }
                    break;
                }

                // Decode the leading octet.
                int value = width switch
                {
                    1 => octet & 0x7F,
                    2 => octet & 0x1F,
                    3 => octet & 0x0F,
                    _ => octet & 0x07,
                };

                // Check and decode the trailing octets.
                for (int k = 1; k < width; k++)
                {
                    octet = rawBuffer[rawBufferPos + k];

                    // Check if the octet is valid.
                    if ((octet & 0xC0) != 0x80)
                    {
                        return SetReaderError("invalid trailing UTF-8 octet", offset + k, octet);
                    }

                    // Decode the octet.
                    value = (value << 6) + (octet & 0x3F);
                }

                // Check the length of the sequence against the value.
                if (!(width == 1 || width == 2 && value >= 0x80 || width == 3 && value >= 0x800 || width == 4 && value >= 0x10000))
                {
                    return SetReaderError("invalid length of a UTF-8 sequence", offset, -1);
                }

                // Check the range of the value.
                if (value >= 0xD800 && value <= 0xDFFF || value > 0x10FFFF)
                {
                    return SetReaderError("invalid Unicode character", offset, value);
                }

                // Check if the character is in the allowed range:
                //      #x9 | #xA | #xD | [#x20-#x7E]               (8 bit)
                //      | #x85 | [#xA0-#xD7FF] | [#xE000-#xFFFD]    (16 bit)
                //      | [#x10000-#x10FFFF]                        (32 bit)
                if (!(value == 0x09 || value == 0x0A || value == 0x0D || value >= 0x20 && value <= 0x7E ||
                      value == 0x85 || value >= 0xA0 && value <= 0xD7FF || value >= 0xE000 && value <= 0xFFFD ||
                      value >= 0x10000 && value <= 0x10FFFF))
                {
                    return SetReaderError("control characters are not allowed", offset, value);
                }

                // Move the raw pointers.
                rawBufferPos += width;
                offset += width;

                // Finally put the character into the buffer.
                if (value <= 0x7F)
                {
                    buffer[bufLen++] = (byte)value;
                }
                else if (value <= 0x7FF)
                {
                    buffer[bufLen++] = (byte)(0xC0 + (value >> 6));
                    buffer[bufLen++] = (byte)(0x80 + (value & 0x3F));
                }
                else if (value <= 0xFFFF)
                {
                    buffer[bufLen++] = (byte)(0xE0 + (value >> 12));
                    buffer[bufLen++] = (byte)(0x80 + ((value >> 6) & 0x3F));
                    buffer[bufLen++] = (byte)(0x80 + (value & 0x3F));
                }
                else
                {
                    buffer[bufLen++] = (byte)(0xF0 + (value >> 18));
                    buffer[bufLen++] = (byte)(0x80 + ((value >> 12) & 0x3F));
                    buffer[bufLen++] = (byte)(0x80 + ((value >> 6) & 0x3F));
                    buffer[bufLen++] = (byte)(0x80 + (value & 0x3F));
                }

                unread++;
            }

            // On EOF, put NUL into the buffer and return.
            if (eof)
            {
                buffer[bufLen++] = 0;
                unread++;
                break;
            }
        }

        // [Go] To return true the given length must be in the buffer, even when EOF broke out early.
        while (bufLen < length)
        {
            buffer[bufLen++] = 0;
        }
        bufferLen = bufLen;
        return true;
    }
}
