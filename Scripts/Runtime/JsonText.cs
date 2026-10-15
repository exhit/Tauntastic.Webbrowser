using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tauntastic.WebBrowser
{
    /// <summary>Writes JSON text: quoting strings for commands and turning script results (as returned by <see cref="SimpleJson"/> and <see cref="WebBrowserSession.EvaluateAsync"/>) back into text.</summary>
    public static class JsonText
    {
        /// <summary>The string as a JSON string literal, with quotes, for example ready to put into a command or a JavaScript source.</summary>
        public static string Quote(string text)
        {
            var builder = new StringBuilder("\"");
            foreach (char c in text ?? string.Empty)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            builder.Append(c);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }

        /// <summary>Compact JSON of a value made of strings, numbers, booleans, null, lists and dictionaries.</summary>
        public static string ToJson(object value)
        {
            var builder = new StringBuilder();
            Write(builder, value);
            return builder.ToString();
        }

        private static void Write(StringBuilder builder, object value)
        {
            if (value == null)
                builder.Append("null");
            else if (value is string)
                builder.Append(Quote((string)value));
            else if (value is bool)
                builder.Append((bool)value ? "true" : "false");
            else if (value is double)
                builder.Append(((double)value).ToString("R", CultureInfo.InvariantCulture));
            else if (value is IDictionary<string, object>)
            {
                builder.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in (IDictionary<string, object>)value)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append(Quote(pair.Key)).Append(':');
                    Write(builder, pair.Value);
                }
                builder.Append('}');
            }
            else if (value is IEnumerable)
            {
                builder.Append('[');
                bool first = true;
                foreach (object item in (IEnumerable)value)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    Write(builder, item);
                }
                builder.Append(']');
            }
            else
                builder.Append(Quote(value.ToString()));
        }
    }
}
