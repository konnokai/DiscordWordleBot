namespace DiscordWordleBot.DataBase.Table
{
    public class WordleUserSetting : DbEntity
    {
        public ulong UserId { get; set; }
        public bool NightMode { get; set; }
        public bool ColorBlindMode { get; set; }
        // �s�W�G�����q�D���
        public DateTime? FirstGuessDate { get; set; }
        // �s�W�G����
        public int Score { get; set; }
    }
}