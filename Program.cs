using Kite;
using Kite.Ui;

var config = KiteConfig.Load();
_ = ModelCatalog.Presets; // 预设目录开机即校验：数据残缺必须炸，哪怕还没有 API Key
var agent = AgentFactory.FromEnvOrConfig(config);

using var view = new FullScreenChatView(agent?.DisplayName ?? "未连接");
using var app = new KiteApp(agent, view, config);

return await app.RunAsync(CancellationToken.None);