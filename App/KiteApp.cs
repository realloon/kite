using Kite.Agent;
using Kite.Config;
using Kite.Context;
using Kite.Sessions;
using Kite.Ui;

namespace Kite.App;

internal sealed class KiteApp : IDisposable {
    private readonly FullScreenChatView _view;
    private readonly ModelConnection _models;
    private readonly SessionManager _sessions;
    private bool _disposed;

    public KiteApp(AgentClient? agent, FullScreenChatView view, ModelCatalog catalog, KiteAuth auth, KiteState state,
        SessionStore store, IReadOnlyList<Skill> skills, string workspaceContext) {
        _view = view;
        _models = new ModelConnection(view, catalog, auth, state, workspaceContext, agent);
        _sessions = new SessionManager(view, store, skills, catalog, state, _models);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken) {
        try {
            _view.ShowWelcome();
            _sessions.ShowInitialState(_models.Agent is null);

            while (!cancellationToken.IsCancellationRequested) {
                var input = await _view.ReadUserInputAsync(_sessions.CancelActiveTurn, cancellationToken);
                if (input is null) break;

                if (input.StartsWith('/')) {
                    var command = SlashCommands.Find(input);
                    if (command?.Name == "/exit") break;

                    await HandleSlashAsync(input, command, cancellationToken);
                    continue;
                }

                if (input.StartsWith('$')) {
                    _sessions.StartSkill(input, cancellationToken);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(input)) {
                    _sessions.StartTurn(input, cancellationToken);
                }
            }
        } finally {
            await _sessions.StopAsync();
        }

        return 0;
    }

    private async Task HandleSlashAsync(string input, SlashCommand? command, CancellationToken cancellationToken) {
        var spaceIndex = input.IndexOf(' ');
        var argument = spaceIndex >= 0 ? input[(spaceIndex + 1)..].Trim() : string.Empty;

        switch (command?.Name) {
            case "/connect" when EnsureNoStreaming("connection"):
                await _models.ConnectAsync(cancellationToken);
                break;
            case "/model" when EnsureNoStreaming("model"):
                await _models.ChangeModelAsync(cancellationToken);
                break;
            case "/variants" when EnsureNoStreaming("variant"):
                await _models.ChangeVariantAsync(cancellationToken);
                break;
            case "/connect" or "/model" or "/variants":
                break;
            case "/undo":
                await _sessions.UndoAsync();
                break;
            case "/compact":
                await _sessions.CompactAsync(argument, cancellationToken);
                break;
            case "/new":
                _sessions.NewSession();
                break;
            case "/sessions":
                await _sessions.SwitchAsync(cancellationToken);
                break;
            case "/stats":
                await _sessions.ShowStatsAsync(cancellationToken);
                break;
            case null:
                _view.WriteError("Unknown command.");
                break;
        }
    }

    private bool EnsureNoStreaming(string operation) {
        if (!_sessions.HasStreaming) {
            return true;
        }

        _view.WriteError($"Stop active sessions before changing the {operation}.");
        return false;
    }

    public void Dispose() {
        if (_disposed) return;

        _disposed = true;
        _sessions.StopAsync().GetAwaiter().GetResult();
        _models.Dispose();
    }
}