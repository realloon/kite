namespace Kite.Ui;

/// <summary>
/// Per-turn summary shown after each reply: duration and token usage.
/// </summary>
public sealed record TurnMeta(TimeSpan Duration, int PromptTokens, int CompletionTokens, bool Interrupted) {
    public override string ToString() =>
        $"{Duration.TotalSeconds:F1}s (↑{PromptTokens} ↓{CompletionTokens}{(Interrupted ? " ⏹" : "")})";
}