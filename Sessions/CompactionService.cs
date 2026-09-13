using System.Text;
using Kite.Agent;

namespace Kite.Sessions;

internal static class CompactionService {
    private const int MaxToolOutputChars = 2_000;

    private const string CompactionHeader =
        "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";

    private const string CompactionFooter = "\n</summary>";

    private const string CompactionAckText =
        "I have reviewed the summary of previous work and I am ready to continue.";

    private const string InitialSummarizationPrompt = """
                                                      The messages above are a conversation to summarize. Create a structured context summary that another coding agent will use to continue the work.

                                                      Follow this EXACT format:

                                                      ## Goal
                                                      [What is the user trying to accomplish? Brief description of overall goals and intent.]

                                                      ## Key Details & Constraints
                                                      - [User preferences, architectural constraints, dependencies, conventions, or "(none)"]

                                                      ## Work Done
                                                      - [x] [Specific changes made, bugs fixed, files edited/created, or commands run]

                                                      ## Current State & Remaining Work
                                                      - [ ] [What was in progress or remaining to be done, current blockers or unknowns, or "(none)"]

                                                      ## Key Decisions
                                                      - **[Decision]**: [Brief rationale]

                                                      ## Relevant Files
                                                      - [file or directory path: why it matters, or "(none)"]

                                                      Rules:
                                                      - Keep every section concise. Prefer terse bullet points over long prose.
                                                      - Preserve exact file paths, symbols, function names, error messages, and URLs.
                                                      - Do not mention the summarization process itself.
                                                      """;

    private const string UpdateSummarizationPrompt = """
                                                     The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.

                                                     Update the existing structured summary with new information:
                                                     - Carry forward goals, decisions, constraints, and file references from <previous-summary>.
                                                     - Add new progress, decisions, and context from the new conversation.
                                                     - Move completed items from Remaining Work to Work Done.
                                                     - If something is resolved or no longer relevant, you may remove it.
                                                     - Where the new conversation conflicts with the previous summary, the newer conversation wins.

                                                     Follow this EXACT format:

                                                     ## Goal
                                                     [Preserve existing goals, add new ones if the task expanded]

                                                     ## Key Details & Constraints
                                                     - [Preserve existing, add new ones discovered]

                                                     ## Work Done
                                                     - [x] [Include previously completed items AND newly completed items]

                                                     ## Current State & Remaining Work
                                                     - [ ] [Current state and what remains to be done]

                                                     ## Key Decisions
                                                     - **[Decision]**: [Brief rationale] (preserve all previous, add new)

                                                     ## Relevant Files
                                                     - [file or directory path: why it matters, or "(none)"]

                                                     Rules:
                                                     - Keep every section concise. Prefer terse bullet points over long prose.
                                                     - Preserve exact file paths, symbols, function names, error messages, and URLs.
                                                     - Do not mention the summarization process itself.
                                                     """;

    public static bool IsCompactionSummary(string content) =>
        content.StartsWith(CompactionHeader, StringComparison.Ordinal) &&
        content.EndsWith(CompactionFooter, StringComparison.Ordinal);

    public static bool IsCompactionAck(string content) => content.Equals(CompactionAckText, StringComparison.Ordinal);

    public static bool IsAlreadyCompacted(IReadOnlyList<ConversationMessage> messages) {
        if (messages.Count != 2) {
            return false;
        }

        return IsCompactionSummary(messages[0].Content) && IsCompactionAck(messages[1].Content);
    }

    public static string ExtractSummary(string content) {
        if (!IsCompactionSummary(content)) {
            return content;
        }

        var inner = content[CompactionHeader.Length..^CompactionFooter.Length];
        return inner.Trim();
    }

    public static string ExtractGoalTitle(string summary) {
        var lines = summary.Split('\n');
        var inGoal = false;
        foreach (var line in lines) {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("## Goal", StringComparison.OrdinalIgnoreCase)) {
                inGoal = true;
                continue;
            }

            if (!inGoal) continue;
            if (trimmed.StartsWith('#')) break;

            if (trimmed.Length > 0) {
                return trimmed.TrimStart('-', '*', ' ');
            }
        }

        return "Compacted session";
    }

    public static IReadOnlyList<ConversationMessage> CreateCompactedMessages(string summary) {
        var summaryUserMessage = ConversationMessage.User($"{CompactionHeader}{summary.Trim()}{CompactionFooter}");
        var ackAssistantMessage = ConversationMessage.Assistant(CompactionAckText);
        return [summaryUserMessage, ackAssistantMessage];
    }

    private static string SerializeConversation(IReadOnlyList<ConversationMessage> messages) {
        var builder = new StringBuilder();
        foreach (var message in messages) {
            switch (message.Role) {
                case "user": {
                    if (builder.Length > 0) {
                        builder.Append("\n\n");
                    }

                    builder.Append("[User]:\n").Append(message.Content);
                    break;
                }
                case "assistant": {
                    if (builder.Length > 0) {
                        builder.Append("\n\n");
                    }

                    builder.Append("[Assistant]:\n").Append(message.Content);
                    break;
                }
                default: {
                    switch (message.Type) {
                        case ConversationMessage.FunctionCallType: {
                            if (builder.Length > 0) {
                                builder.Append("\n\n");
                            }

                            builder.Append("[Assistant tool call]: ")
                                .Append(message.Name)
                                .Append('(')
                                .Append(message.Arguments)
                                .Append(')');
                            break;
                        }
                        case ConversationMessage.FunctionCallOutputType: {
                            if (builder.Length > 0) {
                                builder.Append("\n\n");
                            }

                            builder.Append("[Tool result]:\n").Append(TruncateToolOutput(message.Content));
                            break;
                        }
                    }

                    break;
                }
            }
        }

        return builder.ToString();
    }

    public static string BuildPrompt(IReadOnlyList<ConversationMessage> messages, string? focus) {
        string? previousSummary = null;
        IEnumerable<ConversationMessage> messagesToSerialize = messages;

        if (messages.Count >= 2 && IsCompactionSummary(messages[0].Content) && IsCompactionAck(messages[1].Content)) {
            previousSummary = ExtractSummary(messages[0].Content);
            messagesToSerialize = messages.Skip(2);
        }

        var conversationText = SerializeConversation([.. messagesToSerialize]);
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(previousSummary)) {
            builder.Append("<previous-summary>\n")
                .Append(previousSummary)
                .Append("\n</previous-summary>\n\n");
        }

        builder.Append("<conversation>\n")
            .Append(conversationText)
            .Append("\n</conversation>\n\n");

        builder.Append(string.IsNullOrWhiteSpace(previousSummary)
            ? InitialSummarizationPrompt
            : UpdateSummarizationPrompt);

        if (!string.IsNullOrWhiteSpace(focus)) {
            builder.Append("\n\n**User Focus Directive:**\n")
                .Append(focus)
                .Append("\nPlease ensure your summary prominently addresses this focus.\n");
        }

        return builder.ToString();
    }

    public static string CleanSummaryText(string text) {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("<summary>", StringComparison.OrdinalIgnoreCase)) {
            trimmed = trimmed["<summary>".Length..].TrimStart();
        }

        if (trimmed.EndsWith("</summary>", StringComparison.OrdinalIgnoreCase)) {
            trimmed = trimmed[..^"</summary>".Length].TrimEnd();
        }

        return trimmed;
    }

    private static string TruncateToolOutput(string text) {
        if (text.Length <= MaxToolOutputChars) {
            return text;
        }

        var truncated = text.Length - MaxToolOutputChars;
        return $"{text[..MaxToolOutputChars]}\n\n[... {truncated} characters truncated]";
    }
}