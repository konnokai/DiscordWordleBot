using Discord.Interactions;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StackExchange.Redis;

namespace DiscordWordleBot.Interaction.Wordle
{
    public class WordleSession
    {
        public List<string> Guesses { get; set; } = new();
        public bool HintUsed { get; set; } = false;
    }

    [Group("wordle", "Wordle 遊戲")]
    public class Wordle : TopLevelModule<WordleService>
    {
        private const int MaxGuesses = 6;

        private readonly DiscordSocketClient _client;
        private readonly IDatabase _redis;
        private readonly Font? _font;

        private static readonly SixLabors.ImageSharp.Color Green = SixLabors.ImageSharp.Color.ParseHex("6aaa64");
        private static readonly SixLabors.ImageSharp.Color Yellow = SixLabors.ImageSharp.Color.ParseHex("c9b458");
        private static readonly SixLabors.ImageSharp.Color Gray = SixLabors.ImageSharp.Color.ParseHex("787c7e");
        private static readonly int CellSize = 24;
        private static readonly int CellPadding = 4;

        public Wordle(DiscordSocketClient client)
        {
            _client = client;
            _redis = RedisConnection.RedisDb;

            try
            {
                // 優先載入 Data/Fonts/NotoSans-Medium.ttf
                var fontPath = Program.GetDataFilePath("Fonts\\NotoSans-Medium.ttf");
                if (File.Exists(fontPath))
                {
                    var fontCollection = new FontCollection();
                    var family = fontCollection.Add(fontPath);
                    _font = family.CreateFont(14, FontStyle.Bold);
                }
                else
                {
                    // fallback: 系統字型
                    _font = SystemFonts.CreateFont(SystemFonts.Families.First().Name, 14, FontStyle.Bold);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "載入字型時發生錯誤，將不繪製圖片");
            }
        }

        private static TimeSpan GetExpireTimeSpan()
        {
            var now = DateTime.Now;
            var tomorrow = now.Date.AddDays(1);
            return tomorrow - now;
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

            if (!_service.GetAnswers().Contains(word))
            {
                await Context.Interaction.SendErrorAsync("不是合法的 Wordle 單字。");
                return;
            }

            var userId = Context.User.Id;
            var sessionJson = await _redis.StringGetAsync($"wordle:{userId}");
            var answer = _service.GetDailyAnswer();
            if (string.IsNullOrEmpty(answer))
            {
                await Context.Interaction.SendErrorAsync("今日 Wordle 答案尚未設定，請稍後再試。");
                return;
            }

            WordleSession session;
            if (sessionJson.IsNullOrEmpty)
            {
                // 自動建立新 session 並設置過期時間
                session = new WordleSession { Guesses = new List<string>(), HintUsed = false };
                await _redis.StringSetAsync($"wordle:{userId}", JsonConvert.SerializeObject(session), GetExpireTimeSpan());
                //await Context.Interaction.SendConfirmAsync($"Wordle 遊戲開始！你有 {MaxGuesses} 次機會。", false, true);
            }
            else
            {
                session = JsonConvert.DeserializeObject<WordleSession>(sessionJson!);
                // 若已猜完（答對或次數已滿）則提示今日已遊玩過
                if (session.Guesses.Contains(answer) || session.Guesses.Count >= MaxGuesses)
                {
                    await Context.Interaction.SendErrorAsync($"你今天已經玩過了！正確答案是：{answer}");
                    return;
                }
            }

            session.Guesses.Add(word);
            await _redis.StringSetAsync($"wordle:{userId}", JsonConvert.SerializeObject(session), GetExpireTimeSpan());

            var finished = word == answer || session.Guesses.Count >= MaxGuesses;
            bool isDone;
            string resultMessage;
            if (word == answer)
            {
                isDone = true;
                resultMessage = $"恭喜你答對了！正確答案是：{answer}";
            }
            else if (finished)
            {
                isDone = true;
                resultMessage = $"你已經猜了 {MaxGuesses} 次，遊戲結束！正確答案是：{answer}";
            }
            else
            {
                isDone = false;
                resultMessage = $"你還有 {MaxGuesses - session.Guesses.Count} 次機會。";
            }

            try
            {
                var imageBytes = DrawWordleImage(session.Guesses, answer);
                using var memoryStream = new MemoryStream(imageBytes);
                var embed = new EmbedBuilder()
                    .WithColor(finished ? Discord.Color.Green : Discord.Color.Orange)
                    .WithTitle("Wordle 遊戲")
                    .WithDescription(resultMessage)
                    .WithImageUrl("attachment://wordle.png")
                    .WithFooter($"已猜 {session.Guesses.Count} 次");

                await Context.Interaction.RespondWithFileAsync(memoryStream, "wordle.png", embed: embed.Build(), ephemeral: true);
            }
            catch (Exception)
            {
                // fallback: emoji grid
                string emojiGrid = BuildEmojiGrid(session.Guesses, answer);
                await Context.Interaction.SendConfirmAsync($"{resultMessage}\n\n{emojiGrid}", ephemeral: true);
            }

            if (isDone)
            {
                try
                {
                    var imageBytes2 = DrawWordleImage(session.Guesses, answer, false);
                    using var memoryStream2 = new MemoryStream(imageBytes2);

                    // 不刪除 Redis Key，讓使用者今日無法再玩

                    var embed2 = new EmbedBuilder()
                        .WithColor(finished ? Discord.Color.Green : Discord.Color.Orange)
                        .WithTitle("Wordle 遊戲")
                        .WithDescription($"{Context.User} 結束了遊戲！")
                        .WithImageUrl("attachment://wordle_nolatter.png")
                        .WithFooter($"已猜 {session.Guesses.Count} 次");

                    await Context.Interaction.FollowupWithFileAsync(memoryStream2, "wordle_nolatter.png", embed: embed2.Build());
                }
                catch (Exception)
                {
                    // fallback: emoji grid (no letters)
                    string emojiGrid = BuildEmojiGrid(session.Guesses, answer, false);
                    await Context.Interaction.FollowupAsync($"{Context.User} 結束了遊戲！\n\n{emojiGrid}");
                }
            }
        }

        // 新增：將猜測結果轉為 emoji grid
        private static string BuildEmojiGrid(List<string> guesses, string answer, bool showLetter = true)
        {
            // 🟩🟨⬜
            var result = new StringBuilder();
            foreach (var guess in guesses)
            {
                for (int i = 0; i < 5; i++)
                {
                    if (guess[i] == answer[i])
                        result.Append("🟩");
                    else if (answer.Contains(guess[i]))
                        result.Append("🟨");
                    else
                        result.Append('⬜');
                }
                if (showLetter)
                {
                    result.Append("  ");
                    for (int i = 0; i < 5; i++)
                        result.Append(guess[i].ToString().ToUpper());
                }
                result.AppendLine();
            }
            return result.ToString();
        }

        private byte[] DrawWordleImage(List<string> guesses, string answer, bool isNeedDrawLatter = true)
        {
            if (_font == null)
                throw new InvalidOperationException("字型未正確載入，無法繪製圖片。請檢查字型檔案是否存在。");

            try
            {
                int rows = guesses.Count;
                int width = 5 * CellSize + 4 * CellPadding;
                int height = rows * CellSize + (rows - 1) * CellPadding;
                using var image = new Image<Rgba32>(width, height);
                image.Mutate(ctx => ctx.Fill(Brushes.Solid(SixLabors.ImageSharp.Color.White)));
                var fontCollection = new FontCollection();
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
                            var richTextOptions = new RichTextOptions(_font)
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
            catch (Exception ex)
            {
                Log.Error(ex, "繪製 Wordle 圖片時發生錯誤");
                throw;
            }
        }

        [SlashCommand("hint", "取得提示")]
        public async Task HintAsync()
        {
            var userId = Context.User.Id;
            var sessionJson = await _redis.StringGetAsync($"wordle:{userId}");
            if (sessionJson.IsNullOrEmpty)
            {
                await Context.Interaction.SendErrorAsync("你還沒開始遊戲，請先使用 `/wordle guess`。");
                return;
            }

            var session = JsonConvert.DeserializeObject<WordleSession>(sessionJson!);
            var answer = _service.GetDailyAnswer();
            if (string.IsNullOrEmpty(answer))
            {
                await Context.Interaction.SendErrorAsync("今日 Wordle 答案尚未設定，請稍後再試。");
                return;
            }

            if (session.HintUsed)
            {
                await Context.Interaction.SendErrorAsync("本局遊戲只能使用一次提示。");
                return;
            }

            var guessedLetters = new HashSet<char>(session.Guesses.SelectMany(x => x));
            var unguessed = answer.Where(c => !guessedLetters.Contains(c)).Distinct().ToList();
            if (unguessed.Count == 0)
            {
                await Context.Interaction.SendConfirmAsync("你已經猜過所有答案中的字母了！", ephemeral: true);
                session.HintUsed = true;
                await _redis.StringSetAsync($"wordle:{userId}", JsonConvert.SerializeObject(session), GetExpireTimeSpan());
                return;
            }

            var random = new Random();
            var hintChar = unguessed[random.Next(unguessed.Count)];
            session.HintUsed = true;
            await _redis.StringSetAsync($"wordle:{userId}", JsonConvert.SerializeObject(session), GetExpireTimeSpan());
            await Context.Interaction.SendConfirmAsync($"提示：答案包含字母 {hintChar.ToString().ToUpper()} (不保證位置)", ephemeral: true);
        }
    }
}
