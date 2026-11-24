using System.Text;
using System.Text.Json;

namespace QuotaRing.Core;

public static class JsonObjectText
{
    public static (int Start, int Length)? Find(string json, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals(key)) continue;
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject) return null;
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            return (Encoding.UTF8.GetCharCount(bytes.AsSpan(0, start)),
                Encoding.UTF8.GetCharCount(bytes.AsSpan(start, (int)reader.BytesConsumed - start)));
        }
        return null;
    }

    public static string Replace(string json, string key, string value)
    {
        if (Find(json, key) is { } span) return json[..span.Start] + value + json[(span.Start + span.Length)..];
        using var document = JsonDocument.Parse(json);
        var end = json.LastIndexOf('}');
        var separator = document.RootElement.EnumerateObject().Any() ? "," : "";
        return json[..end] + separator + JsonSerializer.Serialize(key) + ":" + value + json[end..];
    }

    public static string Remove(string json, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals(key)) continue;
            var start = Encoding.UTF8.GetCharCount(bytes.AsSpan(0, (int)reader.TokenStartIndex));
            reader.Read();
            reader.Skip();
            var end = Encoding.UTF8.GetCharCount(bytes.AsSpan(0, (int)reader.BytesConsumed));
            var next = end;
            while (char.IsWhiteSpace(json[next])) next++;
            if (json[next] == ',') end = next + 1;
            else
            {
                var previous = start - 1;
                while (char.IsWhiteSpace(json[previous])) previous--;
                if (json[previous] == ',') start = previous;
            }
            return json[..start] + json[end..];
        }
        return json;
    }
}
