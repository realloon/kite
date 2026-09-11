namespace Kite.Configuration;

public static class PromptStore {
    private const string ResourcePrefix = "kite.Prompts.";
    private const string Extension = ".md";

    public static string Resolve(string value) {
        if (!value.StartsWith('$')) {
            return value;
        }

        var name = value[1..];
        if (string.IsNullOrWhiteSpace(name)) {
            throw new InvalidOperationException("Prompt reference '$' is missing a name");
        }

        var resource = $"{ResourcePrefix}{name}{Extension}";
        using var stream = typeof(PromptStore).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException(
                               $"Prompt reference '{value}' not found ({resource}); available: {ListPrompts()}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ListPrompts() => typeof(PromptStore).Assembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        .Select(n => n[ResourcePrefix.Length..^Extension.Length])
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray() is { Length: > 0 } names
        ? string.Join(", ", names)
        : "(none)";
}