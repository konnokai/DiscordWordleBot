namespace DiscordWordleBot.DataBase.Table
{
    public class GuildConfig : DbEntity
    {
        public ulong GuildId { get; set; }
        public ulong AutoVoiceChannel { get; set; } = 0;
        public ulong ChannelMemberId { get; set; } = 0;
        public ulong ChannelNitroId { get; set; } = 0;
    }
}
