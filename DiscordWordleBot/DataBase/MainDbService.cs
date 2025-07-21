namespace DiscordWordleBot.DataBase
{
    public class MainDbService
    {
        public MainDbContext GetDbContext()
        {
            return new MainDbContext();
        }
    }
}
