namespace Kite;

internal sealed record SlashCommand(string Name, string Description);

internal static class SlashCommands {
    public static IReadOnlyList<SlashCommand> All { get; } = [
        new("/connect", "Connect or change the DeepSeek API key"),
        new("/variants", "Change reasoning effort"),
        new("/new", "Start a new session"),
        new("/exit", "Exit kite")
    ];
}
