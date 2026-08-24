using System.Text;

namespace Kite.Agent;

/// <summary>
/// Demo agent: three paragraphs streamed as 4-char fragments (paragraphs joined by \n\n).
/// Used when no API key is configured, to exercise the pacer, wrapping and Esc interrupt.
/// Tokens are estimated from character counts.
/// </summary>
public sealed class FakeAgent : IAgent {
    private const int FragmentChars = 4;
    private const int DelayMs = 12;

    public string ModelName => "fake-agent";

    public string DisplayName => "fake-agent";

    public async Task<AgentReply> StreamReplyAsync(
        IReadOnlyList<ConversationMessage> conversation,
        Func<string, Task> onChunk,
        CancellationToken cancellationToken) {
        var userMessage = conversation.Count > 0 ? conversation[^1].Content : string.Empty;
        var paragraphs = new[] {
            $"你好！我是 kite 的假 Agent（FakeAgent）。你刚才说的是：“{userMessage}”。这一版我们打磨了流式节奏与折行手感。",
            "这是第二段：在窄终端下按词折行，行尾不会留下孤零零的一个词。中文长段落没有空格也能按字宽正确折行，超长的英文单词会按检测到的显示宽度切开。",
            "第三段用来演示中断：流式中按 Esc 可以立刻打断，meta 行会标记 interrupted。"
        };

        var emitted = new StringBuilder();
        try {
            foreach (var paragraph in paragraphs) {
                foreach (var fragment in SplitFragments(paragraph)) {
                    cancellationToken.ThrowIfCancellationRequested();
                    emitted.Append(fragment);
                    await onChunk(fragment);
                    await Task.Delay(DelayMs, cancellationToken);
                }

                emitted.Append("\n\n");
                await onChunk("\n\n");
            }
        } catch (OperationCanceledException) {
            // Esc interrupt: return the partial reply so it still enters the transcript
        }

        return new AgentReply(emitted.ToString(), userMessage.Length / 4, emitted.Length / 4);
    }

    private static IEnumerable<string> SplitFragments(string text) {
        var sb = new StringBuilder(FragmentChars);
        foreach (var c in text) {
            sb.Append(c);
            if (sb.Length < FragmentChars) continue;
            yield return sb.ToString();
            sb.Clear();
        }

        if (sb.Length > 0) {
            yield return sb.ToString();
        }
    }
}