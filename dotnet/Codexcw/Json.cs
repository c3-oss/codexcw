using System.Globalization;
using System.Text.Json;

namespace C3OSS.Codexcw;

/// <summary>
/// Lenient JsonElement accessors: agent payloads mix camelCase and snake_case
/// keys and encode numbers both as JSON numbers and as numeric strings.
/// </summary>
internal static class Json
{
    public static string GetString(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }
        return "";
    }

    public static string GetDualString(this JsonElement element, string camel, string snake)
    {
        var value = element.GetString(camel);
        return value.Length > 0 ? value : element.GetString(snake);
    }

    public static bool GetBool(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    public static bool GetDualBool(this JsonElement element, string camel, string snake) =>
        element.GetBool(camel) || element.GetBool(snake);

    public static JsonElement? GetObject(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }
        return null;
    }

    public static JsonElement? GetElement(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return value;
        }
        return null;
    }

    public static long GetLong(this JsonElement element, string name) =>
        element.GetElement(name) is { } value ? value.FlexInt64() : 0;

    public static long GetDualLong(this JsonElement element, string camel, string snake)
    {
        var value = element.GetLong(camel);
        return value != 0 ? value : element.GetLong(snake);
    }

    public static double GetDouble(this JsonElement element, string name) =>
        element.GetElement(name) is { } value ? value.FlexDouble() : 0;

    public static double GetDualDouble(this JsonElement element, string camel, string snake)
    {
        var value = element.GetDouble(camel);
        return value != 0 ? value : element.GetDouble(snake);
    }

    public static string GetDualScalarString(this JsonElement element, string camel, string snake)
    {
        var value = element.GetElement(camel)?.ScalarString() ?? "";
        return value.Length > 0 ? value : element.GetElement(snake)?.ScalarString() ?? "";
    }

    public static long FlexInt64(this JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var number))
                {
                    return number;
                }
                return (long)element.GetDouble();
            case JsonValueKind.String:
                var text = (element.GetString() ?? "").Trim();
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;
            default:
                return 0;
        }
    }

    public static double FlexDouble(this JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.GetDouble();
            case JsonValueKind.String:
                var text = (element.GetString() ?? "").Trim();
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;
            default:
                return 0;
        }
    }

    public static string ScalarString(this JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => element.GetRawText(),
    };
}

/// <summary>
/// Strict JsonElement accessors for agent event decoding: a missing or null
/// field keeps its default, but a field present with an incompatible type is a
/// decode failure, matching the typed deserialization in the Go and Rust
/// implementations.
/// </summary>
internal static class StrictJson
{
    public static string GetStrictString(this JsonElement element, string name)
    {
        if (element.GetElement(name) is not { } value)
        {
            return "";
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Mismatch(name, "string", value);
        }
        return value.GetString() ?? "";
    }

    public static bool GetStrictBool(this JsonElement element, string name)
    {
        if (element.GetElement(name) is not { } value)
        {
            return false;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Mismatch(name, "bool", value),
        };
    }

    public static long GetStrictLong(this JsonElement element, string name)
    {
        if (element.GetElement(name) is not { } value)
        {
            return 0;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw Mismatch(name, "integer", value);
        }
        return number;
    }

    public static int? GetStrictNullableInt(this JsonElement element, string name)
    {
        if (element.GetElement(name) is not { } value)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw Mismatch(name, "integer", value);
        }
        return number;
    }

    public static double GetStrictDouble(this JsonElement element, string name)
    {
        if (element.GetElement(name) is not { } value)
        {
            return 0;
        }
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw Mismatch(name, "number", value);
        }
        return value.GetDouble();
    }

    public static IReadOnlyList<string> GetStrictStringList(this JsonElement element, string name)
    {
        if (element.GetElement(name) is not { } value)
        {
            return [];
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Mismatch(name, "array", value);
        }
        var parsed = new List<string>(value.GetArrayLength());
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw Mismatch(name, "string array", entry);
            }
            parsed.Add(entry.GetString() ?? "");
        }
        return parsed;
    }

    private static FormatException Mismatch(string name, string expected, JsonElement value) =>
        new($"field {name}: expected {expected}, got {value.ValueKind.ToString().ToLowerInvariant()}");
}
