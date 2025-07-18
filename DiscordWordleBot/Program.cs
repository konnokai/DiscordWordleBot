using Discord.Interactions;
using DiscordWordleBot.Interaction;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DiscordWordleBot
{
    class Program
    {
        public enum UpdateStatusFlags { Guild, Member, Info }

        public static string VERSION => GetLinkerTime(Assembly.GetEntryAssembly());
        public static IUser ApplicatonOwner { get; private set; } = null;
        public static Stopwatch StopWatch { get; private set; } = new Stopwatch();
        public static DiscordSocketClient Client { get; set; }
        public static UpdateStatusFlags UpdateStatus { get; set; } = UpdateStatusFlags.Guild;
        public static ConnectionMultiplexer RedisConnection { get; private set; }
        public static bool IsDisconnect { get; internal set; } = false;
        public static bool IsConnect { get; private set; } = false;

        private static Timer timerUpdateStatus;
        private static readonly BotConfig _botConfig = new();

        static void Main(string[] args)
        {
            StopWatch.Start();

            Log.Info(VERSION + " 初始化中");
            Console.OutputEncoding = Encoding.UTF8;
            Console.CancelKeyPress += Console_CancelKeyPress;

            _botConfig.InitBotConfig();

            timerUpdateStatus = new Timer(TimerHandler);

            if (!Directory.Exists(Path.GetDirectoryName(GetDataFilePath(""))))
                Directory.CreateDirectory(Path.GetDirectoryName(GetDataFilePath("")));

            using (var db = new SupportContext())
            {
                if (!File.Exists(GetDataFilePath("DataBase.db")))
                {
                    db.Database.EnsureCreated();
                }
            }

            try
            {
                global::RedisConnection.Init(_botConfig.RedisOption);
                RedisConnection = global::RedisConnection.Instance.ConnectionMultiplexer;
                Log.Info("Redis已連線");
            }
            catch (Exception ex)
            {
                Log.Error("Redis連線錯誤，請確認伺服器是否已開啟");
                Log.Error(ex.Message);
                return;
            }

            MainAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private static void TimerHandler(object state)
        {
            if (IsDisconnect) return;

            ChangeStatus();
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            IsDisconnect = true;
            e.Cancel = true;
        }

        static async Task MainAsync()
        {
            Client = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Warning,
                ConnectionTimeout = int.MaxValue,
                MessageCacheSize = 50,
                GatewayIntents = GatewayIntents.All & ~GatewayIntents.GuildInvites & ~GatewayIntents.GuildScheduledEvents,
                AlwaysDownloadDefaultStickers = false,
                AlwaysResolveStickers = false,
                FormatUsersInBidirectionalUnicode = false,
                LogGatewayIntentWarnings = false,
            });

            #region 初始化互動指令系統
            var interactionServices = new ServiceCollection()
                //.AddHttpClient()
                .AddSingleton(Client)
                .AddSingleton(_botConfig)
                .AddSingleton(new InteractionService(Client, new InteractionServiceConfig()
                {
                    AutoServiceScopes = true,
                    UseCompiledLambda = true,
                    EnableAutocompleteHandlers = true,
                    DefaultRunMode = RunMode.Async
                }));

            interactionServices.LoadInteractionFrom(Assembly.GetAssembly(typeof(InteractionHandler)));
            IServiceProvider iService = interactionServices.BuildServiceProvider();
            await iService.GetService<InteractionHandler>().InitializeAsync();
            #endregion

            Client.JoinedGuild += async (guild) =>
            {
                await SendMessageToDiscord($"加入 {guild.Name}({guild.Id})\n擁有者: {guild.OwnerId}");
            };

            Client.Ready += async () =>
            {
                Log.Info($"已透過 {Client.CurrentUser.Username} 身分登入");

                timerUpdateStatus.Change(0, 20 * 60 * 1000);

                UptimeKumaClient.Init(_botConfig.UptimeKumaPushUrl, Client);

                ApplicatonOwner = (await Client.GetApplicationInfoAsync().ConfigureAwait(false)).Owner;

                IsConnect = true;

                try
                {
                    InteractionService interactionService = iService.GetService<InteractionService>();

#if DEBUG
                    if (_botConfig.TestSlashCommandGuildId == 0 || Client.GetGuild(_botConfig.TestSlashCommandGuildId) == null)
                        Log.Warn("未設定測試Slash指令的伺服器或伺服器不存在，略過");
                    else
                    {
                        try
                        {
                            var result = await interactionService.AddModulesToGuildAsync(_botConfig.TestSlashCommandGuildId, true, interactionService.Modules.Where((x) => x.DontAutoRegister).ToArray());
                            Log.Info($"已註冊指令 ({_botConfig.TestSlashCommandGuildId}) : {string.Join(", ", result.Select((x) => x.Name))}");

                            result = await interactionService.RegisterCommandsToGuildAsync(_botConfig.TestSlashCommandGuildId);
                            Log.Info($"已註冊指令 ({_botConfig.TestSlashCommandGuildId}) : {string.Join(", ", result.Select((x) => x.Name))}");
                        }
                        catch (Exception ex)
                        {
                            Log.Error("註冊伺服器專用Slash指令失敗");
                            Log.Error(ex.ToString());
                        }
                    }
                }
#else
                    int commandCount = 0;
                    try
                    {

                        if (File.Exists(GetDataFilePath("CommandCount.bin")))
                            commandCount = BitConverter.ToInt32(File.ReadAllBytes(GetDataFilePath("CommandCount.bin")));

                        File.WriteAllBytes(GetDataFilePath("CommandCount.bin"), BitConverter.GetBytes(iService.GetService<InteractionHandler>().CommandCount));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("設定指令數量失敗，請確認檔案是否正常");
                        Log.Error(ex.Message);
                        if (File.Exists(GetDataFilePath("CommandCount.bin")))
                            File.Delete(GetDataFilePath("CommandCount.bin"));

                        IsDisconnect = true;
                        return;
                    }

                    if (commandCount != iService.GetService<InteractionHandler>().CommandCount)
                    {
                        try
                        {
                            foreach (var item in interactionService.Modules.Where((x) => x.Preconditions.Any((x) => x is Interaction.Attribute.RequireGuildAttribute)))
                            {
                                var guildId = ((Interaction.Attribute.RequireGuildAttribute)item.Preconditions.FirstOrDefault((x) => x is Interaction.Attribute.RequireGuildAttribute)).GuildId;
                                var guild = Client.GetGuild(guildId.Value);

                                if (guild == null)
                                {
                                    Log.Warn($"{item.Name} 註冊失敗，伺服器 {guildId} 不存在");
                                    continue;
                                }

                                var result = await interactionService.AddModulesToGuildAsync(guild, true, item);
                                Log.Info($"已在 {guild.Name}({guild.Id}) 註冊指令: {string.Join(", ", result.Select((x) => x.Name))}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error("註冊伺服器專用Slash指令失敗");
                            Log.Error(ex.ToString());
                        }

                        await interactionService.RegisterCommandsGloballyAsync();
                        Log.Info("已註冊全球指令");
                    }
                }
#endif
                catch (Exception ex)
                {
                    Log.Error("註冊Slash指令失敗，關閉中...");
                    Log.Error(ex.ToString());
                    IsDisconnect = true;
                }

                Log.FormatColorWrite("準備完成", ConsoleColor.Green);
            };

            #region Login
            await Client.LoginAsync(TokenType.Bot, _botConfig.DiscordToken);
            #endregion

            await Client.StartAsync();

            do { await Task.Delay(1000); }
            while (!IsDisconnect);

            await Client.StopAsync();

            return;
        }

        public static void ChangeStatus()
        {
            Task.Run(async () =>
            {
                switch (UpdateStatus)
                {
                    case UpdateStatusFlags.Guild:
                        await Client.SetCustomStatusAsync($"在 {Client.Guilds.Count} 個伺服器");
                        UpdateStatus = UpdateStatusFlags.Member;
                        break;
                    case UpdateStatusFlags.Member:
                        try
                        {
                            await Client.SetCustomStatusAsync($"服務 {Client.Guilds.Sum((x) => x.MemberCount)} 個成員");
                            UpdateStatus = UpdateStatusFlags.Info;
                        }
                        catch (Exception) { UpdateStatus = UpdateStatusFlags.Info; ChangeStatus(); }
                        break;
                    case UpdateStatusFlags.Info:
                        await Client.SetCustomStatusAsync("去打你的程式啦");
                        UpdateStatus = UpdateStatusFlags.Guild;
                        break;
                }
            });
        }

        public static string GetDataFilePath(string fileName)
        {
            return AppDomain.CurrentDomain.BaseDirectory + "Data" +
                (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "\\" : "/") + fileName;
        }

        public static async Task SendMessageToDiscord(string content)
        {
            Message message = new();

            if (IsConnect) message.username = Client.CurrentUser.Username;
            else message.username = "Bot";

            if (IsConnect) message.avatar_url = Client.CurrentUser.GetAvatarUrl();
            else message.avatar_url = "";

            message.content = content;

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Content-Type", "application/json");

            var json = JsonConvert.SerializeObject(message);
            var contentObj = new StringContent(json, Encoding.UTF8, "application/json");
            await httpClient.PostAsync(_botConfig.WebHookUrl, contentObj);
        }
        public static string GetLinkerTime(Assembly assembly)
        {
            const string BuildVersionMetadataPrefix = "+build";

            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attribute?.InformationalVersion != null)
            {
                var value = attribute.InformationalVersion;
                var index = value.IndexOf(BuildVersionMetadataPrefix);
                if (index > 0)
                {
                    value = value[(index + BuildVersionMetadataPrefix.Length)..];
                    return value;
                }
            }
            return default;
        }
    }
    public class Message
    {
        public string username { get; set; }
        public string content { get; set; }
        public string avatar_url { get; set; }
    }

}
