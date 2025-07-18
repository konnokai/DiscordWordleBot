using StackExchange.Redis;

public sealed class RedisConnection
{
    private static Lazy<RedisConnection> lazy = new Lazy<RedisConnection>(() =>
    {
        if (String.IsNullOrEmpty(_settingOption)) throw new InvalidOperationException("Please call Init() first.");
        return new RedisConnection();
    });

    private static string _settingOption;

    public readonly ConnectionMultiplexer ConnectionMultiplexer;
    public static IDatabase RedisDb { get; set; }
    public static IServer RedisServer { get; set; }

    public static RedisConnection Instance
    {
        get
        {
            return lazy.Value;
        }
    }

    private RedisConnection()
    {
        var options = ConfigurationOptions.Parse(_settingOption);
        ConnectionMultiplexer = ConnectionMultiplexer.Connect(options);

        RedisDb = ConnectionMultiplexer.GetDatabase(2);
        RedisServer = ConnectionMultiplexer.GetServer(options.EndPoints.First());
    }

    public static void Init(string settingOption)
    {
        _settingOption = settingOption;
    }
}

