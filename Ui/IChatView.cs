namespace Kite.Ui;

/// <summary>
/// UI abstraction: KiteApp depends only on this; implementations
/// (Spectre, multi-panel, custom renderer) are replaceable.
/// </summary>
public interface IChatView {
    void ShowWelcome();

    /// <summary>Start an assistant turn: enter streaming state, reset the pacer and interrupt token.</summary>
    void StartAssistantTurn();

    /// <summary>Feed agent output into streaming rendering (the pacer may buffer it).</summary>
    void AppendAssistantChunk(string chunk);

    /// <summary>Tool action line (dim, appended after the current text, before the tool runs).</summary>
    void AppendToolLine(string line);

    /// <summary>End the turn: drain the buffer, print the meta line with real token usage.</summary>
    Task<TurnMeta> EndAssistantTurnAsync(bool interrupted, int promptTokens, int completionTokens);

    /// <summary>Error line for a failed turn (appended to the content area).</summary>
    void WriteError(string message);

    /// <summary>Plain info line (dim).</summary>
    void WriteInfo(string message);

    /// <summary>
    /// Prompt line + masked input (shows •, not stored in input history).
    /// Returns the text on Enter, null on Ctrl+C.
    /// </summary>
    Task<string?> ReadSecretAsync(string prompt, CancellationToken cancellationToken);

    /// <summary>
    /// Prompt line + plain input (goes to input history).
    /// Returns the text on Enter, null on Ctrl+C.
    /// </summary>
    Task<string?> ReadTextAsync(string prompt, CancellationToken cancellationToken);

    /// <summary>Update the model name shown in the footer (applies on the next band paint).</summary>
    void SetModelName(string modelName);

    /// <summary>Read a user input line; null means cancel/quit.</summary>
    Task<string?> ReadUserInputAsync(CancellationToken cancellationToken);

    /// <summary>Cancellation token for the current turn: Esc during streaming cancels it.</summary>
    CancellationToken TurnCancellationToken { get; }
}