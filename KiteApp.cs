using System.Text.Json;
using Kite.Agent;
using Kite.Ui;

namespace Kite;

public sealed class KiteApp(IAgent? agent, IChatView view, KiteConfig? config = null) : IDisposable {
    private readonly KiteConfig _config = config ?? new KiteConfig();
    private IAgent? _agent = agent;
    private readonly List<ConversationMessage> _conversation = [];

    public async Task<int> RunAsync(CancellationToken cancellationToken) {
        if (_agent is ResponsesAgent responses) {
            responses.ExecuteToolCall ??= ExecuteToolCallAsync;
        }

        view.ShowWelcome();
        if (_agent is null) {
            view.WriteInfo("未配置 API Key —— 输入 /connect 提交 API Key");
        }

        while (!cancellationToken.IsCancellationRequested) {
            var input = await view.ReadUserInputAsync(cancellationToken);
            if (input is null or "/exit") break;

            if (input.StartsWith('/')) {
                await HandleSlashAsync(input, cancellationToken);
                continue;
            }

            if (string.IsNullOrWhiteSpace(input)) continue;

            if (_agent is null) {
                view.WriteError("未配置 API Key，请先 /connect 配置");
                continue;
            }

            _conversation.Add(ConversationMessage.User(input));
            view.AddUserMessage(input);
            view.StartAssistantTurn();

            var interrupted = false;
            string? error = null;
            var reply = AgentReply.Empty;
            try {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    view.TurnCancellationToken);
                reply = await _agent.StreamReplyAsync(
                    _conversation,
                    agentEvent => {
                        switch (agentEvent.Kind) {
                            case AgentEventKind.TextDelta:
                                view.AppendAssistantChunk(agentEvent.Text);
                                break;
                            case AgentEventKind.ReasoningSummaryDelta:
                                view.AppendReasoningChunk(agentEvent.Text);
                                break;
                            default:
                                throw new InvalidOperationException(
                                    $"未处理的 agent 事件类型：{agentEvent.Kind}");
                        }

                        return Task.CompletedTask;
                    },
                    linked.Token);
            } catch (OperationCanceledException) when (view.TurnCancellationToken.IsCancellationRequested) {
                interrupted = true;
            } catch (Exception ex) {
                error = ex.Message;
            }

            interrupted |= view.TurnCancellationToken.IsCancellationRequested;
            await view.EndAssistantTurnAsync(interrupted, reply.PromptTokens, reply.CompletionTokens);

            if (error is not null) {
                view.WriteError(error);
            } else if (reply.Text.Length > 0) {
                _conversation.Add(ConversationMessage.Assistant(reply.Text));
            }
        }

        return 0;
    }

    private async Task HandleSlashAsync(string input, CancellationToken cancellationToken) {
        switch (input) {
            case "/connect":
                await ConnectDeepSeekAsync(cancellationToken);
                break;
            case "/variants":
                await ChangeVariantAsync(cancellationToken);
                break;
            case "/new":
                _conversation.Clear();
                view.ResetTranscript();
                break;
            default:
                view.WriteError($"未知命令：{input}（可用：/connect，/variants，/new，/exit）");
                break;
        }
    }

    /// <summary>
    /// /connect: prompt for a DeepSeek API key (masked input), persist it,
    /// and hot-swap the agent.
    /// </summary>
    private async Task ConnectDeepSeekAsync(CancellationToken cancellationToken) {
        var prompt = _config.HasDeepSeekKey
            ? "更换 DeepSeek API Key（直接回车保持现有 Key）："
            : "DeepSeek API Key（输入后回车提交，Ctrl+C 取消）：";
        var key = await view.ReadSecretAsync(prompt, cancellationToken);
        if (key is null) {
            view.WriteInfo("已取消连接");
            return;
        }

        if (string.IsNullOrWhiteSpace(key)) {
            if (_config.HasDeepSeekKey && _agent is not null) {
                view.WriteInfo($"保持现有配置：DeepSeek · {_agent.DisplayName}");
                return;
            }

            view.WriteError("未输入 API Key，连接未完成");
            return;
        }

        try {
            var newAgent = AgentFactory.CreateDeepSeek(key.Trim(), config: _config);
            (_agent as IDisposable)?.Dispose();
            _agent = newAgent;
            _config.Provider = "deepseek";
            _config.ApiKey = key.Trim();
            _config.Save();
            view.SetModelName(_agent.DisplayName);
            view.WriteInfo($"已连接 DeepSeek · {_agent.DisplayName}，开始对话吧");
        } catch (Exception ex) {
            view.WriteError($"连接失败：{ex.Message}");
        }
    }

    /// <summary>
    /// /variants: pick a reasoning.effort from the model's preset list (free
    /// text for custom models), hot-swap the agent and persist it.
    /// </summary>
    private async Task ChangeVariantAsync(CancellationToken cancellationToken) {
        if (_agent is null) {
            view.WriteError("请先 /connect 配置 API Key");
            return;
        }

        var key = AgentFactory.CurrentKey(_config);
        if (string.IsNullOrEmpty(key)) {
            view.WriteError("缺少 API Key（请先 /connect）");
            return;
        }

        var variants = ModelCatalog.Find(_agent.ModelName)?.Variants;
        var prompt = variants is null
            ? $"思考强度 reasoning.effort（自定义模型，直接回车保持当前 {_agent.DisplayName}）："
            : $"思考强度 reasoning.effort（可选：{string.Join(" / ", variants)}；直接回车保持当前 {_agent.DisplayName}）：";
        var value = await view.ReadTextAsync(prompt, cancellationToken);
        if (value is null) {
            view.WriteInfo("已取消");
            return;
        }

        var effort = value.Trim().ToLowerInvariant();
        if (effort.Length == 0) {
            view.WriteInfo($"保持当前：{_agent.DisplayName}");
            return;
        }

        if (variants is not null && !variants.Contains(effort, StringComparer.OrdinalIgnoreCase)) {
            view.WriteError(
                $"无效的思考强度 '{value}'：{_agent.ModelName} 仅支持 {string.Join(" / ", variants)}");
            return;
        }

        try {
            var newAgent = AgentFactory.CreateDeepSeek(key, reasoningEffort: effort, config: _config);
            (_agent as IDisposable)?.Dispose();
            _agent = newAgent;
            _config.Variants = effort;
            _config.Save();
            view.SetModelName(_agent.DisplayName);
            view.WriteInfo($"已切换：{_agent.DisplayName}");
        } catch (Exception ex) {
            view.WriteError($"切换失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Tool executor for the run tool: no sandbox, no confirmation, no limits.
    /// </summary>
    private async Task<string> ExecuteToolCallAsync(ToolCall call, CancellationToken cancellationToken) {
        if (!string.Equals(call.Name, RunBash.DefaultName, StringComparison.Ordinal)) {
            return $"unknown tool: {call.Name}";
        }

        var command = TryReadCommand(call.Arguments) ?? call.Arguments;
        view.AppendToolLine($"{RunBash.DefaultName} {command}");

        try {
            return await RunBash.RunAsync(command, cancellationToken);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return $"error: {ex.Message}";
        }
    }

    private static string? TryReadCommand(string arguments) {
        try {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.TryGetProperty("command", out var command) &&
                command.ValueKind == JsonValueKind.String) {
                return command.GetString();
            }
        } catch (JsonException) {
            // Not JSON: pass the raw text through
        }

        return null;
    }

    public void Dispose() => (_agent as IDisposable)?.Dispose();
}
