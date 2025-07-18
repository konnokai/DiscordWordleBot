using Microsoft.EntityFrameworkCore;

namespace DiscordWordleBot.DataBase
{
    class SupportContext : DbContext
    {
        public DbSet<GuildConfig> GuildConfig { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite($"Data Source={Program.GetDataFilePath("DataBase.db")}");

        public static SupportContext GetDbContext()
        {
            var context = new SupportContext();
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
