namespace Kite.Ui;

internal sealed record SlashCommand(string Name, string Description, params string[] Aliases) {
    public bool Matches(string input) =>
        Name.Equals(input, StringComparison.Ordinal) ||
        Aliases.Contains(input, StringComparer.Ordinal);

    public bool MatchesPrefix(string query) =>
        Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
        Aliases.Any(alias => alias.StartsWith(query, StringComparison.OrdinalIgnoreCase));
}

internal static class SlashCommands {
    public static IReadOnlyList<SlashCommand> All { get; } = [
        new("/connect", "Connect or change a provider API key"),
        new("/model", "Switch model"),
        new("/variants", "Change reasoning effort"),
        new("/undo", "Undo last turn and restore files"),
        new("/compact", "Compact conversation context"),
        new("/new", "Start a new session"),
        new("/sessions", "Switch session", "resume"),
        new("/stats", "Show session statistics"),
        new("/exit", "Exit kite", "/quit")
    ];

    public static SlashCommand? Find(string input) {
        var commandName = input.Split(' ', 2)[0];
        return All.FirstOrDefault(command => command.Matches(commandName));
    }
}