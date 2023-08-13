﻿/*
A parser and linter for JSON.
*/
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JSON_Tools.Utils;

namespace JSON_Tools.JSON_Tools
{
    /// <summary>
    /// An exception that may be thrown when the parser encounters syntactically invalid JSON.
    /// Subclasses FormatException.
    /// </summary>
    public class JsonParserException : FormatException
    {
        public new string Message { get; set; }
        public char CurChar { get; set; }
        public int Position { get; set; }

        public JsonParserException(string Message, char c, int pos)
        {
            this.Message = Message;
            this.CurChar = c;
            this.Position = pos;
        }

        public override string ToString()
        {
            return $"{Message} at position {Position} (char {JsonLint.CharDisplay(CurChar)})";
        }
    }

    /// <summary>
    /// A syntax error caught and logged by the linter.
    /// </summary>
    public struct JsonLint
    {
        public string message;
        public int pos;
        public char curChar;
        public ParserState severity;

        public JsonLint(string message, int pos, char curChar, ParserState severity)
        {
            this.message = message;
            this.pos = pos;
            this.curChar = curChar;
            this.severity = severity;
        }

        public override string ToString()
        {
            return $"Syntax error (severity = {severity}) at position {pos} (char {CharDisplay(curChar)}): {message}";
        }

        /// <summary>
        /// Display a char wrapped in singlequotes in a way that makes it easily recognizable.
        /// For example, '\n' is represented as '\n' and '\'' is represented as '\''.
        /// </summary>
        /// <param name="c"></param>
        /// <returns></returns>
        public static string CharDisplay(char c)
        {
            switch (c)
            {
                case '\x00': return "'\\x00'";
                case '\t': return "'\\t'";
                case '\r': return "'\\r'";
                case '\n': return "'\\n'";
                case '\'': return "'\\''";
                default: return $"'{c}'";
            }
        }
    }

    /// <summary>
    /// Any errors above this level are reported by a JsonParser.<br></br>
    /// The integer value of a state reflects how seriously the input deviates from the original JSON spec.
    /// </summary>
    public enum LoggerLevel
    {
        /// <summary>
        /// Valid according to the <i>exact</i> original JSON specification.<br></br>
        /// This is pretty annoying to use because horizontal tabs ('\t') are forbidden.
        /// </summary>
        STRICT,
        /// <summary>
        /// Valid according to a <i>slightly relaxed</i> version of the JSON specification.<br></br>
        /// In addition to the JSON spec, it tolerates:<br></br>
        /// * control characters with ASCII codes below 0x20
        /// </summary>
        OK,
        /// <summary>
        /// Everything at the OK level plus NaN, Infinity, and -Infinity.
        /// </summary>
        NAN_INF,
        /// <summary>
        /// Everything at the NAN_INF level, plus JavaScript comments.<br></br>
        /// Note that this differs slightly from the standard JSONC spec
        /// because NaN and +/-Infinity are not part of that spec.
        /// </summary>
        JSONC,
        /// <summary>
        /// JSON that follows the specification described here: https://json5.org/<br></br>
        /// Includes everything at the JSONC level, plus various things, including:<br></br>
        /// * unquoted object keys<br></br>
        /// * comma after last element of iterable<br></br>
        /// * singlequoted strings
        /// </summary>
        JSON5,
    }

    /// <summary>
    /// the sequence of states the JSON parser can be in.<br></br>
    /// The first five states (STRICT, OK, NAN_INF, JSONC, JSON5) have the same
    /// meaning as in the LoggerLevel enum.
    /// The last two states (BAD and FATAL) reflect errors that are
    /// <i>always logged</i> and thus do not belong in the LoggerLevel enum.
    /// </summary>
    public enum ParserState
    {
        /// <summary>
        /// see LoggerLevel.STRICT
        /// </summary>
        STRICT,
        /// <summary>
        /// see LoggerLevel.OK
        /// </summary>
        OK,
        /// <summary>
        /// see LoggerLevel.NAN_INF
        /// </summary>
        NAN_INF,
        /// <summary>
        /// see LoggerLevel.JSONC
        /// </summary>
        JSONC,
        /// <summary>
        /// see LoggerLevel.JSON5
        /// </summary>
        JSON5,
        /// <summary>
        /// JSON with syntax errors that my parser can handle but that should always be logged, such as:<br></br>
        /// * unterminated strings<br></br>
        /// * missing commas after array elements<br></br>
        /// * Python-style single-line comments (start with '#')
        /// </summary>
        BAD,
        /// <summary>
        /// errors that are always fatal, such as:<br></br>
        /// * recursion depth hits the recursion limit
        /// * empty input
        /// </summary>
        FATAL
    }

    /// <summary>
    /// Parses a JSON document into a <seealso cref="JNode"/> tree.
    /// </summary>
    public class JsonParser
    {
        /// <summary>
        /// need to track recursion depth because stack overflow causes a panic that makes Notepad++ crash
        /// </summary>
        public const int MAX_RECURSION_DEPTH = 512;

        #region JSON_PARSER_ATTRS
        /// <summary>
        /// If true, any strings in the standard formats of ISO 8601 dates (yyyy-MM-dd) and datetimes (yyyy-MM-dd hh:mm:ss.sss)
        ///  will be automatically parsed as the appropriate type.
        ///  Not currently supported. May never be.
        /// </summary>
        public bool parse_datetimes;

        /// <summary>
        /// If line is not null, most forms of invalid syntax will not cause the parser to stop,<br></br>
        /// but instead the syntax error will be recorded in a list.
        /// </summary>
        public List<JsonLint> lint;

        /// <summary>
        /// position in JSON string
        /// </summary>
        public int ii;

        /// <summary>
        /// the number of extra bytes in the UTF-8 encoding of the text consumed
        /// so far.<br></br>
        /// For example, if<br></br>
        /// "words": "Thế nào rồi?"<br></br>
        /// has been consumed, utf8ExtraBytes is 5 because all the characters
        /// are 1-byte ASCII<br></br>
        /// except 'ồ' and 'ế', which are both 3-byte characters<br></br>
        /// and 'à' which is a 2-byte character
        /// </summary>
        private int utf8_extra_bytes;

        public ParserState state { get; private set; }
        
        /// <summary>
        /// errors above this 
        /// </summary>
        public LoggerLevel logger_level;

        /// <summary>
        /// Any error above the logger level causes an error to be thrown.<br></br>
        /// If false, parse functions will return everything logged up until a fatal error
        /// and will parse everything if there were no fatal errors.<br></br>
        /// Present primarily for backwards compatibility.
        /// </summary>
        public bool throw_if_logged;

        public bool throw_if_fatal;

        /// <summary>
        /// attach ExtraJNodeProperties to each JNode parsed
        /// </summary>
        public bool include_extra_properties;
        
        /// <summary>
        /// the number of bytes in the utf-8 representation
        /// before the current position in the current document
        /// </summary>
        public int utf8_pos { get { return ii + utf8_extra_bytes; } }

        public bool fatal
        {
            get { return state == ParserState.FATAL; }
        }

        public bool has_logged
        {
            get { return (int)state > (int)logger_level; }
        }

        public bool exited_early
        {
            get { return fatal || (throw_if_logged && has_logged); }
        }

        /// <summary>
        /// if parsing failed, this will be the final error logged. If parsing succeeded, this is null.
        /// </summary>
        public JsonLint? fatal_error
        {
            get
            {
                if (exited_early)
                    return lint[lint.Count - 1];
                return null;
            }
        }

        public JsonParser(LoggerLevel logger_level = LoggerLevel.NAN_INF, bool parse_datetimes = false, bool throw_if_logged = true, bool throw_if_fatal = true)
            //, bool include_extra_properties = false)
        {
            this.logger_level = logger_level;
            this.parse_datetimes = parse_datetimes;
            this.throw_if_logged = throw_if_logged;
            this.throw_if_fatal = throw_if_fatal;
            //this.include_extra_properties = include_extra_properties;
            ii = 0;
            lint = new List<JsonLint>();
            state = ParserState.STRICT;
            utf8_extra_bytes = 0;
        }

        #endregion
        #region HELPER_METHODS

        public static int ExtraUTF8Bytes(char c)
        {
            return (c < 128)
                ? 0
                : (c > 2047)
                    ? // check if it's in the surrogate pair region
                      (c >= 0xd800 && c <= 0xdfff)
                        ? 1 // each member of a surrogate pair counts as 2 bytes
                            // for a total of 4 bytes for the unicode characters over 65535
                        : 2 // other chars bigger than 2047 take up 3 bytes
                    : 1; // non-ascii chars less than 2048 take up 2 bytes
        }

        public static int ExtraUTF8BytesBetween(string inp, int start, int end)
        {
            int count = 0;
            for (int ii = start; ii < end; ii++)
            {
                count += ExtraUTF8Bytes(inp[ii]);
            }
            return count;
        }

        /// <summary>
        /// Set the parser's state to severity, unless the state was already higher.<br></br>
        /// If the severity is above the parser's logger_level:<br></br>
        ///     * if throw_if_logged or (FATAL and throw_if_fatal), throw a JsonParserException<br></br>
        ///     * otherwise, add new JsonLint with the appropriate message, position, curChar, and severity.<br></br>
        /// Return whether current state is FATAL.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="inp"></param>
        /// <param name="pos"></param>
        /// <param name="severity"></param>
        /// <exception cref="JsonParserException"/>
        private bool HandleError(string message, string inp, int pos, ParserState severity)
        {
            if (state < severity)
                state = severity;
            bool fatal = this.fatal;
            if ((int)severity > (int)logger_level)
            {
                char c = (pos >= inp.Length)
                    ? '\x00'
                    : inp[pos];
                lint.Add(new JsonLint(message, utf8_pos, c, severity));
                if (throw_if_logged || (fatal && throw_if_fatal))
                {
                    throw new JsonParserException(message, c, utf8_pos);
                }
            }
            return fatal;
        }

        private void ConsumeLine(string inp)
        {
            while (ii < inp.Length && inp[ii] != '\n')
            {
                ii++;
            }
            ii++;
        }

        /// <summary>
        /// Consume comments and whitespace until the next character that is not
        /// '#', '/', ' ', '\t', '\r', or '\n'.
        /// Return false if an unacceptable error occurred.
        /// </summary>
        /// <param name="inp"></param>
        /// <returns></returns>
        private bool ConsumeInsignificantChars(string inp)
        {
            while (ii < inp.Length)
            {
                char c = inp[ii];
                switch (c)
                {
                    case ' ':
                    case '\t':
                    case '\r':
                    case '\n': ii++; break;
                    case '/':
                        ii++;
                        if (ii == inp.Length)
                        {
                            HandleError("Expected JavaScript comment after '/'", inp, inp.Length - 1, ParserState.FATAL);
                            return false;
                        }
                        HandleError("JavaScript comments are not part of the original JSON specification", inp, ii, ParserState.JSONC);
                        c = inp[ii];
                        if (c == '/')
                        {
                            // JavaScript single-line comment
                            ConsumeLine(inp);
                        }
                        else if (c == '*')
                        {
                            bool comment_ended = false;
                            while (ii < inp.Length - 1)
                            {
                                c = inp[ii++];
                                if (c == '*')
                                {
                                    if (inp[ii] == '/')
                                    {
                                        comment_ended = true;
                                        ii++;
                                        break;
                                    }
                                }
                            }
                            if (!comment_ended)
                            {
                                HandleError("Unterminated multi-line comment", inp, inp.Length - 1, ParserState.BAD);
                                ii++;
                                return false;
                            }
                        }
                        else
                        {
                            HandleError("Expected JavaScript comment after '/'", inp, ii, ParserState.FATAL);
                            return false;
                        }
                        break;
                    case '#':
                        // Python-style single-line comment
                        HandleError("Python-style '#' comments are not part of any well-accepted JSON specification",
                                inp, ii, ParserState.BAD);
                        ConsumeLine(inp);
                        break;
                    case '\u2028': // line separator
                    case '\u2029': // paragraph separator
                    case '\ufeff': // Byte-order mark
                    // the next 16 (plus '\x20', normal whitespace) comprise the unicode space separator category
                    case '\xa0': // non-breaking space
                    case '\u1680': // Ogham Space Mark
                    case '\u2000': // En Quad
                    case '\u2001': // Em Quad
                    case '\u2002': // En Space
                    case '\u2003': // Em Space
                    case '\u2004': // Three-Per-Em Space
                    case '\u2005': // Four-Per-Em Space
                    case '\u2006': // Six-Per-Em Space
                    case '\u2007': // Figure Space
                    case '\u2008': // Punctuation Space
                    case '\u2009': // Thin Space
                    case '\u200A': // Hair Space
                    case '\u202F': // Narrow No-Break Space
                    case '\u205F': // Medium Mathematical Space
                    case '\u3000': // Ideographic Space
                        HandleError("Whitespace characters other than ' ', '\\t', '\\r', and '\\n' are only allowed in JSON5", inp, ii, ParserState.JSON5);
                        utf8_extra_bytes += ExtraUTF8Bytes(c);
                        ii++;
                        break;
                    default: return true;
                }
            }
            return true; // unreachable
        }

        /// <summary>
        /// read a hexadecimal integer representation of length `length` at position `index` in `inp`.
        /// sets the parser's state to FATAL if the integer is not valid hexadecimal
        /// or if `index` is less than `length` from the end of `inp`.
        /// </summary>
        private int ParseHexChar(string inp, int length)
        {
            if (ii >= inp.Length - length)
            {
                HandleError("Could not find valid hexadecimal of length " + length,
                                              inp, ii, ParserState.FATAL);
                return -1;
            }
            int end = ii + length > inp.Length
                ? inp.Length
                : ii + length;
            var hexNum = inp.Substring(ii, end - ii);
            ii = end - 1;
            // the -1 is because ParseString increments by 1 after every escaped sequence anyway
            int charval;
            try
            {
                charval = int.Parse(hexNum, NumberStyles.HexNumber);
            }
            catch
            {
                HandleError("Could not find valid hexadecimal of length " + length,
                                              inp, ii, ParserState.FATAL);
                return -1;
            }
            return charval;
        }

        /// <summary>
        /// check if char c is a control character (less than 0x20)
        /// and then check if it is '\n' or the null character or negative.
        /// Handle errors accordingly.
        /// </summary>
        /// <param name="c"></param>
        /// <param name="inp"></param>
        /// <param name="ii"></param>
        /// <param name="start_utf8_pos"></param>
        /// <returns></returns>
        private bool HandleCharErrors(int c, string inp, int ii)
        {
            if (c < 0x20)
            {
                if (c == '\n')
                    return HandleError($"String literal contains newline", inp, ii, ParserState.BAD);
                if (c == 0)
                    return HandleError("'\\x00' is the null character, which is illegal in JsonTools", inp, ii, ParserState.FATAL);
                if (c < 0)
                    return true;
                return HandleError("Control characters (ASCII code less than 0x20) are disallowed inside strings under the strict JSON specification",
                    inp, ii, ParserState.OK);
            }
            return false;
        }

        public static Dictionary<char, char> ESCAPE_MAP = new Dictionary<char, char>
        {
            { '\\', '\\' },
            { 'n', '\n' },
            { 'r', '\r' },
            { 'b', '\b' },
            { 't', '\t' },
            { 'f', '\f' },
            { '/', '/' }, // the '/' char is often escaped in JSON
            { 'v', '\x0b' }, // vertical tab
            { '\'', '\'' },
            { '"', '"' },
        };

        #endregion
        #region PARSER_FUNCTIONS

        /// <summary>
        /// Parse a string literal in a JSON string.<br></br>
        /// Sets the parser's state to BAD if:<br></br>
        /// 1. The end of the input is reached before the closing quote char<br></br>
        /// 2. A '\n' is encountered before the closing quote char
        /// 3. Contains invalid hexadecimal<br></br>
        /// 4. Contains "\\" escaping a character other than 'n', 'b', 'r', '\', '/', '"', 'f', or 't'.
        /// </summary>
        /// <param name="inp">the json string</param>
        /// <returns>a JNode of type Dtype.STR, and the position of the end of the string literal</returns>
        /// </exception>
        public JNode ParseString(string inp)
        {
            int start_utf8_pos = ii + utf8_extra_bytes;
            char quote_char = inp[ii++];
            if (quote_char == '\'' && HandleError("Singlequoted strings are only allowed in JSON5", inp, ii, ParserState.JSON5))
            {
                return new JNode("", Dtype.STR, utf8_pos);
            }
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (ii >= inp.Length)
                {
                    HandleError($"Unterminated string literal starting at position {start_utf8_pos}", inp, ii - 1, ParserState.BAD);
                    break;
                }
                char c = inp[ii];
                if (c == quote_char)
                {
                    break;
                }
                else if (c == '\\')
                {
                    if (ii >= inp.Length - 2)
                    {
                        HandleError($"Unterminated string literal starting at position {start_utf8_pos}", inp, inp.Length - 1, ParserState.BAD);
                        ii++;
                        continue;
                    }
                    char next_char = inp[ii + 1];
                    if (next_char == quote_char)
                    {
                        sb.Append(quote_char);
                        ii += 1;
                    }
                    else if (ESCAPE_MAP.TryGetValue(next_char, out char escaped_char))
                    {
                        sb.Append(escaped_char);
                        ii += 1;
                    }
                    else if (next_char == 'u')
                    {
                        // 2-byte unicode of the form \uxxxx
                        ii += 2;
                        int next_hex = ParseHexChar(inp, 4);
                        if (HandleCharErrors(next_hex, inp, ii))
                            break;
                        sb.Append((char)next_hex);
                    }
                    else if (next_char == '\n' || next_char == '\r')
                    {
                        HandleError("Escaped newline characters are only allowed in JSON5", inp, ii + 1, ParserState.JSON5);
                        ii++;
                        if (next_char == '\r'
                            && ii < inp.Length - 1 && inp[ii + 1] == '\n')
                            ii++;
                    }
                    else if (next_char == 'x')
                    {
                        // 1-byte unicode (allowed only in JSON5)
                        ii += 2;
                        int next_hex = ParseHexChar(inp, 2);
                        if (HandleCharErrors(next_hex, inp, ii))
                            break;
                        HandleError("\\x escapes are only allowed in JSON5", inp, ii, ParserState.JSON5);
                        sb.Append((char)next_hex);
                    }
                    else HandleError($"Escaped char '{next_char}' is only valid in JSON5", inp, ii + 1, ParserState.JSON5);
                }
                else
                {
                    if (HandleCharErrors(c, inp, ii))
                        break;
                    utf8_extra_bytes += ExtraUTF8Bytes(c);
                    sb.Append(c);
                }
                ii++;
            }
            ii++;
            if (parse_datetimes)
            {
                return TryParseDateOrDateTime(sb.ToString(), start_utf8_pos);
            }
            return new JNode(sb.ToString(), Dtype.STR, start_utf8_pos);
        }

        public string ParseKey(string inp)
        {
            char quote_char = inp[ii];
            if (quote_char == '\'' && HandleError("Singlequoted strings are only allowed in JSON5", inp, ii, ParserState.JSON5))
            {
                return null;
            }
            if (quote_char != '\'' && quote_char != '"')
            {
                return ParseUnquotedKey(inp);
            }
            ii++;
            var sb = new StringBuilder();
            while (true)
            {
                if (ii >= inp.Length)
                {
                    HandleError($"Unterminated object key", inp, ii - 1, ParserState.FATAL);
                    return null;
                }
                char c = inp[ii];
                if (c == quote_char)
                {
                    break;
                }
                else if (c == '\\')
                {
                    if (ii >= inp.Length - 2)
                    {
                        HandleError($"Unterminated object key", inp, inp.Length - 1, ParserState.FATAL);
                        return null;
                    }
                    char next_char = inp[ii + 1];
                    if (next_char == quote_char)
                    {
                        sb.Append(JNode.CharToString(quote_char));
                        ii++;
                    }
                    else if (ESCAPE_MAP.TryGetValue(next_char, out _))
                    {
                        sb.Append('\\');
                        sb.Append(next_char);
                        ii += 1;
                    }
                    else if (next_char == 'u')
                    {
                        // 2-byte unicode of the form \uxxxx
                        // \x and \U escapes are not part of the JSON standard
                        ii += 2;
                        int next_hex = ParseHexChar(inp, 4);
                        if (HandleCharErrors(next_hex, inp, ii))
                            break;
                        sb.Append(JNode.CharToString((char)next_hex));
                    }
                    else if (next_char == '\n' || next_char == '\r')
                    {
                        HandleError($"Escaped newline characters are only allowed in JSON5", inp, ii + 1, ParserState.JSON5);
                        ii++;
                        if (next_char == '\r'
                            && ii < inp.Length - 1 && inp[ii + 1] == '\n')
                            ii++; // skip \r\n as one
                    }
                    else if (next_char == 'x')
                    {
                        ii += 2;
                        int next_hex = ParseHexChar(inp, 2);
                        if (HandleCharErrors(next_hex, inp, ii))
                            break;
                        HandleError("\\x escapes are only allowed in JSON5", inp, ii, ParserState.JSON5);
                        sb.Append(JNode.CharToString((char)next_hex));
                    }
                    else HandleError($"Escaped char '{next_char}' is only valid in JSON5", inp, ii + 1, ParserState.JSON5);
                }
                else if (c < 0x20) // control characters
                {
                    if (c == '\n')
                        HandleError($"Object key contains newline", inp, ii, ParserState.BAD);
                    else
                        HandleError("Control characters (ASCII code less than 0x20) are disallowed inside strings under the strict JSON specification", inp, ii, ParserState.OK);
                    sb.Append(JNode.CharToString(c));
                }
                else
                {
                    utf8_extra_bytes += ExtraUTF8Bytes(c);
                    sb.Append(c);
                }
                ii++;
            }
            ii++;
            return sb.ToString();
        }

        public const string UNQUOTED_START = @"(?:[_\$\p{Lu}\p{Ll}\p{Lt}\p{Lm}\p{Lo}\p{Nl}]|\\u[\da-f]{4})";

        private static Regex UNICODE_ESCAPES = new Regex(@"(?<=\\u)[\da-f]{4}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static Regex UNQUOTED_KEY_REGEX = new Regex($@"{UNQUOTED_START}(?:[\p{{Mn}}\p{{Mc}}\p{{Nd}}\p{{Pc}}\u200c\u200d]|{UNQUOTED_START})*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public string ParseUnquotedKey(string inp)
        {
            var match = UNQUOTED_KEY_REGEX.Match(inp, ii);
            if (!match.Success)
            {
                HandleError($"No valid unquoted key beginning at {ii}", inp, ii, ParserState.FATAL);
                return null;
            }
            HandleError("Unquoted keys are only supported in JSON5", inp, ii, ParserState.JSON5);
            var result = match.Value;
            ii += result.Length;
            utf8_extra_bytes += ExtraUTF8BytesBetween(result, 0, result.Length);
            return ParseUnquotedKeyHelper(inp, result);
        }

        public string ParseUnquotedKeyHelper(string inp, string result)
        {
            if (result.Contains("\\u")) // fix unicode escapes
            {
                StringBuilder sb = new StringBuilder();
                Match m = UNICODE_ESCAPES.Match(result);
                int start = 0;
                while (m.Success)
                {
                    if (m.Index > start + 2)
                    {
                        sb.Append(result, start, m.Index - start - 2);
                    }
                    char hexval = (char)int.Parse(m.Value, NumberStyles.HexNumber);
                    if (HandleCharErrors(hexval, inp, ii))
                        return null;
                    sb.Append(JNode.CharToString(hexval));
                    start = m.Index + 4;
                    m = m.NextMatch();
                }
                if (start < result.Length)
                    sb.Append(result, start, result.Length - start);
                result = sb.ToString();
            }
            return result;
        }

        private static Regex DATE_TIME_REGEX = new Regex(@"^\d{4}-\d\d-\d\d # date
                                                           (?:[T ](?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d # hours, minutes, seconds
                                                           (?:\.\d{1,3})?Z?)?$ # milliseconds",
                                                         RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);
        private JNode TryParseDateOrDateTime(string maybe_datetime, int start_utf8_pos)
        {
            Match mtch = DATE_TIME_REGEX.Match(maybe_datetime);
            int len = maybe_datetime.Length;
            if (mtch.Success)
            {
                try
                {
                    if (len == 10)
                    {
                        // yyyy-mm-dd dates have length 10
                        return new JNode(DateTime.Parse(maybe_datetime), Dtype.DATE, start_utf8_pos);
                    }
                    if (len >= 19 && len <= 23)
                    {
                        // yyyy-mm-dd hh:mm:ss has length 19, and yyyy-mm-dd hh:mm:ss.sss has length 23
                        return new JNode(DateTime.Parse(maybe_datetime), Dtype.DATETIME, start_utf8_pos);
                    }
                }
                catch { } // it was an invalid date, i guess
            }
            // it didn't match, so it's just a normal string
            return new JNode(maybe_datetime, Dtype.STR, start_utf8_pos);
        }

        /// <summary>
        /// Parse a number in a JSON string, including NaN or Infinity<br></br>
        /// Also parses null and the Python literals None (which we parse as null), nan, inf
        /// </summary>
        /// <param name="inp">the JSON string</param>
        /// <returns>a JNode with type = Dtype.INT or Dtype.FLOAT, and the position of the end of the number.
        /// </returns>
        public JNode ParseNumber(string inp)
        {
            // parsed tracks which portions of a number have been parsed.
            // So if the int part has been parsed, it will be 1.
            // If the int and decimal point parts have been parsed, it will be 3.
            // If the int, decimal point, and scientific notation parts have been parsed, it will be 7
            int parsed = 1;
            int start = ii;
            int start_utf8_pos = start + utf8_extra_bytes;
            char c = inp[ii];
            bool negative = false;
            if (c < '0' || c > '9')
            {
                if (c == 'n')
                {
                    // try null
                    if (ii <= inp.Length - 4 && inp[ii + 1] == 'u' && inp[ii + 2] == 'l' && inp[ii + 3] == 'l')
                    {
                        ii += 4;
                        return new JNode(null, Dtype.NULL, start_utf8_pos);
                    }
                    if (ii <= inp.Length - 3 && inp[ii + 1] == 'a' && inp[ii + 2] == 'n')
                    {
                        HandleError("nan is not a valid representation of Not a Number in JSON", inp, ii, ParserState.BAD);
                        ii += 3;
                        return new JNode(NanInf.nan, Dtype.FLOAT, start_utf8_pos);
                    }
                    HandleError("Expected literal starting with 'n' to be null or nan", inp, ii + 1, ParserState.FATAL);
                    return new JNode(null, Dtype.NULL, start_utf8_pos);
                }
                if (c == '-' || c == '+')
                {
                    if (c == '+')
                        HandleError("Leading + signs in numbers are not allowed except in JSON5", inp, ii, ParserState.JSON5);
                    else negative = true;
                    ii++;
                }
                c = inp[ii];
                if (c == 'I')
                {
                    // try Infinity
                    if (ii <= inp.Length - 8 && inp[ii + 1] == 'n' && inp.Substring(ii + 2, 6) == "finity")
                    {
                        HandleError("Infinity is not part of the original JSON specification", inp, ii, ParserState.NAN_INF);
                        ii += 8;
                        double infty = negative ? NanInf.neginf : NanInf.inf;
                        return new JNode(infty, Dtype.FLOAT, start_utf8_pos);
                    }
                    HandleError("Expected literal starting with 'I' to be Infinity",
                                                  inp, ii + 1, ParserState.FATAL);
                    return new JNode(null, Dtype.NULL, start_utf8_pos);
                }
                else if (c == 'N')
                {
                    // try NaN
                    if (ii <= inp.Length - 3 && inp[ii + 1] == 'a' && inp[ii + 2] == 'N')
                    {
                        HandleError("NaN is not part of the original JSON specification", inp, ii, ParserState.NAN_INF);
                        ii += 3;
                        return new JNode(NanInf.nan, Dtype.FLOAT, start_utf8_pos);
                    }
                    // try None
                    if (ii <= inp.Length - 4 && inp[ii + 1] == 'o' && inp[ii + 2] == 'n' && inp[ii + 3] == 'e')
                    {
                        ii += 4;
                        HandleError("None is not an accepted part of any JSON specification", inp, ii, ParserState.BAD);
                        return new JNode(null, Dtype.NULL, start_utf8_pos);
                    }
                    HandleError("Expected literal starting with 'N' to be NaN or None", inp, ii + 1, ParserState.FATAL);
                    return new JNode(null, Dtype.NULL, start_utf8_pos);
                }
                else if (c == 'i')
                {
                    if (ii <= inp.Length - 3 && inp[ii + 1] == 'n' && inp[ii + 2] == 'f')
                    {
                        HandleError("inf is not the correct representation of Infinity in JSON", inp, ii, ParserState.BAD);
                        ii += 3;
                        return new JNode(negative ? NanInf.neginf : NanInf.inf, Dtype.FLOAT, start_utf8_pos);
                    }
                    HandleError("Expected literal starting with 'i' to be inf", inp, ii, ParserState.FATAL);
                    return new JNode(null, Dtype.NULL, start_utf8_pos);
                }
            }
            if (c == '0' && ii < inp.Length - 1 && inp[ii + 1] == 'x')
            {
                HandleError("Hexadecimal numbers are only part of JSON5", inp, ii, ParserState.JSON5);
                ii += 2;
                start = ii;
                while (ii < inp.Length)
                {
                    c = inp[ii];
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                        break;
                    ii++;
                }
                var hexnum = long.Parse(inp.Substring(start, ii - start), NumberStyles.HexNumber);
                return new JNode(negative ? -hexnum : hexnum, Dtype.INT, start_utf8_pos);
            }
            while (ii < inp.Length)
            {
                c = inp[ii];
                if (c >= '0' && c <= '9')
                {
                    ii++;
                }
                else if (c == '.')
                {
                    if (parsed != 1)
                    {
                        HandleError("Number with a decimal point in the wrong place", inp, ii, ParserState.FATAL);
                        break;
                    }
                    if (ii == start && HandleError("Numbers with a leading decimal point are only part of JSON5", inp, start_utf8_pos, ParserState.JSON5))
                    {
                        return new JNode(null, Dtype.NULL, start_utf8_pos);
                    }
                    parsed = 3;
                    ii++;
                }
                else if (c == 'e' || c == 'E')
                {
                    if ((parsed & 4) != 0)
                    {
                        break;
                    }
                    parsed += 4;
                    ii++;
                    if (ii < inp.Length)
                    {
                        c = inp[ii];
                        if (c == '+' || c == '-')
                        {
                            ii++;
                        }
                    }
                    else
                    {
                        HandleError("Scientific notation 'e' with no number following", inp, inp.Length - 1, ParserState.FATAL);
                        return new JNode(null, Dtype.NULL, start_utf8_pos);
                    }
                }
                else if (c == '/' && ii < inp.Length - 1)
                {
                    char next_c = inp[ii + 1];
                    // make sure prospective denominator is also a number (we won't allow NaN or Infinity as denominator)
                    if (!((next_c >= '0' && next_c <= '9')
                        || next_c == '-' || next_c == '.' || next_c == '+'))
                    {
                        break;
                    }
                    HandleError("Fractions of the form 1/3 are not part of any JSON specification", inp, start_utf8_pos, ParserState.BAD);
                    double numer = double.Parse(inp.Substring(start, ii - start), JNode.DOT_DECIMAL_SEP);
                    JNode denom_node;
                    ii++;
                    denom_node = ParseNumber(inp);
                    if (fatal)
                    {
                        return new JNode(numer, Dtype.FLOAT, start_utf8_pos);
                    }
                    double denom = Convert.ToDouble(denom_node.value);
                    return new JNode(numer / denom, Dtype.FLOAT, start_utf8_pos);
                }
                else
                {
                    break;
                }
            }
            string numstr = inp.Substring(start, ii - start);
            if (parsed == 1)
            {
                try
                {
                    return new JNode(long.Parse(numstr), Dtype.INT, start_utf8_pos);
                }
                catch (OverflowException)
                {
                    // doubles can represent much larger numbers than 64-bit ints,
                    // albeit with loss of precision
                }
            }
            double num;
            try
            {
                num = double.Parse(numstr, JNode.DOT_DECIMAL_SEP);
            }
            catch
            {
                HandleError($"Number {numstr} had bad format", inp, start_utf8_pos, ParserState.BAD);
                num = NanInf.nan;
            }
            return new JNode(num, Dtype.FLOAT, start_utf8_pos);
        }

        /// <summary>
        /// Parse an array in a JSON string.<br></br>
        /// Parsing may fail for any of the following reasons:<br></br>
        /// 1. The array is not terminated by ']'.<br></br>
        /// 2. The array is terminated with '}' instead of ']'.<br></br>
        /// 3. Two commas with nothing but whitespace in between.<br></br>
        /// 4. A comma before the first value.<br></br>
        /// 5. A comma after the last value.<br></br>
        /// 6. Two values with no comma in between.
        /// </summary>
        /// <param name="inp">the JSON string</param>
        /// <returns>a JArray, and the position of the end of the array.</returns>
        public JArray ParseArray(string inp, int recursion_depth)
        {
            var children = new List<JNode>();
            JArray arr = new JArray(ii + utf8_extra_bytes, children);
            bool already_seen_comma = false;
            ii++;
            char cur_c;
            if (recursion_depth == MAX_RECURSION_DEPTH)
            {
                // Need to do this to avoid stack overflow when presented with unreasonably deep nesting.
                // Stack overflow causes an unrecoverable panic, and we would rather fail gracefully.
                HandleError($"Maximum recursion depth ({MAX_RECURSION_DEPTH}) reached", inp, ii, ParserState.FATAL);
                return arr;
            }
            while (ii < inp.Length)
            {
                if (!ConsumeInsignificantChars(inp))
                {
                    return arr;
                }
                if (ii >= inp.Length)
                {
                    break;
                }
                cur_c = inp[ii];
                if (cur_c == ',')
                {
                    if (already_seen_comma
                        && HandleError($"Two consecutive commas after element {children.Count - 1} of array", inp, ii, ParserState.BAD))
                    {
                        return arr;
                    }
                    already_seen_comma = true;
                    if (children.Count == 0
                        && HandleError("Comma before first value in array", inp, ii, ParserState.BAD))
                    {
                        return arr;
                    }
                    ii++;
                    continue;
                }
                else if (cur_c == ']')
                {
                    if (already_seen_comma)
                    {
                        HandleError("Comma after last element of array", inp, ii, ParserState.JSON5);
                    }
                    ii++;
                    return arr;
                }
                else if (cur_c == '}')
                {
                    HandleError("Tried to terminate an array with '}'", inp, ii, ParserState.BAD);
                    if (already_seen_comma)
                    {
                        HandleError("Comma after last element of array", inp, ii, ParserState.JSON5);
                    }
                    ii++;
                    return arr;
                }
                else
                {
                    if (children.Count > 0 && !already_seen_comma
                        && HandleError("No comma between array members", inp, ii, ParserState.BAD))
                    {
                        return arr;
                    }
                    // a new array member of some sort
                    already_seen_comma = false;
                    JNode new_obj;
                    new_obj = ParseSomething(inp, recursion_depth);
                    //if (include_extra_properties)
                    //{
                    //    new_obj.extras = new ExtraJNodeProperties(arr, ii, children.Count);
                    //}
                    children.Add(new_obj);
                    if (fatal)
                        return arr;
                }
            }
            ii++;
            HandleError("Unterminated array", inp, inp.Length - 1, ParserState.BAD);
            return arr;
        }

        /// <summary>
        /// Parse an object in a JSON string.<br></br>
        /// Parsing may fail for any of the following reasons:<br></br>
        /// 1. The object is not terminated by '}'.<br></br>
        /// 2. The object is terminated with ']' instead of '}'.<br></br>
        /// 3. Two commas with nothing but whitespace in between.<br></br>
        /// 4. A comma before the first key-value pair.<br></br>
        /// 5. A comma after the last key-value pair.<br></br>
        /// 6. Two key-value pairs with no comma in between.<br></br>
        /// 7. No ':' between a key and a value.<br></br>
        /// 8. A key that's not a string.
        /// </summary>
        /// <param name="inp">the JSON string</param>
        /// <returns>a JArray, and the position of the end of the array.</returns>
        public JObject ParseObject(string inp, int recursion_depth)
        {
            var children = new Dictionary<string, JNode>();
            JObject obj = new JObject(ii + utf8_extra_bytes, children);
            bool already_seen_comma = false;
            ii++;
            char cur_c;
            if (recursion_depth == MAX_RECURSION_DEPTH)
            {
                HandleError($"Maximum recursion depth ({MAX_RECURSION_DEPTH}) reached", inp, ii, ParserState.FATAL);
                return obj;
            }
            while (ii < inp.Length)
            {
                if (!ConsumeInsignificantChars(inp))
                {
                    return obj;
                }
                if (ii >= inp.Length)
                {
                    break;
                }
                cur_c = inp[ii];
                if (cur_c == ',')
                {
                    if (already_seen_comma
                        && HandleError($"Two consecutive commas after key-value pair {children.Count - 1} of object", inp, ii, ParserState.BAD))
                    {
                        return obj;
                    }
                    already_seen_comma = true;
                    if (children.Count == 0
                        && HandleError("Comma before first value in object", inp, ii, ParserState.BAD))
                    {
                        return obj;
                    }
                    ii++;
                    continue;
                }
                else if (cur_c == '}')
                {
                    if (already_seen_comma)
                        HandleError("Comma after last key-value pair of object", inp, ii, ParserState.JSON5);
                    ii++;
                    return obj;
                }
                else if (cur_c == ']')
                {
                    HandleError("Tried to terminate object with ']'", inp, ii, ParserState.BAD);
                    if (already_seen_comma)
                        HandleError("Comma after last key-value pair of object", inp, ii, ParserState.JSON5);
                    ii++;
                    return obj;
                }
                else // expecting a key
                {
                    int child_count = children.Count;
                    if (child_count > 0 && !already_seen_comma
                        && HandleError($"No comma after key-value pair {child_count - 1} in object", inp, ii, ParserState.BAD))
                    {
                        return obj;
                    }
                    // a new key-value pair
                    string key = ParseKey(inp);
                    if (fatal || key == null)
                    {
                        return obj;
                    }
                    if (ii >= inp.Length)
                    {
                        break;
                    }
                    if (inp[ii] == ':')
                        ii++;
                    else
                    {
                        if (!ConsumeInsignificantChars(inp))
                        {
                            return obj;
                        }
                        if (ii >= inp.Length)
                        {
                            break;
                        }
                        if (inp[ii] == ':')
                        {
                            ii++;
                        }
                        else HandleError($"No ':' between key {child_count} and value {child_count} of object", inp, ii, ParserState.BAD);
                    }
                    if (!ConsumeInsignificantChars(inp))
                    {
                        return obj;
                    }
                    if (ii >= inp.Length)
                    {
                        break;
                    }
                    JNode val = ParseSomething(inp, recursion_depth);
                    //if (include_extra_properties)
                    //{
                    //    val.extras = new ExtraJNodeProperties(obj, ii, key);
                    //}
                    children[key] = val;
                    if (fatal)
                    {
                        return obj;
                    }
                    if (children.Count == child_count)
                    {
                        HandleError($"Object has multiple of key \"{key}\"", inp, ii, ParserState.BAD);
                    }
                    already_seen_comma = false;
                }
            }
            ii++;
            HandleError("Unterminated object", inp, inp.Length - 1, ParserState.BAD);
            return obj;
        }

        /// <summary>
        /// Parse anything (a scalar, null, an object, or an array) in a JSON string.<br></br>
        /// Parsing may fail (causing this to return a null JNode) for any of the following reasons:<br></br>
        /// 1. Whatever reasons ParseObject, ParseArray, or ParseString might throw an error.<br></br>
        /// 2. An unquoted string other than true, false, null, NaN, Infinity, -Infinity.<br></br>
        /// 3. The JSON string contains only blankspace or is empty.
        /// </summary>
        /// <param name="inp">the JSON string</param>
        /// <returns>a JNode.</returns>
        public JNode ParseSomething(string inp, int recursion_depth)
        {
            int start_utf8_pos = ii + utf8_extra_bytes;
            if (ii >= inp.Length)
            {
                HandleError("Unexpected end of file", inp, inp.Length - 1, ParserState.FATAL);
                return new JNode(null, Dtype.NULL, start_utf8_pos);
            }
            char cur_c = inp[ii];
            if (cur_c == '"' || cur_c == '\'')
            {
                return ParseString(inp);
            }
            if (cur_c >= '0' && cur_c <= '9'
                || cur_c == '-' || cur_c == '+'
                || cur_c == 'n' // null and nan
                || cur_c == 'I' || cur_c == 'N' // Infinity, NaN and None
                || cur_c == '.' // leading decimal point JSON5 numbers
                || cur_c == 'i') // inf
            {
                return ParseNumber(inp);
            }
            if (cur_c == '[')
            {
                return ParseArray(inp, recursion_depth + 1);
            }
            if (cur_c == '{')
            {
                return ParseObject(inp, recursion_depth + 1);
            }
            char next_c;
            if (ii > inp.Length - 4)
            {
                HandleError("No valid literal possible", inp, ii, ParserState.FATAL);
                return new JNode(null, Dtype.NULL, start_utf8_pos);
            }
            // misc literals. In strict JSON, only true or false
            next_c = inp[ii + 1];
            if (cur_c == 't')
            {
                // try true
                if (next_c == 'r' && inp[ii + 2] == 'u' && inp[ii + 3] == 'e')
                {
                    ii += 4;
                    return new JNode(true, Dtype.BOOL, start_utf8_pos);
                }
                HandleError("Expected literal starting with 't' to be true", inp, ii+1, ParserState.FATAL);
                return new JNode(null, Dtype.NULL, start_utf8_pos);
            }
            if (cur_c == 'f')
            {
                // try false
                if (ii <= inp.Length - 5 && next_c == 'a' && inp.Substring(ii + 2, 3) == "lse")
                {
                    ii += 5;
                    return new JNode(false, Dtype.BOOL, start_utf8_pos);
                }
                HandleError("Expected literal starting with 'f' to be false", inp, ii+1, ParserState.FATAL);
                return new JNode(null, Dtype.NULL, start_utf8_pos);
            }
            if (cur_c == 'T')
            {
                // try True from Python
                if (next_c == 'r' && inp[ii + 2] == 'u' && inp[ii + 3] == 'e')
                {
                    ii += 4;
                    HandleError("True is not an accepted part of any JSON specification", inp, ii, ParserState.BAD);
                    return new JNode(true, Dtype.BOOL, start_utf8_pos);
                }
                HandleError("Expected literal starting with 'T' to be True", inp, ii + 1, ParserState.FATAL);
                return new JNode(null, Dtype.NULL, start_utf8_pos);
            }
            if (cur_c == 'F')
            {
                // try False from Python
                if (ii <= inp.Length - 5 && next_c == 'a' && inp.Substring(ii + 2, 3) == "lse")
                {
                    ii += 5;
                    HandleError("False is not an accepted part of any JSON specification", inp, ii, ParserState.BAD);
                    return new JNode(false, Dtype.BOOL, start_utf8_pos);
                }
                HandleError("Expected literal starting with 'F' to be False", inp, ii + 1, ParserState.FATAL);
                return new JNode(null, Dtype.NULL, start_utf8_pos);
            }
            if (cur_c == 'u')
            {
                // try undefined, because apparently some people want that?
                // https://github.com/kapilratnani/JSON-Viewer/pull/146
                // it will be parsed as null
                if (ii <= inp.Length - 9 && next_c == 'n' && inp.Substring(ii + 2, 7) == "defined")
                {
                    ii += 9;
                    HandleError("undefined is not part of any JSON specification", inp, start_utf8_pos - utf8_extra_bytes, ParserState.BAD);
                }
                else HandleError("Expected literal starting with 'u' to be undefined",
                                              inp, ii + 1, ParserState.FATAL);
                return new JNode(null, Dtype.NULL, start_utf8_pos);
            }
            HandleError("Badly located character", inp, ii, ParserState.FATAL);
            return new JNode(null, Dtype.NULL, start_utf8_pos);
        }

        /// <summary>
        /// Parse a JSON string and return a JNode representing the document.
        /// </summary>
        /// <param name="inp">the JSON string</param>
        /// <returns></returns>
        public JNode Parse(string inp)
        {
            Reset();
            if (inp.Length == 0)
            {
                HandleError("No input", inp, 0, ParserState.FATAL);
                return new JNode();
            }
            if (!ConsumeInsignificantChars(inp))
            {
                return new JNode();
            }
            if (ii >= inp.Length)
            {
                HandleError("Json string is only whitespace and maybe comments", inp, inp.Length - 1, ParserState.FATAL);
                return new JNode();
            }
            JNode json = ParseSomething(inp, 0);
            //if (include_extra_properties)
            //{
            //    json.extras = new ExtraJNodeProperties(null, ii, null);
            //}
            if (fatal)
            {
                return json;
            }
            if (!ConsumeInsignificantChars(inp))
            {
                return json;
            }
            if (ii < inp.Length)
            {
                HandleError($"At end of valid JSON document, got {inp[ii]} instead of EOF", inp, ii, ParserState.BAD);
            }
            return json;
        }

        /// <summary>
        /// Parse a JSON Lines document (a text file containing one or more \n-delimited lines
        /// where each line contains its own valid JSON document)
        /// as an array where the i^th element is the document on the i^th line.<br></br>
        /// See https://jsonlines.org/
        /// </summary>
        /// <param name="inp"></param>
        /// <returns></returns>
        public JNode ParseJsonLines(string inp)
        {
            Reset();
            if (inp.Length == 0)
            {
                HandleError("No input", inp, 0, ParserState.FATAL);
                return new JNode();
            }
            if (!ConsumeInsignificantChars(inp))
            {
                return new JNode();
            }
            if (ii >= inp.Length)
            {
                HandleError("Json string is only whitespace and maybe comments", inp, inp.Length - 1, ParserState.FATAL);
                return new JNode();
            }
            int last_ii = 0;
            JNode json;
            List<JNode> children = new List<JNode>();
            JArray arr = new JArray(0, children);
            int line_num = 0;
            while (ii < inp.Length)
            {
                json = ParseSomething(inp, 0);
                ConsumeInsignificantChars(inp);
                children.Add(json);
                if (fatal)
                {
                    return arr;
                }
                for (; last_ii < ii; last_ii++)
                {
                    if (inp[last_ii] == '\n')
                        line_num++;
                }
                // make sure this document was all in one line
                if (!(line_num == arr.Length
                    || (ii >= inp.Length && line_num == arr.Length - 1)))
                {
                    if (ii >= inp.Length)
                        ii = inp.Length - 1;
                    HandleError(
                        "JSON Lines document does not contain exactly one JSON document per line",
                        inp, ii, ParserState.FATAL
                    );
                    return arr;
                }
                if (!ConsumeInsignificantChars(inp))
                {
                    return arr;
                }
            }
            return arr;
        }

        /// <summary>
        /// reset the lint, position, and utf8_extra_bytes of this parser
        /// </summary>
        public void Reset()
        {
            lint.Clear();
            state = ParserState.STRICT;
            utf8_extra_bytes = 0;
            ii = 0;
        }

        /// <summary>
        /// create a new JsonParser with all the same settings as this one
        /// </summary>
        /// <returns></returns>
        public JsonParser Copy()
        {
            return new JsonParser(logger_level, parse_datetimes, throw_if_logged, throw_if_fatal);//, include_extra_properties);
        }
    }
    #endregion
        
}