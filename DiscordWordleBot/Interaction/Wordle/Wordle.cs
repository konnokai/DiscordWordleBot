using Discord.Interactions;
using DiscordWordleBot.DataBase;
using DiscordWordleBot.DataBase.Table;
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
        // 新增：夜間模式與色盲模式
        public bool NightMode { get; set; } = false;
        public bool ColorBlindMode { get; set; } = false;
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
        // 夜間模式顏色
        private static readonly SixLabors.ImageSharp.Color NightGreen = SixLabors.ImageSharp.Color.ParseHex("538d4e");
        private static readonly SixLabors.ImageSharp.Color NightYellow = SixLabors.ImageSharp.Color.ParseHex("b59f3b");
        private static readonly SixLabors.ImageSharp.Color NightGray = SixLabors.ImageSharp.Color.ParseHex("3a3a3c");
        private static readonly SixLabors.ImageSharp.Color NightBackground = SixLabors.ImageSharp.Color.ParseHex("121213");
        // 色盲模式顏色
        private static readonly SixLabors.ImageSharp.Color ColorBlindOrange = SixLabors.ImageSharp.Color.ParseHex("f5793a");
        private static readonly SixLabors.ImageSharp.Color ColorBlindBlue = SixLabors.ImageSharp.Color.ParseHex("85c0f9");
        // 夜間+色盲
        private static readonly SixLabors.ImageSharp.Color NightColorBlindOrange = SixLabors.ImageSharp.Color.ParseHex("f5793a");
        private static readonly SixLabors.ImageSharp.Color NightColorBlindBlue = SixLabors.ImageSharp.Color.ParseHex("85c0f9");
        private static readonly SixLabors.ImageSharp.Color NightColorBlindGray = NightGray;

        private static readonly int CellSize = 24;
        private static readonly int CellPadding = 4;

        public Wordle(DiscordSocketClient client)
        {
            _client = client;
            _redis = RedisConnection.RedisDb;

            try
            {
                // 優先載入 Data/Fonts/NotoSans-Medium.ttf
                var fontPath = DiscordWordleBot.Utility.GetDataFilePath($"Fonts{DiscordWordleBot.Utility.GetPlatformSlash()}NotoSans-Medium.ttf");
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

            // 取得使用者偏好
            var userSetting = GetUserSetting(userId);

            WordleSession session;
            if (sessionJson.IsNullOrEmpty)
            {
                // 自動建立新 session 並設置過期時間
                session = new WordleSession { Guesses = [], HintUsed = false };
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
            // 將偏好寫入 session
            session.NightMode = userSetting.NightMode;
            session.ColorBlindMode = userSetting.ColorBlindMode;

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
                var imageBytes = DrawWordleImage(session.Guesses, answer, true, session);
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
                string emojiGrid = BuildEmojiGrid(session.Guesses, answer, true, session);
                await Context.Interaction.SendConfirmAsync($"{resultMessage}\n\n{emojiGrid}", ephemeral: true);
            }

            if (isDone)
            {
                try
                {
                    var imageBytes2 = DrawWordleImage(session.Guesses, answer, false, session);
                    using var memoryStream2 = new MemoryStream(imageBytes2);

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
                    string emojiGrid = BuildEmojiGrid(session.Guesses, answer, false, session);
                    await Context.Interaction.FollowupAsync($"{Context.User} 結束了遊戲！\n\n{emojiGrid}");
                }
            }
        }

        // 新增：將猜測結果轉為 emoji grid
        private static string BuildEmojiGrid(List<string> guesses, string answer, bool showLetter = true, WordleSession? session = null)
        {
            // 根據 session 設定選擇 emoji
            bool colorBlind = session?.ColorBlindMode ?? false;
            string greenEmoji = colorBlind ? "🟧" : "🟩"; // 橙色
            string yellowEmoji = colorBlind ? "🟦" : "🟨"; // 藍色
            string grayEmoji = "⬜";

            var result = new StringBuilder();
            foreach (var guess in guesses)
            {
                for (int i = 0; i < 5; i++)
                {
                    if (guess[i] == answer[i])
                        result.Append(greenEmoji);
                    else if (answer.Contains(guess[i]))
                        result.Append(yellowEmoji);
                    else
                        result.Append(grayEmoji);
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

        private byte[] DrawWordleImage(List<string> guesses, string answer, bool isNeedDrawLatter = true, WordleSession? session = null)
        {
            if (_font == null)
                throw new InvalidOperationException("字型未正確載入，無法繪製圖片。請檢查字型檔案是否存在。");

            // 根據 session 設定選擇顏色
            bool night = session?.NightMode ?? false;
            bool colorBlind = session?.ColorBlindMode ?? false;

            SixLabors.ImageSharp.Color GetCellColor(char guessChar, char answerChar, string answerStr, int col)
            {
                if (colorBlind && night)
                {
                    if (guessChar == answerChar) return NightColorBlindOrange;
                    else if (answerStr.Contains(guessChar)) return NightColorBlindBlue;
                    else return NightColorBlindGray;
                }
                else if (colorBlind)
                {
                    if (guessChar == answerChar) return ColorBlindOrange;
                    else if (answerStr.Contains(guessChar)) return ColorBlindBlue;
                    else return Gray;
                }
                else if (night)
                {
                    if (guessChar == answerChar) return NightGreen;
                    else if (answerStr.Contains(guessChar)) return NightYellow;
                    else return NightGray;
                }
                else
                {
                    if (guessChar == answerChar) return Green;
                    else if (answerStr.Contains(guessChar)) return Yellow;
                    else return Gray;
                }
            }

            try
            {
                int rows = guesses.Count;
                int width = 5 * CellSize + 4 * CellPadding;
                int height = rows * CellSize + (rows - 1) * CellPadding;
                using var image = new Image<Rgba32>(width, height);
                image.Mutate(ctx => ctx.Fill(Brushes.Solid(night ? NightBackground : SixLabors.ImageSharp.Color.White)));
                var fontCollection = new FontCollection();
                for (int row = 0; row < rows; row++)
                {
                    var guess = guesses[row];
                    for (int col = 0; col < 5; col++)
                    {
                        int x = col * (CellSize + CellPadding);
                        int y = row * (CellSize + CellPadding);
                        var color = GetCellColor(guess[col], answer[col], answer, col);
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

        [SlashCommand("mode", "切換夜間模式或色盲高對比模式")]
        public async Task ModeAsync(
            [Summary("night", "夜間模式 (true/false)")] bool? night = null,
            [Summary("colorblind", "色盲高對比模式 (true/false)")] bool? colorBlind = null)
        {
            var userId = Context.User.Id;
            UpdateUserSetting(userId, night, colorBlind);
            var setting = GetUserSetting(userId);
            await Context.Interaction.SendConfirmAsync($"已設定：夜間模式 {(setting.NightMode ? "開啟" : "關閉")}, 色盲高對比模式 {(setting.ColorBlindMode ? "開啟" : "關閉")}", ephemeral: true);
        }

        // 取得使用者的 Wordle 模式設定
        private static WordleUserSetting GetUserSetting(ulong userId)
        {
            using var db = MainDbContext.GetDbContext();
            var setting = db.WordleUserSetting.FirstOrDefault(x => x.UserId == userId);
            if (setting == null)
            {
                setting = new WordleUserSetting { UserId = userId, NightMode = false, ColorBlindMode = false };
                db.WordleUserSetting.Add(setting);
                db.SaveChanges();
            }
            return setting;
        }

        // 更新使用者的 Wordle 模式設定
        private static void UpdateUserSetting(ulong userId, bool? night, bool? colorBlind)
        {
            using var db = MainDbContext.GetDbContext();
            var setting = db.WordleUserSetting.FirstOrDefault(x => x.UserId == userId);
            if (setting == null)
            {
                setting = new WordleUserSetting { UserId = userId, NightMode = night ?? false, ColorBlindMode = colorBlind ?? false };
                db.WordleUserSetting.Add(setting);
            }
            else
            {
                if (night.HasValue) setting.NightMode = night.Value;
                if (colorBlind.HasValue) setting.ColorBlindMode = colorBlind.Value;
            }
            db.SaveChanges();
        }
    }
}
