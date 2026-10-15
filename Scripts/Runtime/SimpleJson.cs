using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tauntastic.WebBrowser
{
    /// <summary>Minimal JSON reader used for Chrome DevTools Protocol messages and for page script results. Objects become Dictionary of string to object, arrays become List of object, numbers become double, and strings, booleans and null map to string, bool and null. Needed because JsonUtility cannot read dictionaries.</summary>
    public static class SimpleJson
    {
        /// <summary>Parses JSON text. Returns false when the text is not valid JSON.</summary>
        public static bool TryParse(string text, out object value)
        {
            value = null;
            if (string.IsNullOrEmpty(text))
                return false;

            try
            {
                var reader = new Reader(text);
                value = reader.ReadDocument();
                return true;
            }
            catch (FormatException)
            {
                value = null;
                return false;
            }
        }

        /// <summary>Returns the string value of a key, or an empty string when it is missing or not a string.</summary>
        public static string GetString(Dictionary<string, object> dictionary, string key)
        {
            object value;
            if (dictionary != null && dictionary.TryGetValue(key, out value) && value is string)
                return (string)value;
            return string.Empty;
        }

        /// <summary>Returns the object value of a key, or null when it is missing or not an object.</summary>
        public static Dictionary<string, object> GetObject(Dictionary<string, object> dictionary, string key)
        {
            object value;
            if (dictionary != null && dictionary.TryGetValue(key, out value))
                return value as Dictionary<string, object>;
            return null;
        }

        /// <summary>Returns the bool value of a key, or false when it is missing or not a bool.</summary>
        public static bool GetBool(Dictionary<string, object> dictionary, string key)
        {
            object value;
            if (dictionary != null && dictionary.TryGetValue(key, out value) && value is bool)
                return (bool)value;
            return false;
        }

        /// <summary>Recursive descent reader that turns JSON text into dictionaries, lists, strings, numbers, booleans and null.</summary>
        private sealed class Reader
        {
            private readonly string _text;
            private int _index;

            public Reader(string text)
            {
                _text = text;
            }

            public object ReadDocument()
            {
                SkipWhitespace();
                object value = ReadValue();
                SkipWhitespace();
                if (_index != _text.Length)
                    throw new FormatException("Unexpected data after the JSON value.");
                return value;
            }

            private object ReadValue()
            {
                SkipWhitespace();
                if (_index >= _text.Length)
                    throw new FormatException("Unexpected end of JSON.");

                char c = _text[_index];
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return ReadString();
                if (c == 't') { ReadLiteral("true"); return true; }
                if (c == 'f') { ReadLiteral("false"); return false; }
                if (c == 'n') { ReadLiteral("null"); return null; }
                return ReadNumber();
            }

            private Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>();
                _index++;
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _index++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                        throw new FormatException("Expected a property name.");
                    string key = ReadString();
                    SkipWhitespace();
                    Expect(':');
                    result[key] = ReadValue();
                    SkipWhitespace();
                    char next = Next();
                    if (next == '}')
                        return result;
                    if (next != ',')
                        throw new FormatException("Expected ',' or '}'.");
                }
            }

            private List<object> ReadArray()
            {
                var result = new List<object>();
                _index++;
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _index++;
                    return result;
                }

                while (true)
                {
                    result.Add(ReadValue());
                    SkipWhitespace();
                    char next = Next();
                    if (next == ']')
                        return result;
                    if (next != ',')
                        throw new FormatException("Expected ',' or ']'.");
                }
            }

            private string ReadString()
            {
                Expect('"');
                var builder = new StringBuilder();
                while (true)
                {
                    char c = Next();
                    if (c == '"')
                        return builder.ToString();

                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }

                    char escape = Next();
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (_index + 4 > _text.Length)
                                throw new FormatException("Bad unicode escape.");
                            int code;
                            if (!int.TryParse(_text.Substring(_index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                throw new FormatException("Bad unicode escape.");
                            builder.Append((char)code);
                            _index += 4;
                            break;
                        default:
                            throw new FormatException("Bad escape sequence.");
                    }
                }
            }

            private object ReadNumber()
            {
                int start = _index;
                while (_index < _text.Length && "+-0123456789.eE".IndexOf(_text[_index]) >= 0)
                    _index++;

                double number;
                if (_index == start ||
                    !double.TryParse(_text.Substring(start, _index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    throw new FormatException("Bad number.");
                return number;
            }

            private void ReadLiteral(string literal)
            {
                if (string.CompareOrdinal(_text, _index, literal, 0, literal.Length) != 0)
                    throw new FormatException("Bad literal.");
                _index += literal.Length;
            }

            private void SkipWhitespace()
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                    _index++;
            }

            private char Peek()
            {
                if (_index >= _text.Length)
                    throw new FormatException("Unexpected end of JSON.");
                return _text[_index];
            }

            private char Next()
            {
                char c = Peek();
                _index++;
                return c;
            }

            private void Expect(char expected)
            {
                if (Next() != expected)
                    throw new FormatException("Expected '" + expected + "'.");
            }
        }
    }
}
