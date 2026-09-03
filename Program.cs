using Kite.Agent;
using Kite.App;
using Kite.Configuration;
using Kite.Ui;

var config = KiteConfig.Load();
_ = ModelCatalog.Presets; // Load and validate presets even without an API key.
var agent = AgentFactory.FromConfig(config);

using var view = new FullScreenChatView(agent?.DisplayName ?? "Not connected");
using var app = new KiteApp(agent, view, config);

return await app.RunAsync(CancellationToken.None);