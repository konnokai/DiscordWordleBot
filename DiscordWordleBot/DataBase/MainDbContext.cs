using Microsoft.EntityFrameworkCore;

namespace DiscordWordleBot.DataBase
{
    public class MainDbContext : DbContext
    {
        public DbSet<GuildConfig> GuildConfig { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite($"Data Source={Utility.GetDataFilePath("DataBase.db")}");

        public static MainDbContext GetDbContext()
        {
            var context = new MainDbContext();
            context.Database.SetCommandTimeout(60);
            var conn = context.Database.GetDbConnection();
            conn.Open();
            using (var com = conn.CreateCommand())
            {
                com.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=OFF";
                com.ExecuteNonQuery();
            }
            return context;
        }
    }
}
