using Kite;
using Kite.Ui;

var config = KiteConfig.Load();
_ = ModelCatalog.Presets; // 预设目录开机即校验：数据残缺必须炸，哪怕还没有 API Key
var agent = AgentFactory.FromEnvOrConfig(config);

using var app = new KiteApp(agent, new SpectreChatView(agent?.DisplayName ?? "未连接"), config);
var code = await app.RunAsync(CancellationToken.None);

// Restore startup modes and cursor (same exit sequence as fx)
Console.Write("\e[0m\e[r\e[?25h");
return code;