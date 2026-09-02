namespace Kite.Ui;

/// <summary>
/// UI abstraction: the app owns conversation state; the view owns presentation state.
/// </summary>
public interface IChatView {
    void ShowWelcome();

    void AddUserMessage(string text);

    void ResetTranscript();

    void StartAssistantTurn();

    void AppendAssistantChunk(string chunk);

    void AppendReasoningChunk(string chunk);

    void AppendToolLine(string line);

    Task<TurnMeta> EndAssistantTurnAsync(bool interrupted, int promptTokens, int completionTokens);

    void WriteError(string message);

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

    void SetModelName(string modelName);

    /// <summary>Read a user input line; null means cancel/quit.</summary>
    Task<string?> ReadUserInputAsync(CancellationToken cancellationToken);

    /// <summary>Cancellation token for the current turn: Esc during streaming cancels it.</summary>
    CancellationToken TurnCancellationToken { get; }
}
