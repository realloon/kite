using Kite.Agent;
using Kite.App;
using Kite.Configuration;
using Kite.Context;
using Kite.Sessions;
using Kite.Ui;

var config = UserPresets.Load();
var catalog = new ModelCatalog(config);
var auth = KiteAuth.Load();
var state = KiteState.Load();
var workspace = Directory.GetCurrentDirectory();
var store = new SessionStore(workspace);
var skills = Skills.List(store.Workspace);
var workspaceContext = ContextBuilder.LoadWorkspace(store.Workspace, skills);
var agent = AgentFactory.FromState(catalog, auth, state, workspaceContext);

using var view = new FullScreenChatView(agent?.DisplayName ?? "Not connected", skills);
using var app = new KiteApp(agent, view, catalog, auth, state, store, skills, workspaceContext);

return await app.RunAsync(CancellationToken.None);