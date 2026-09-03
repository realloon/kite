namespace Kite.Ui;

/// <summary>
/// UI abstraction: the app owns conversation state; the view owns presentation state.
/// </summary>
public interface IChatView {
    void ShowWelcome();

    void AddUserMessage(string text);

    void LoadTranscript(IReadOnlyList<TranscriptItem> items, bool streaming);

    void StartAssistantTurn();

    void EndAssistantTurn();

    void AppendAssistantChunk(string chunk);

    void AppendReasoningChunk(string chunk);

    void AppendToolLine(string line);

    void WriteError(string message);

    void WriteInfo(string message);

    /// <summary>
    /// Prompt line + masked input (shows •, not stored in input history).
    /// Returns the text on Enter, null on Ctrl+C.
    /// </summary>
    Task<string?> ReadSecretAsync(string prompt, CancellationToken cancellationToken);

    /// <summary>Show choices and return the selected value, or null on cancel.</summary>
    Task<string?> ReadChoiceAsync(
        string prompt,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken);

    void SetModelName(string modelName);

    /// <summary>Read a user input line; null means cancel/quit.</summary>
    Task<string?> ReadUserInputAsync(CancellationToken cancellationToken, Func<bool>? onEscape = null);
}
