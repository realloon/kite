using System.Text.Json;

namespace Kite.Tools;

internal static class ToolArgs {
    public static JsonElement Schema(string json) {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonDocument ParseObject(string arguments, string toolName) {
        JsonDocument document;
        try {
            document = JsonDocument.Parse(arguments);
        } catch (JsonException ex) {
            throw new InvalidOperationException($"{toolName} arguments are invalid JSON", ex);
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object) {
            return document;
        }

        document.Dispose();
        throw new InvalidOperationException($"{toolName} arguments must be an object");
    }

    public static void RejectUnknownProperties(JsonElement root, string toolName, params ReadOnlySpan<string> allowed) {
        foreach (var property in root.EnumerateObject()) {
            var known = false;
            foreach (var candidate in allowed) {
                if (!property.Name.Equals(candidate, StringComparison.Ordinal)) continue;

                known = true;
                break;
            }

            if (!known) {
                throw new InvalidOperationException($"{toolName} does not support argument '{property.Name}'");
            }
        }
    }

    public static string RequiredString(JsonElement root, string toolName, string name) {
        if (!root.TryGetProperty(name, out var value)) {
            throw new InvalidOperationException($"{toolName} requires string field '{name}'");
        }

        if (value.ValueKind != JsonValueKind.String) {
            throw new InvalidOperationException($"{toolName} field '{name}' must be a string");
        }

        return value.GetString() ?? throw new InvalidOperationException($"{toolName} field '{name}' is null");
    }

    public static int OptionalPositiveInteger(JsonElement root, string toolName, string name, int defaultValue,
        int? maximum = null) {
        if (!root.TryGetProperty(name, out var value)) {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < 1) {
            throw new InvalidOperationException($"{toolName} field '{name}' must be a positive integer");
        }

        return result > maximum
            ? throw new InvalidOperationException($"{toolName} field '{name}' must be at most {maximum}")
            : result;
    }
}