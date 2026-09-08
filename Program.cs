using Kite.Agent;
using Kite.App;
using Kite.Configuration;
using Kite.Sessions;
using Kite.Ui;

var config = KiteConfig.Load();
var catalog = new ModelCatalog(config);
var auth = KiteAuth.Load();
var state = KiteState.Load();
var workspace = Directory.GetCurrentDirectory();
var agent = ResponsesAgent.FromState(catalog, auth, state, workspace);
var store = new SessionStore(workspace);

using var view = new FullScreenChatView(agent?.DisplayName ?? "Not connected");
using var app = new KiteApp(agent, view, catalog, auth, state, store);

return await app.RunAsync(CancellationToken.None);