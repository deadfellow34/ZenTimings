using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZenTimings
{
    /// <summary>
    /// A small indenting JSON writer. .NET Framework 4.5 has no System.Text.Json and the project
    /// carries no JSON package, so the export writes its own - it only ever emits objects, arrays
    /// and scalars, which is all a report needs.
    /// </summary>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private int _indent;
        private bool _pendingComma;
        private bool _afterPropertyName;

        public JsonWriter BeginObject()
        {
            WritePrefix();
            _sb.Append('{');
            _indent++;
            _pendingComma = false;
            return this;
        }

        public JsonWriter EndObject()
        {
            _indent--;
            if (_pendingComma)
            {
                _sb.AppendLine();
                _sb.Append(Pad());
            }
            _sb.Append('}');
            _pendingComma = true;
            _afterPropertyName = false;
            return this;
        }

        public JsonWriter BeginArray()
        {
            WritePrefix();
            _sb.Append('[');
            _indent++;
            _pendingComma = false;
            return this;
        }

        public JsonWriter EndArray()
        {
            _indent--;
            if (_pendingComma)
            {
                _sb.AppendLine();
                _sb.Append(Pad());
            }
            _sb.Append(']');
            _pendingComma = true;
            _afterPropertyName = false;
            return this;
        }

        public JsonWriter PropertyName(string name)
        {
            WritePrefix();
            _sb.Append(Quote(name));
            _sb.Append(": ");
            _afterPropertyName = true;
            return this;
        }

        public JsonWriter Property(string name, object value)
        {
            PropertyName(name);
            WriteValue(value);
            return this;
        }

        public JsonWriter WriteNull()
        {
            WriteValue(null);
            return this;
        }

        public JsonWriter WriteValue(object value)
        {
            WritePrefix();
            _sb.Append(Literal(value));
            _pendingComma = true;
            return this;
        }

        public JsonWriter WriteDictionary(IDictionary<string, object> values)
        {
            BeginObject();
            if (values != null)
            {
                foreach (var kvp in values)
                    Property(kvp.Key, kvp.Value);
            }
            EndObject();
            return this;
        }

        private void WritePrefix()
        {
            if (_afterPropertyName)
            {
                _afterPropertyName = false;
                return;
            }

            if (_pendingComma)
                _sb.Append(',');

            if (_sb.Length > 0)
            {
                _sb.AppendLine();
                _sb.Append(Pad());
            }
        }

        private string Pad()
        {
            return _indent > 0 ? new string(' ', _indent * 2) : string.Empty;
        }

        private static string Literal(object value)
        {
            if (value == null)
                return "null";

            if (value is bool)
                return (bool)value ? "true" : "false";

            if (value is string)
                return Quote((string)value);

            if (value is float)
                return Number(((float)value).ToString("R", CultureInfo.InvariantCulture));
            if (value is double)
                return Number(((double)value).ToString("R", CultureInfo.InvariantCulture));
            if (value is decimal)
                return Number(((decimal)value).ToString(CultureInfo.InvariantCulture));

            if (value is byte || value is sbyte || value is short || value is ushort
                || value is int || value is uint || value is long || value is ulong)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            // Enums and everything else become strings; a report is read by humans first.
            return Quote(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        /// <summary>NaN and Infinity are not valid JSON - emit them as null.</summary>
        private static string Number(string text)
        {
            if (text == "NaN" || text == "Infinity" || text == "-Infinity")
                return "null";
            return text;
        }

        private static string Quote(string value)
        {
            if (value == null)
                return "null";

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        public override string ToString()
        {
            return _sb.ToString();
        }
    }
}
