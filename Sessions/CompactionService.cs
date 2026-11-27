using System.Text;
using Kite.Agent;

namespace Kite.Sessions;

internal static class CompactionService {
    private const string CompactionHeader =
        "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";

    private const string CompactionFooter = "\n</summary>";

    public const int KeepRecentTurns = 3;
    public const string DividerText = "Context compacted";

    private const string SummaryFormatAndRules = """
                                                 Follow this EXACT format:

                                                 ## Goal
                                                 [What is the user trying to accomplish? Preserve existing goals and add new ones if the task expanded.]

                                                 ## Key Details & Constraints
                                                 - [User preferences, architectural constraints, dependencies, conventions, or "(none)"]

                                                 ## Work Done
                                                 - [x] [Specific changes made, bugs fixed, files edited/created, or commands run]

                                                 ## Current State & Remaining Work
                                                 - [ ] [What is in progress or remaining to be done, current blockers or unknowns, or "(none)"]

                                                 ## Key Decisions
                                                 - **[Decision]**: [Brief rationale]

                                                 ## Relevant Files
                                                 - [file or directory path: why it matters, or "(none)"]

                                                 Rules:
                                                 - Keep every section concise. Prefer terse bullet points over long prose.
                                                 - Preserve exact file paths, symbols, function names, error messages, and URLs.
                                                 - Do not mention the summarization process itself.
                                                 - Output only the summary text; do not call any tool.
                                                 """;

    private const string InitialSummarizationPrompt = $"""
                                                       The messages above are a conversation to summarize. Create a structured context summary that another coding agent will use to continue the work.

                                                       {SummaryFormatAndRules}
                                                       """;

    private const string UpdateSummarizationPrompt = $"""
                                                      The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.

                                                      Update the existing structured summary with new information:
                                                      - Carry forward goals, decisions, constraints, and file references from <previous-summary>.
                                                      - Add new progress, decisions, and context from the new conversation.
                                                      - Move completed items from Remaining Work to Work Done.
                                                      - If something is resolved or no longer relevant, you may remove it.
                                                      - Where the new conversation conflicts with the previous summary, the newer conversation wins.

                                                      {SummaryFormatAndRules}
                                                      """;

    public static bool IsCompactionSummary(string content) =>
        content.StartsWith(CompactionHeader, StringComparison.Ordinal) &&
        content.EndsWith(CompactionFooter, StringComparison.Ordinal);

    // Cuts on user messages so tool calls are never split from their outputs.
    public static CompactionSplit? TrySplit(IReadOnlyList<ConversationMessage> messages) {
        var hasSummary = messages is [UserMessage first, ..] && IsCompactionSummary(first.Content);
        var olderFrom = hasSummary ? 1 : 0;
        var turnStarts = new List<int>();
        for (var index = olderFrom; index < messages.Count; index++) {
            if (messages[index] is UserMessage) {
                turnStarts.Add(index);
            }
        }

        if (turnStarts.Count <= KeepRecentTurns) {
            return null;
        }

        var cut = turnStarts[^KeepRecentTurns];
        return new CompactionSplit(
            [.. messages.Skip(olderFrom).Take(cut - olderFrom)],
            [.. messages.Skip(cut)],
            hasSummary ? ExtractSummary(((UserMessage)messages[0]).Content) : null);
    }

    internal sealed record CompactionSplit(
        IReadOnlyList<ConversationMessage> Older,
        IReadOnlyList<ConversationMessage> Retained,
        string? PreviousSummary);

    public static string ExtractSummary(string content) {
        if (!IsCompactionSummary(content)) {
            return content;
        }

        return content[CompactionHeader.Length..^CompactionFooter.Length].Trim();
    }

    public static string ExtractGoalTitle(string summary) {
        var inGoal = false;
        foreach (var line in summary.Split('\n')) {
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

    public static IReadOnlyList<ConversationMessage> CreateCompactedMessages(
        string summary, IReadOnlyList<ConversationMessage> retained) => [
        ConversationMessage.User($"{CompactionHeader}{summary.Trim()}{CompactionFooter}"), .. retained
    ];

    // Appended after real conversation items to keep the request prefix-cacheable.
    public static string BuildInstruction(string? previousSummary, string? focus) {
        var builder = new StringBuilder();
        var hasPrevious = !string.IsNullOrWhiteSpace(previousSummary);

        if (hasPrevious) {
            builder.Append("<previous-summary>\n")
                .Append(previousSummary)
                .Append("\n</previous-summary>\n\n");
        }

        builder.Append(hasPrevious ? UpdateSummarizationPrompt : InitialSummarizationPrompt);

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
}