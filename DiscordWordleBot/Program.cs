using Discord.Interactions;
using DiscordWordleBot.DataBase;
using DiscordWordleBot.HttpClients;
using DiscordWordleBot.Interaction;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using System.Diagnostics;
using System.Reflection;

namespace DiscordWordleBot
{
    class Program
    {
        public enum UpdateStatusFlags { Guild, Member, Info }

        public static string VERSION => GetLinkerTime(Assembly.GetEntryAssembly());

        public static ConnectionMultiplexer Redis { get; set; }
        public static ISubscriber RedisSub { get; set; }
        public static IDatabase RedisDb { get; set; }
        public static MainDbService DbService { get; private set; }

        public static Stopwatch StopWatch { get; private set; } = new Stopwatch();
        public static DiscordSocketClient Client { get => client; }
        public static IUser ApplicatonOwner { get; private set; } = null;
        public static UpdateStatusFlags UpdateStatus { get; set; } = UpdateStatusFlags.Guild;
        public static bool IsDisconnect { get; internal set; } = false;
        public static bool IsConnect { get; private set; } = false;

        private static Timer timerUpdateStatus;
        private static DiscordSocketClient client;
        private static readonly BotConfig _botConfig = new();

        static void Main(string[] args)
        {
            StopWatch.Start();

            Log.Info(VERSION + " 初始化中");
            Console.OutputEncoding = Encoding.UTF8;
            Console.CancelKeyPress += Console_CancelKeyPress;

            _botConfig.InitBotConfig();
            DbService = new MainDbService();

            timerUpdateStatus = new Timer(TimerHandler);

            if (!Directory.Exists(Path.GetDirectoryName(Utility.GetDataFilePath(""))))
                Directory.CreateDirectory(Path.GetDirectoryName(Utility.GetDataFilePath("")));

            if (!File.Exists(Utility.GetDataFilePath("DataBase.db")))
            {
                using var db = DbService.GetDbContext();
                db.Database.EnsureCreated();
            }

            try
            {
                RedisConnection.Init(_botConfig.RedisOption);
                Redis = RedisConnection.Instance.ConnectionMultiplexer;
                RedisSub = Redis.GetSubscriber();
                RedisDb = Redis.GetDatabase();

                Log.Info("Redis已連線");
            }
            catch (Exception ex)
            {
                Log.Error("Redis連線錯誤，請確認伺服器是否已開啟");
                Log.Error(ex.Message);
                return;
            }

            StartAndBlockAsync().ConfigureAwait(false).GetAwaiter().GetResult();
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

        public static async Task StartAndBlockAsync()
        {
            client = new DiscordSocketClient(new DiscordSocketConfig()
            {
                LogLevel = LogSeverity.Verbose,
                ConnectionTimeout = int.MaxValue,
                MessageCacheSize = 0,
                // 因為沒有註冊事件，Discord .NET 建議可移除這兩個沒用到的特權
                // https://dotblogs.com.tw/yc421206/2015/10/20/c_scharp_enum_of_flags
                GatewayIntents = GatewayIntents.AllUnprivileged & ~GatewayIntents.GuildInvites & ~GatewayIntents.GuildScheduledEvents,
                AlwaysDownloadDefaultStickers = false,
                AlwaysResolveStickers = false,
                FormatUsersInBidirectionalUnicode = false,
                LogGatewayIntentWarnings = false,
            });

            #region 初始化Discord設定與事件
            client.Log += Log.LogMsg;

            client.Ready += async () =>
            {
                Log.Info($"已透過 {Client.CurrentUser.Username} 身分登入");

                StopWatch.Start();
                timerUpdateStatus.Change(0, 15 * 60 * 1000);

                ApplicatonOwner = (await client.GetApplicationInfoAsync()).Owner;
                IsConnect = true;
            };

            client.LeftGuild += (guild) =>
            {
                Log.Info($"離開伺服器: {guild.Name}");
                return Task.CompletedTask;
            };
            #endregion

#if DEBUG || RELEASE
            Log.Info("登入中...");

            try
            {
                await client.LoginAsync(TokenType.Bot, _botConfig.DiscordToken);
                await client.StartAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex.Demystify(), "Discord 登入失敗!");
                return;
            }

            do { await Task.Delay(200); }
            while (!IsConnect);

            Log.Info("登入成功!");

            UptimeKumaClient.Init(_botConfig.UptimeKumaPushUrl, client);
#endif

            #region 初始化互動指令系統
            var interactionServices = new ServiceCollection()
                //.AddHttpClient()
                .AddSingleton(Client)
                .AddSingleton(_botConfig)
                .AddSingleton(new InteractionService(Client, new InteractionServiceConfig()
                {
                    AutoServiceScopes = true,
                    UseCompiledLambda = false,
                    EnableAutocompleteHandlers = false,
                    DefaultRunMode = RunMode.Async
                }));

            interactionServices.LoadInteractionFrom(Assembly.GetAssembly(typeof(InteractionHandler)));
            IServiceProvider serviceProvider = interactionServices.BuildServiceProvider();
            await serviceProvider.GetService<InteractionHandler>().InitializeAsync();
            #endregion

            #region 註冊互動指令
            try
            {
                int commandCount = 0;
                try
                {
                    if (File.Exists(Utility.GetDataFilePath("CommandCount.bin")))
                        commandCount = BitConverter.ToInt32(File.ReadAllBytes(Utility.GetDataFilePath("CommandCount.bin")));

                    File.WriteAllBytes(Utility.GetDataFilePath("CommandCount.bin"), BitConverter.GetBytes(serviceProvider.GetService<InteractionHandler>().CommandCount));
                }
                catch (Exception ex)
                {
                    Log.Error(ex.Demystify(), "設定指令數量失敗，請確認檔案是否正常");

                    if (File.Exists(Utility.GetDataFilePath("CommandCount.bin")))
                        File.Delete(Utility.GetDataFilePath("CommandCount.bin"));

                    IsDisconnect = true;
                    return;
                }

                if (commandCount != serviceProvider.GetService<InteractionHandler>().CommandCount)
                {
                    InteractionService interactionService = serviceProvider.GetService<InteractionService>();
#if DEBUG
                    if (_botConfig.TestSlashCommandGuildId == 0 || client.GetGuild(_botConfig.TestSlashCommandGuildId) == null)
                        Log.Warn("未設定測試Slash指令的伺服器或伺服器不存在，略過");
                    else
                    {
                        try
                        {
                            var result = await interactionService.RegisterCommandsToGuildAsync(_botConfig.TestSlashCommandGuildId);
                            Log.Info($"已註冊指令 ({_botConfig.TestSlashCommandGuildId}) : {string.Join(", ", result.Select((x) => x.Name))}");

                            result = await interactionService.AddModulesToGuildAsync(_botConfig.TestSlashCommandGuildId, false, interactionService.Modules.Where((x) => x.DontAutoRegister).ToArray());
                            Log.Info($"已註冊指令 ({_botConfig.TestSlashCommandGuildId}) : {string.Join(", ", result.Select((x) => x.Name))}");
                        }
                        catch (Exception ex)
                        {
                            Log.Error("註冊伺服器專用Slash指令失敗");
                            Log.Error(ex.ToString());
                        }
                    }
#elif RELEASE
                    try
                    {
                        if (_botConfig.TestSlashCommandGuildId != 0 && client.GetGuild(_botConfig.TestSlashCommandGuildId) != null)
                        {
                            var result = await interactionService.RemoveModulesFromGuildAsync(_botConfig.TestSlashCommandGuildId, interactionService.Modules.Where((x) => !x.DontAutoRegister).ToArray());
                            Log.Info($"({_botConfig.TestSlashCommandGuildId}) 已移除測試指令，剩餘指令: {string.Join(", ", result.Select((x) => x.Name))}");
                        }
                        try
                        {
                            foreach (var item in interactionService.Modules.Where((x) => x.Preconditions.Any((x) => x is Interaction.Attribute.RequireGuildAttribute)))
                            {
                                var guildId = ((Interaction.Attribute.RequireGuildAttribute)item.Preconditions.Single((x) => x is Interaction.Attribute.RequireGuildAttribute)).GuildId;
                                var guild = client.GetGuild(guildId.Value);

                                if (guild == null)
                                {
                                    Log.Warn($"{item.Name} 註冊失敗，伺服器 {guildId} 不存在");
                                    continue;
                                }

                                var result = await interactionService.AddModulesToGuildAsync(guild, false, item);
                                Log.Info($"已在 {guild.Name}({guild.Id}) 註冊指令: {string.Join(", ", item.SlashCommands.Select((x) => x.Name))}");
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
                    catch (Exception ex)
                    {
                        Log.Error("取得指令數量失敗，請確認Redis伺服器是否可以存取");
                        Log.Error(ex.Message);
                        IsDisconnect = true;
                    }
#endif
                }
            }
            catch (Exception ex)
            {
                Log.Error("註冊Slash指令失敗，關閉中...");
                Log.Error(ex.ToString());
                IsDisconnect = true;
            }
            #endregion

            // 因為會用到 DiscordWebhookClient Service，所以沒辦法往上移動到 Region 內
            client.JoinedGuild += (guild) =>
            {
                Log.Info($"加入伺服器: {guild.Name}");

                var hasInvitePermission = guild.GetUser(client.CurrentUser.Id)?.GuildPermissions.CreateInstantInvite ?? false;
                if (!hasInvitePermission)
                {
                    serviceProvider.GetService<DiscordWebhookClient>().SendMessageToDiscord($"加入 {guild.Name} ({guild.Id})\n" +
                        $"擁有者: {guild.OwnerId}\n" +
                        $"未開放邀請權限，已離開");
                    guild.LeaveAsync().GetAwaiter().GetResult();
                    return Task.CompletedTask;
                }

                serviceProvider.GetService<DiscordWebhookClient>().SendMessageToDiscord($"加入 {guild.Name}({guild.Id})\n" +
                    $"擁有者: {guild.OwnerId}");

                using (var db = DbService.GetDbContext())
                {
                    if (!db.GuildConfig.Any(x => x.GuildId == guild.Id))
                    {
                        db.GuildConfig.Add(new GuildConfig() { GuildId = guild.Id });
                        db.SaveChanges();
                    }
                }

                return Task.CompletedTask;
            };

            Log.Info("已初始化完成!");

            do { await Task.Delay(1000); }
            while (!IsDisconnect);

            await client.StopAsync();
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
