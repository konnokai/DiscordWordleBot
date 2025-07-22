namespace DiscordWordleBot.DataBase.Table
{
    public class WordleUserSetting : DbEntity
    {
        public ulong UserId { get; set; }
        public bool NightMode { get; set; }
        public bool ColorBlindMode { get; set; }
        // 新增：困難模式
        public bool HardMode { get; set; }
        // 新增：首次猜題日期
        public DateTime? FirstGuessDate { get; set; }
        // 新增：得分
        public int Score { get; set; }
    }
}