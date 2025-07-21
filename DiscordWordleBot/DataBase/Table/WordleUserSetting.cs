namespace DiscordWordleBot.DataBase.Table
{
    public class WordleUserSetting : DbEntity
    {
        public ulong UserId { get; set; }
        public bool NightMode { get; set; }
        public bool ColorBlindMode { get; set; }
    }
}