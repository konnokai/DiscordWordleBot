using Discord.Interactions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;
using StackExchange.Redis;
using System.Diagnostics;

namespace DiscordWordleBot.Interaction.Wordle
{
    public class WordleSession
    {
        public string Answer { get; set; } = "";
        public List<string> Guesses { get; set; } = new();
    }

    [Group("wordle", "Wordle 遊戲")]
    public class Wordle : TopLevelModule
    {
        private readonly DiscordSocketClient _client;
        private readonly List<string> _answers;
        private readonly IDatabase _redis;
        private const int MaxGuesses = 6;
        private static readonly SixLabors.ImageSharp.Color Green = SixLabors.ImageSharp.Color.ParseHex("6aaa64");
        private static readonly SixLabors.ImageSharp.Color Yellow = SixLabors.ImageSharp.Color.ParseHex("c9b458");
        private static readonly SixLabors.ImageSharp.Color Gray = SixLabors.ImageSharp.Color.ParseHex("787c7e");
        private static readonly int CellSize = 24;
        private static readonly int CellPadding = 4;

        public Wordle(DiscordSocketClient client)
        {
            _client = client;
            _answers = LoadAnswers();
            _redis = RedisConnection.RedisDb;

            if (_answers.Count == 0)
            {
                Log.Error("Wordle 答案為空，請檢查 WordleAnswers.txt 資料的正確性");
            }
        }

        private static List<string> LoadAnswers()
        {
            if (!File.Exists(Program.GetDataFilePath("WordleAnswers.txt"))) return [];

            return [.. File.ReadAllLines(Program.GetDataFilePath("WordleAnswers.txt"))
                .Select(x => x.Trim().ToLowerInvariant())
                .Where(x => x.Length == 5)
                .Distinct()];
        }

        [SlashCommand("start", "開始一局 Wordle 遊戲")]
        public async Task StartAsync()
        {
            try
            {
                var random = new Random();
                var answer = _answers[random.Next(_answers.Count)];
                var userId = Context.User.Id;
                var session = new WordleSession { Answer = answer };

                Log.Info($"{Context.Guild.Id} - {Context.User.Id} 開始 Wordle: {answer}");

                await _redis.StringSetAsync($"wordle:{userId}", JsonConvert.SerializeObject(session));

                await Context.Interaction.SendConfirmAsync($"Wordle 遊戲開始！請輸入 `/wordle guess <五字英文>` 來猜答案。你有 {MaxGuesses} 次機會。", false, true);
            }
            catch (Exception ex)
            {
                Log.Error(ex.Demystify(), $"{Context.Guild.Id} - {Context.User.Id} 開始 Wordle 失敗");
                await Context.Interaction.SendErrorAsync($"開始遊戲失敗");
            }
        }

        [SlashCommand("guess", "猜一個五字英文單字")]
        public async Task GuessAsync([Summary("word", "你的猜測")] string word)
        {
            word = word.Trim().ToLowerInvariant();
            if (word.Length != 5)
            {
                await Context.Interaction.SendErrorAsync("請輸入五字英文單字。");
                return;
            }

            if (!_answers.Contains(word))
            {
                await Context.Interaction.SendErrorAsync("不是合法的 Wordle 單字。");
                return;
            }

            var userId = Context.User.Id;
            var sessionJson = await _redis.StringGetAsync($"wordle:{userId}");
            if (sessionJson.IsNullOrEmpty)
            {
                await Context.Interaction.SendErrorAsync("你還沒開始遊戲，請先輸入 `/wordle start`。");
                return;
            }

            var session = JsonConvert.DeserializeObject<WordleSession>(sessionJson!);
            if (session.Guesses.Count >= MaxGuesses)
            {
                await Context.Interaction.SendErrorAsync($"你已經猜了 {MaxGuesses} 次，遊戲結束！正確答案是：{session.Answer}");
                return;
            }

            session.Guesses.Add(word);
            await _redis.StringSetAsync($"wordle:{userId}", JsonConvert.SerializeObject(session));

            var finished = word == session.Answer || session.Guesses.Count >= MaxGuesses;

            bool isDone;
            string resultMessage;
            if (word == session.Answer)
            {
                isDone = true;
                resultMessage = $"恭喜你答對了！正確答案是：{session.Answer}";
            }
            else if (finished)
            {
                isDone = true;
                resultMessage = $"你已經猜了 {MaxGuesses} 次，遊戲結束！正確答案是：{session.Answer}";
            }
            else
            {
                isDone = false;
                resultMessage = $"你還有 {MaxGuesses - session.Guesses.Count} 次機會。";
            }

            var imageBytes = DrawWordleImage(session.Guesses, session.Answer);
            using var memoryStream = new MemoryStream(imageBytes);

            var embed = new EmbedBuilder()
                .WithColor(finished ? Discord.Color.Green : Discord.Color.Orange)
                .WithTitle("Wordle 遊戲")
                .WithDescription(resultMessage)
                .WithImageUrl("attachment://wordle.png")
                .WithFooter($"已猜 {session.Guesses.Count} 次");

            await Context.Interaction.RespondWithFileAsync(memoryStream, "wordle.png", embed: embed.Build(), ephemeral: true);

            if (isDone)
            {
                var imageBytes2 = DrawWordleImage(session.Guesses, session.Answer, false);
                using var memoryStream2 = new MemoryStream(imageBytes2);

                await _redis.KeyDeleteAsync($"wordle:{userId}");

                var embed2 = new EmbedBuilder()
                    .WithColor(finished ? Discord.Color.Green : Discord.Color.Orange)
                    .WithTitle("Wordle 遊戲")
                    .WithDescription($"{Context.User} 結束了遊戲！")
                    .WithImageUrl("attachment://wordle_nolatter.png")
                    .WithFooter($"已猜 {session.Guesses.Count} 次");

                await Context.Interaction.FollowupWithFileAsync(memoryStream2, "wordle_nolatter.png", embed: embed2.Build());
            }
        }

        private static byte[] DrawWordleImage(List<string> guesses, string answer, bool isNeedDrawLatter = true)
        {
            int rows = guesses.Count;
            int width = 5 * CellSize + 4 * CellPadding;
            int height = rows * CellSize + (rows - 1) * CellPadding;
            using var image = new Image<Rgba32>(width, height);
            image.Mutate(ctx => ctx.Fill(Brushes.Solid(SixLabors.ImageSharp.Color.White)));
            var fontCollection = new FontCollection();
            var font = SystemFonts.CreateFont("Arial", 14, FontStyle.Bold);
            for (int row = 0; row < rows; row++)
            {
                var guess = guesses[row];
                for (int col = 0; col < 5; col++)
                {
                    int x = col * (CellSize + CellPadding);
                    int y = row * (CellSize + CellPadding);
                    var color = Gray;
                    if (guess[col] == answer[col]) color = Green;
                    else if (answer.Contains(guess[col])) color = Yellow;
                    var rect = new Rectangle(x, y, CellSize, CellSize);
                    image.Mutate(ctx => ctx.Fill(Brushes.Solid(color), rect));

                    // Draw letter
                    if (isNeedDrawLatter)
                    {
                        var letter = guess[col].ToString().ToUpperInvariant();
                        var richTextOptions = new RichTextOptions(font)
                        {
                            Origin = new PointF(x + CellSize / 2f, y + CellSize / 2f),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        image.Mutate(ctx => ctx.DrawText(richTextOptions, letter, SixLabors.ImageSharp.Color.White));
                    }
                }
            }
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }
    }
}
