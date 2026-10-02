using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LocalCaption.Core.Interview;

/// <summary>
/// A small JSON tree for building and reading JSON-RPC messages without a type per method —
/// the port of <c>JSONValue</c> in <c>CodexRPC.swift</c>. Integers and doubles stay distinct
/// (as in Swift), equality is structural, and <see cref="Line"/> writes exactly what the Mac
/// app's <c>JSONEncoder</c> writes with <c>[.sortedKeys, .withoutEscapingSlashes]</c>.
/// </summary>
public abstract record JsonValue
{
    private JsonValue() { }

    /// <summary>JSON <c>null</c>.</summary>
    public sealed record Null : JsonValue
    {
        /// <summary>The one null value.</summary>
        public static Null Instance { get; } = new();
    }

    /// <summary>JSON <c>true</c> / <c>false</c>.</summary>
    public sealed record Bool(bool Value) : JsonValue;

    /// <summary>A number that decoded as a 64-bit integer (<c>1</c>, <c>1.0</c>, <c>1e2</c>).</summary>
    public sealed record Int(long Value) : JsonValue;

    /// <summary>Any other finite number.</summary>
    public sealed record Double(double Value) : JsonValue
    {
        /// <summary>IEEE equality, as Swift's <c>==</c>: <c>0.0 == -0.0</c>, NaN is never equal.</summary>
        public bool Equals(Double? other) => other is not null && Value == other.Value;

        /// <inheritdoc />
        public override int GetHashCode() => Value == 0 ? 0 : Value.GetHashCode();
    }

    /// <summary>A JSON string.</summary>
    public sealed record String(string Value) : JsonValue;

    /// <summary>A JSON array; equality compares the elements in order.</summary>
    public sealed record Array(IReadOnlyList<JsonValue> Items) : JsonValue
    {
        /// <inheritdoc />
        public bool Equals(Array? other) => other is not null && Items.SequenceEqual(other.Items);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var item in Items) hash.Add(item);
            return hash.ToHashCode();
        }
    }

    /// <summary>A JSON object; equality ignores member order.</summary>
    public sealed record Object(IReadOnlyDictionary<string, JsonValue> Members) : JsonValue
    {
        /// <inheritdoc />
        public bool Equals(Object? other)
        {
            if (other is null || Members.Count != other.Members.Count) return false;
            foreach (var (key, value) in Members)
                if (!other.Members.TryGetValue(key, out var theirs) || !value.Equals(theirs)) return false;
            return true;
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = 0;
            foreach (var (key, value) in Members) hash ^= HashCode.Combine(key, value);
            return hash;
        }
    }

    // ── Construction ────────────────────────────────────────────────────────────────────

    /// <summary>An object from key/value pairs (Swift's <c>.object([...])</c> literal).</summary>
    public static Object Obj(params ReadOnlySpan<(string Key, JsonValue Value)> members)
    {
        var dict = new Dictionary<string, JsonValue>(members.Length, StringComparer.Ordinal);
        foreach (var (key, value) in members) dict[key] = value;
        return new Object(dict);
    }

    /// <summary>An array from its elements.</summary>
    public static Array Arr(params ReadOnlySpan<JsonValue> items) => new(items.ToArray());

    /// <summary>A string literal is a JSON string.</summary>
    public static implicit operator JsonValue(string value) => new String(value);

    /// <summary>A bool literal is a JSON bool.</summary>
    public static implicit operator JsonValue(bool value) => new Bool(value);

    /// <summary>An integer is a JSON integer.</summary>
    public static implicit operator JsonValue(long value) => new Int(value);

    // ── Reading ─────────────────────────────────────────────────────────────────────────

    /// <summary>The member <paramref name="key"/> of an object; <c>null</c> for a missing key or a non-object.</summary>
    public JsonValue? this[string key] => this is Object o && o.Members.TryGetValue(key, out var v) ? v : null;

    /// <summary>The string, or <c>null</c> if this is not a string.</summary>
    public string? StringValue => this is String s ? s.Value : null;

    /// <summary>The bool, or <c>null</c> if this is not a bool.</summary>
    public bool? BoolValue => this is Bool b ? b.Value : null;

    /// <summary>The elements, or <c>null</c> if this is not an array.</summary>
    public IReadOnlyList<JsonValue>? ArrayValue => this is Array a ? a.Items : null;

    /// <summary>The members, or <c>null</c> if this is not an object.</summary>
    public IReadOnlyDictionary<string, JsonValue>? ObjectValue => this is Object o ? o.Members : null;

    /// <summary>An integer, or a double with no fractional part; otherwise <c>null</c>.</summary>
    public long? IntValue => this switch
    {
        Int i => i.Value,
        Double d when IsExactInt64(d.Value) => (long)d.Value,
        _ => null,
    };

    // ── Parsing ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a JSON document the way Swift's <c>JSONDecoder</c> decodes <c>JSONValue</c>: a
    /// number that is exactly a 64-bit integer becomes <see cref="Int"/> (so <c>1.0</c> and
    /// <c>1e2</c> do), anything else <see cref="Double"/>; a duplicate key keeps the first value.
    /// </summary>
    /// <exception cref="JsonException">Not one well-formed JSON value.</exception>
    public static JsonValue Parse(string json)
    {
        var options = new JsonDocumentOptions { MaxDepth = 512 };
        using var doc = JsonDocument.Parse(json, options);
        return From(doc.RootElement);
    }

    /// <summary><see cref="Parse"/>, or <c>null</c> for anything that is not valid JSON.</summary>
    public static JsonValue? TryParse(string json)
    {
        try { return Parse(json); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { return null; }
    }

    private static JsonValue From(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null: return Null.Instance;
            case JsonValueKind.True: return new Bool(true);
            case JsonValueKind.False: return new Bool(false);
            case JsonValueKind.String: return new String(e.GetString()!);
            case JsonValueKind.Number:
                if (e.TryGetInt64(out var i)) return new Int(i);
                if (!e.TryGetDouble(out var d) || !double.IsFinite(d))
                    throw new JsonException($"number {e.GetRawText()} is not representable");
                // Swift decodes Int64 first, which accepts any exactly-integral value in range.
                if (IsExactInt64(d)) return new Int((long)d);
                return new Double(d);
            case JsonValueKind.Array:
                return new Array(e.EnumerateArray().Select(From).ToArray());
            case JsonValueKind.Object:
                var dict = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject()) dict.TryAdd(p.Name, From(p.Value));
                return new Object(dict);
            default:
                throw new JsonException($"unexpected JSON value kind {e.ValueKind}");
        }
    }

    /// <summary>Integral and inside the <see cref="long"/> range, so the cast is exact.</summary>
    private static bool IsExactInt64(double d) =>
        d == Math.Floor(d) && d >= -9.2233720368547758E18 && d < 9.2233720368547758E18;

    // ── Writing ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One line of newline-delimited JSON (no pretty printing, no trailing newline): keys sorted
    /// ordinally, <c>/</c> not escaped, non-ASCII written as is, doubles formatted as Swift
    /// formats them.
    /// </summary>
    /// <remarks>
    /// Sorting only makes the output deterministic. The server reads keys in any order and the
    /// shared vectors compare JSON by structure, so this does not copy the Mac encoder's
    /// locale-aware key order (which puts <c>a2</c> before <c>a10</c>).
    /// </remarks>
    /// <exception cref="InvalidOperationException">A NaN or infinite double (Swift throws too).</exception>
    public string Line()
    {
        var sb = new StringBuilder();
        Write(this, sb);
        return sb.ToString();
    }

    /// <summary>The <see cref="Line"/> form, for diagnostics.</summary>
    public sealed override string ToString()
    {
        try { return Line(); }
        catch (InvalidOperationException) { return "<non-finite JSON number>"; }
    }

    private static void Write(JsonValue value, StringBuilder sb)
    {
        switch (value)
        {
            case Null: sb.Append("null"); break;
            case Bool b: sb.Append(b.Value ? "true" : "false"); break;
            case Int i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case Double d: sb.Append(FormatDouble(d.Value)); break;
            case String s: WriteString(s.Value, sb); break;
            case Array a:
                sb.Append('[');
                for (var n = 0; n < a.Items.Count; n++)
                {
                    if (n > 0) sb.Append(',');
                    Write(a.Items[n], sb);
                }
                sb.Append(']');
                break;
            case Object o:
                sb.Append('{');
                var first = true;
                foreach (var key in o.Members.Keys.Order(StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(key, sb);
                    sb.Append(':');
                    Write(o.Members[key], sb);
                }
                sb.Append('}');
                break;
        }
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case < ' ': sb.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Round-trippable, culture-invariant. Nothing this app sends contains a double.</summary>
    internal static string FormatDouble(double d) =>
        double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture)
                           : throw new InvalidOperationException("JSON has no NaN or infinity");
}
