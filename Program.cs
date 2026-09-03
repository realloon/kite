using Kite.Agent;
using Kite.App;
using Kite.Configuration;
using Kite.Sessions;
using Kite.Ui;

var config = KiteConfig.Load();
_ = ModelCatalog.Presets;
var agent = AgentFactory.FromConfig(config);
var store = new SessionStore(Directory.GetCurrentDirectory());

using var view = new FullScreenChatView(agent?.DisplayName ?? "Not connected");
using var app = new KiteApp(agent, view, config, store);

return await app.RunAsync(CancellationToken.None);