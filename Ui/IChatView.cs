namespace Kite.Ui;

public sealed record ChoiceResult(int Index, bool DeleteRequested);

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

    /// <summary>Show choices and return the selection, or null on cancel.</summary>
    Task<ChoiceResult?> ReadChoiceAsync(
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken,
        bool allowDelete = false);

    void SetModelName(string modelName);

    void SetSessionCost(string cost);

    /// <summary>Read a user input line; null means cancel/quit.</summary>
    Task<string?> ReadUserInputAsync(CancellationToken cancellationToken, Func<bool>? onEscape = null);
}