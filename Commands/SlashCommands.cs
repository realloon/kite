namespace Kite.Commands;

internal sealed record SlashCommand(
    string Name,
    string Description,
    params string[] Aliases) {
    public bool Matches(string input) =>
        string.Equals(Name, input, StringComparison.Ordinal) ||
        Aliases.Contains(input, StringComparer.Ordinal);

    public bool MatchesPrefix(string query) =>
        Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
        Aliases.Any(alias => alias.StartsWith(query, StringComparison.OrdinalIgnoreCase));
}

internal static class SlashCommands {
    public static IReadOnlyList<SlashCommand> All { get; } = [
        new("/connect", "Connect or change a provider API key"),
        new("/model", "Choose a provider and model"),
        new("/variants", "Change reasoning effort"),
        new("/new", "Start a new session"),
        new("/sessions", "Switch session"),
        new("/exit", "Exit kite", "/quit")
    ];

    public static SlashCommand? Find(string input) => All.FirstOrDefault(command => command.Matches(input));
}