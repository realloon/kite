using Kite.App;
using Kite.Config;
using Kite.Context;
using Kite.Sessions;
using Kite.Ui;

if (args.Contains("--version") || args.Contains("-v")) {
    Console.WriteLine(KiteVersion.Value);
    return 0;
}

var presets = Presets.Load();
var catalog = new ModelCatalog(presets);
var auth = KiteAuth.Load();
var state = KiteState.Load();
var workspace = Directory.GetCurrentDirectory();
var store = new SessionStore(workspace);
var skills = Skills.List(store.Workspace);
var workspaceContext = ContextBuilder.LoadWorkspace(store.Workspace, skills);
var agent = AgentFactory.FromState(catalog, auth, state, workspaceContext);

using var view = new FullScreenChatView(
    agent?.DisplayName ?? "Not connected",
    [.. skills.Select(skill => (skill.Name, skill.Description))]);
using var app = new KiteApp(agent, view, catalog, auth, state, store, skills, workspaceContext);

return await app.RunAsync(CancellationToken.None);