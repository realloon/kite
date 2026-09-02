namespace Kite.Ui;

/// <summary>
/// UI abstraction: the app owns conversation state; the view owns presentation state.
/// </summary>
public interface IChatView {
    void ShowWelcome();

    void AddUserMessage(string text);

    /// <summary>Clear the visible transcript and return to the welcome state.</summary>
    void ResetTranscript();

    /// <summary>Start an assistant turn and make its entry visible.</summary>
    void StartAssistantTurn();

    /// <summary>Append assistant text to the active entry.</summary>
    void AppendAssistantChunk(string chunk);

    /// <summary>Append a provider-supplied reasoning summary.</summary>
    void AppendReasoningChunk(string chunk);

    /// <summary>Add a tool action entry.</summary>
    void AppendToolLine(string line);

    /// <summary>Finish the turn and show its usage metadata.</summary>
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