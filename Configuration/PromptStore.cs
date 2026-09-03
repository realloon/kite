namespace Kite.Configuration;

/// <summary>
/// Embedded prompt documents (Prompts/*.md, shipped inside the binary).
/// presets.json references them as "$name" so long instructions do not have
/// to live inside JSON. A reference that does not resolve fails loudly at
/// startup; plain text without the "$" prefix passes through untouched.
/// </summary>
public static class PromptStore {
    private const string ResourcePrefix = "Kite.Prompts.";
    private const string Extension = ".md";

    /// <summary>Resolve "$name" to the embedded prompt text; any other value passes through.</summary>
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

    private static string ListPrompts() =>
        typeof(PromptStore).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..^Extension.Length])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray() is { Length: > 0 } names
            ? string.Join(", ", names)
            : "(none)";
}
