using Microsoft.EntityFrameworkCore;

namespace DiscordWordleBot.DataBase
{
    public class MainDbContext : DbContext
    {
        public DbSet<GuildConfig> GuildConfig { get; set; }
        public DbSet<WordleUserSetting> WordleUserSetting { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite($"Data Source={Utility.GetDataFilePath("DataBase.db")}");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // 若有需要可於此處進行欄位預設值或索引設定
        }

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
