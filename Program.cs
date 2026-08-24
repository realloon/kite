using Kite;
using Kite.Ui;

var config = KiteConfig.Load();
var agent = AgentFactory.FromEnvOrConfig(config);

using var app = new KiteApp(agent, new SpectreChatView(agent.DisplayName), config);
var code = await app.RunAsync(CancellationToken.None);

// Restore startup modes and cursor (same exit sequence as fx)
Console.Write("\e[0m\e[r\e[?25h");
return code;