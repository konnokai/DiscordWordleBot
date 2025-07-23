using Discord.Interactions;
using DiscordWordleBot.DataBase;
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
        // 新增：困難模式
        public bool HardMode { get; set; } = false;
    }

    [Group("wordle", "Wordle 遊戲")]
    public class Wordle : TopLevelModule<WordleService>
    {
        private const int MaxGuesses = 6;

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
        private static readonly SixLabors.ImageSharp.Color LightGray = SixLabors.ImageSharp.Color.ParseHex("d3d6da"); // 未使用過的鍵盤顏色
        private static readonly SixLabors.ImageSharp.Color NightLightGray = SixLabors.ImageSharp.Color.ParseHex("818384"); // 夜間未用色

        private static readonly int CellSize = 24;
        private static readonly int CellPadding = 4;

        private enum LetterState { None, Gray, Yellow, Green }

        public Wordle()
        {
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

        private bool ValidateHardMode(string guess, List<string> previousGuesses, string answer)
        {
            if (previousGuesses.Count == 0) return true;

            // 從之前的猜測中收集提示
            var lastGuess = previousGuesses[^1];
            for (int i = 0; i < 5; i++)
            {
                // 綠色提示（正確位置的字母）必須被使用
                if (lastGuess[i] == answer[i] && guess[i] != answer[i])
                    return false;
            }

            // 檢查黃色提示（正確字母但位置錯誤）
            for (int i = 0; i < 5; i++)
            {
                if (lastGuess[i] != answer[i] && answer.Contains(lastGuess[i]))
                {
                    // 確保這個字母在新的猜測中被使用
                    if (!guess.Contains(lastGuess[i]))
                        return false;
                }
            }

            return true;
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
                session = new WordleSession
                {
                    Guesses = [],
                    HintUsed = false,
                    HardMode = userSetting.HardMode
                };
            }
            else
            {
                session = JsonConvert.DeserializeObject<WordleSession>(sessionJson!);
                // 若已猜完（答對或次數已滿）則提示今日已遊玩過
                if (IsSessionFinished(session))
                {
                    await Context.Interaction.SendErrorAsync($"你今天已經玩過了！正確答案是：{answer}");
                    return;
                }
            }

            // 困難模式檢查
            if (session.HardMode)
            {
                if (!ValidateHardMode(word, session.Guesses, answer))
                {
                    await Context.Interaction.SendErrorAsync("困難模式：你必須使用之前猜測中發現的所有提示！");
                    return;
                }
            }

            // 將偏好寫入 session
            session.NightMode = userSetting.NightMode;
            session.ColorBlindMode = userSetting.ColorBlindMode;

            session.Guesses.Add(word);
            await _redis.StringSetAsync($"wordle:{userId}", JsonConvert.SerializeObject(session), GetExpireTimeSpan());

            string resultMessage;
            var finished = IsSessionFinished(session);
            if (word == answer)
            {
                resultMessage = $"恭喜你答對了！正確答案是：{answer}";
            }
            else if (finished)
            {
                resultMessage = $"你已經猜了 {MaxGuesses} 次，遊戲結束！正確答案是：{answer}";
            }
            else
            {
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
                    .WithFooter($"已猜 {session.Guesses.Count} 次" + (session.HardMode ? " | 困難模式" : ""));

                await Context.Interaction.RespondWithFileAsync(memoryStream, "wordle.png", embed: embed.Build(), ephemeral: true);
            }
            catch (Exception)
            {
                // fallback: emoji grid
                string emojiGrid = BuildEmojiGrid(session.Guesses, answer, true, session);
                await Context.Interaction.SendConfirmAsync($"{resultMessage}\n\n{emojiGrid}", ephemeral: true);
            }

            // 新增：完成作答時記錄首次猜題日期與分數
            int lastScore = 0;
            int totalScore = 0;
            if (finished)
            {
                try
                {
                    // 計算分數（倒扣制，沒猜中給 0 分）
                    int score = 0;
                    if (session.Guesses.Contains(answer)) // 確保答案在猜測列表中才進入計算階段
                    {
                        score = 6 - session.Guesses.IndexOf(answer); // 第一次猜中得 6 分，第二次 5 分...
                        if (session.HardMode) score += 2; // 困難模式額外加 2 分
                        //if (score < 0) score = 0; // 原則上分數不會小於 0，最後猜中也會有個 1 分，先拿掉等之後如果有超過 6 次答題的情況再看要怎麼改
                    }

                    using var db = MainDbContext.GetDbContext();
                    var setting = db.WordleUserSetting.FirstOrDefault(x => x.UserId == userId);
                    if (setting != null) // 邏輯上不該會有 null
                    {
                        if (!setting.FirstGuessDate.HasValue)
                            setting.FirstGuessDate = DateTime.Now;
                        if (score > 0) // 只有猜中才計分
                            setting.Score += score;
                        totalScore = setting.Score;
                        db.SaveChanges();
                    }

                    lastScore = score;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"({Context.User.Id}) 記錄 Wordle 分數時發生錯誤");
                }

                // 顯示本次得分與總分
                string scoreMsg = $"\n本次得分：{lastScore} 分" +
                                  (session.HardMode && lastScore > 0 ? "（含困難模式加 2 分）" : "") +
                                  $"\n目前總分：{totalScore} 分";

                try
                {
                    var imageBytes2 = DrawWordleImage(session.Guesses, answer, false, session, false);
                    using var memoryStream2 = new MemoryStream(imageBytes2);

                    var embed2 = new EmbedBuilder()
                        .WithColor(finished ? Discord.Color.Green : Discord.Color.Orange)
                        .WithTitle("Wordle 遊戲")
                        .WithDescription($"{Context.User.Mention} 完成了遊戲！{scoreMsg}")
                        .WithImageUrl("attachment://wordle_nolatter.png")
                        .WithFooter($"已猜 {session.Guesses.Count} 次" + (session.HardMode ? " | 困難模式" : ""));

                    await Context.Interaction.FollowupWithFileAsync(memoryStream2, "wordle_nolatter.png", embed: embed2.Build());
                }
                catch (Exception)
                {
                    // fallback: emoji grid (no letters)
                    string emojiGrid = BuildEmojiGrid(session.Guesses, answer, false, session);
                    await Context.Interaction.FollowupAsync($"{Context.User.Mention} 完成了遊戲！\n\n{emojiGrid}\n{scoreMsg}");
                }
            }
        }

        // 標記每個字母的顏色（綠、黃、灰），完全符合官方 Wordle 重複字母規則
        private static LetterState[] MarkGuess(string guess, string answer)
        {
            var result = new LetterState[5];
            var answerChars = answer.ToCharArray();
            var guessChars = guess.ToCharArray();
            var answerCharCounts = new Dictionary<char, int>();
            var greenCounts = new Dictionary<char, int>();

            // 統計答案中每個字母的數量
            foreach (var c in answerChars)
            {
                if (!answerCharCounts.ContainsKey(c)) answerCharCounts[c] = 0;
                answerCharCounts[c]++;
            }
            // 1. 先標記綠色，並記錄每個字母已配對的綠色數量
            for (int i = 0; i < 5; i++)
            {
                if (guessChars[i] == answerChars[i])
                {
                    result[i] = LetterState.Green;
                    if (!greenCounts.ContainsKey(guessChars[i])) greenCounts[guessChars[i]] = 0;
                    greenCounts[guessChars[i]]++;
                }
            }
            // 2. 再標記黃色
            var yellowCounts = new Dictionary<char, int>();
            for (int i = 0; i < 5; i++)
            {
                if (result[i] == LetterState.Green) continue;
                char c = guessChars[i];
                int totalInAnswer = answerCharCounts.ContainsKey(c) ? answerCharCounts[c] : 0;
                int greenUsed = greenCounts.ContainsKey(c) ? greenCounts[c] : 0;
                int yellowUsed = yellowCounts.ContainsKey(c) ? yellowCounts[c] : 0;
                // 黃色標記僅在答案剩餘次數內
                int canBeYellow = totalInAnswer - greenUsed - yellowUsed;
                if (canBeYellow > 0)
                {
                    result[i] = LetterState.Yellow;
                    if (!yellowCounts.ContainsKey(c)) yellowCounts[c] = 0;
                    yellowCounts[c]++;
                }
                else
                {
                    result[i] = LetterState.Gray;
                }
            }
            return result;
        }

        // 修改 BuildEmojiGrid 以使用 MarkGuess
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
                var marks = MarkGuess(guess, answer);
                for (int i = 0; i < 5; i++)
                {
                    result.Append(marks[i] switch
                    {
                        LetterState.Green => greenEmoji,
                        LetterState.Yellow => yellowEmoji,
                        _ => grayEmoji
                    });
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

        // 修改 DrawWordleImage 內格子顏色標記邏輯，使用 MarkGuess
        private byte[] DrawWordleImage(List<string> guesses, string answer, bool isNeedDrawLatter = true, WordleSession? session = null, bool showKeyboard = true)
        {
            if (_font == null)
                throw new InvalidOperationException("字型未正確載入，無法繪製圖片。請檢查字型檔案是否存在。");

            // 根據 session 設定選擇顏色
            bool night = session?.NightMode ?? false;
            bool colorBlind = session?.ColorBlindMode ?? false;

            SixLabors.ImageSharp.Color GetCellColor(LetterState state)
            {
                return state switch
                {
                    LetterState.Green => colorBlind && night ? NightColorBlindOrange : colorBlind ? ColorBlindOrange : night ? NightGreen : Green,
                    LetterState.Yellow => colorBlind && night ? NightColorBlindBlue : colorBlind ? ColorBlindBlue : night ? NightYellow : Yellow,
                    _ => colorBlind && night ? NightColorBlindGray : colorBlind ? Gray : night ? NightGray : Gray
                };
            }

            // 鍵盤字母排列
            string[] keyboardRows =
            [
                "QWERTYUIOP",
                "ASDFGHJKL",
                "ZXCVBNM"
            ];
            int keyCellW = 24, keyCellH = 32, keyCellPad = 4;
            int keyboardRowsCount = keyboardRows.Length;
            int keyboardWidth = 10 * keyCellW + 9 * keyCellPad;
            int keyboardHeight = keyboardRowsCount * keyCellH + (keyboardRowsCount - 1) * keyCellPad;
            int rows = guesses.Count;
            int gridWidth = 5 * CellSize + 4 * CellPadding;
            int gridHeight = rows * CellSize + (rows - 1) * CellPadding;
            int width = showKeyboard ? Math.Max(gridWidth, keyboardWidth) : gridWidth;
            int height = gridHeight + (showKeyboard ? (24 + keyboardHeight) : 0); // 24px padding between grid和keyboard

            try
            {
                using var image = new Image<Rgba32>(width, height);
                image.Mutate(ctx => ctx.Fill(Brushes.Solid(night ? NightBackground : SixLabors.ImageSharp.Color.White)));
                // 置中主格子
                int gridStartX = (width - gridWidth) / 2;
                for (int row = 0; row < rows; row++)
                {
                    var guess = guesses[row];
                    var marks = MarkGuess(guess, answer);
                    for (int col = 0; col < 5; col++)
                    {
                        int x = gridStartX + col * (CellSize + CellPadding);
                        int y = row * (CellSize + CellPadding);
                        var color = GetCellColor(marks[col]);
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
                // 畫鍵盤（可選）
                if (showKeyboard)
                {
                    var letterStates = GetKeyboardLetterStates(guesses, answer);
                    int kbStartY = gridHeight + 24;
                    for (int r = 0; r < keyboardRows.Length; r++)
                    {
                        string row = keyboardRows[r];
                        int rowLen = row.Length;
                        int rowStartX = (width - (rowLen * keyCellW + (rowLen - 1) * keyCellPad)) / 2;
                        for (int c = 0; c < rowLen; c++)
                        {
                            char ch = row[c];
                            LetterState state = letterStates.TryGetValue(char.ToLower(ch), out var s) ? s : LetterState.None;
                            SixLabors.ImageSharp.Color keyColor = state switch
                            {
                                LetterState.Green => colorBlind && night ? NightColorBlindOrange : colorBlind ? ColorBlindOrange : night ? NightGreen : Green,
                                LetterState.Yellow => colorBlind && night ? NightColorBlindBlue : colorBlind ? ColorBlindBlue : night ? NightYellow : Yellow,
                                LetterState.Gray => colorBlind && night ? NightColorBlindGray : colorBlind ? Gray : night ? NightGray : Gray,
                                _ => night ? NightLightGray : LightGray // None = 未用過
                            };
                            // 根據格子顏色決定字母顏色
                            SixLabors.ImageSharp.Color letterColor = state switch
                            {
                                LetterState.None => night ? SixLabors.ImageSharp.Color.White : SixLabors.ImageSharp.Color.Black,
                                LetterState.Gray => SixLabors.ImageSharp.Color.White,
                                LetterState.Green => SixLabors.ImageSharp.Color.White,
                                LetterState.Yellow => SixLabors.ImageSharp.Color.White,
                                _ => SixLabors.ImageSharp.Color.Black
                            };
                            var rect = new Rectangle(rowStartX + c * (keyCellW + keyCellPad), kbStartY + r * (keyCellH + keyCellPad), keyCellW, keyCellH);
                            image.Mutate(ctx => ctx.Fill(Brushes.Solid(keyColor), rect));
                            // Draw letter
                            var letter = ch.ToString();
                            var richTextOptions = new RichTextOptions(_font)
                            {
                                Origin = new PointF(rect.X + keyCellW / 2f, rect.Y + keyCellH / 2f),
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            image.Mutate(ctx => ctx.DrawText(richTextOptions, letter, letterColor));
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

        [SlashCommand("mode", "切換夜間模式、色盲高對比模式或困難模式")]
        public async Task ModeAsync(
            [Summary("night", "夜間模式 (true/false)")] bool? night = null,
            [Summary("colorblind", "色盲高對比模式 (true/false)")] bool? colorBlind = null,
            [Summary("hard", "困難模式 (true/false) - 已發現的提示必須在後續猜測中使用")] bool? hard = null)
        {
            var userId = Context.User.Id;
            var sessionJson = await _redis.StringGetAsync($"wordle:{userId}");
            bool sessionExists = !sessionJson.IsNullOrEmpty;
            bool hardBlocked = false;
            bool? hardToSet = hard;
            if (sessionExists && hard.HasValue)
            {
                var session = JsonConvert.DeserializeObject<WordleSession>(sessionJson!);
                bool finished = IsSessionFinished(session);
                if (!finished)
                {
                    hardBlocked = true;
                    hardToSet = null;
                }
            }
            UpdateUserSetting(userId, night, colorBlind, hardToSet);
            var setting = GetUserSetting(userId);
            string msg = $"夜間模式: {(setting.NightMode ? "開啟" : "關閉")}\n" +
                         $"色盲高對比模式: {(setting.ColorBlindMode ? "開啟" : "關閉")}\n" +
                         $"困難模式: {(setting.HardMode ? "開啟" : "關閉")}";
            if (hardBlocked)
            {
                msg += "\n你已經開始遊戲，無法變更困難模式。";
            }
            await Context.Interaction.SendConfirmAsync(msg, ephemeral: true);
        }

        [SlashCommand("share", "分享你今天的猜題結果")]
        public async Task ShareAsync()
        {
            var userId = Context.User.Id;
            var sessionJson = await _redis.StringGetAsync($"wordle:{userId}");
            var answer = _service.GetDailyAnswer();
            if (string.IsNullOrEmpty(answer))
            {
                await Context.Interaction.SendErrorAsync("今日 Wordle 答案尚未設定，請稍後再試。");
                return;
            }
            if (sessionJson.IsNullOrEmpty)
            {
                await Context.Interaction.SendErrorAsync("你今天還沒玩過 Wordle，無法分享結果。");
                return;
            }
            var session = JsonConvert.DeserializeObject<WordleSession>(sessionJson!);
            // 僅允許已猜完（答對或次數已滿）才可分享
            if (!IsSessionFinished(session))
            {
                await Context.Interaction.SendErrorAsync("你今天還沒完成遊戲，無法分享結果。");
                return;
            }
            try
            {
                var imageBytes = DrawWordleImage(session.Guesses, answer, false, session, false);
                using var memoryStream = new MemoryStream(imageBytes);
                var embed = new EmbedBuilder()
                    .WithColor(Discord.Color.Blue)
                    .WithTitle($"{Context.User.Username} 的 Wordle 結果")
                    .WithDescription($"{session.Guesses.Count} / {MaxGuesses} 次完成今日 Wordle！" + (session.HardMode ? " (困難模式)" : ""))
                    .WithImageUrl("attachment://wordle_share.png")
                    .WithFooter("Wordle 分享");
                await Context.Interaction.RespondWithFileAsync(memoryStream, "wordle_share.png", embed: embed.Build(), ephemeral: false);
            }
            catch (Exception)
            {
                // fallback: emoji grid (no letters)
                string emojiGrid = BuildEmojiGrid(session.Guesses, answer, false, session);
                await Context.Interaction.SendConfirmAsync($"{Context.User.Username} 的 Wordle 結果\n\n{emojiGrid}", ephemeral: false);
            }
        }

        [SlashCommand("view", "偷看其他玩家的猜題過程 (你必須完成今日遊戲)")]
        public async Task ViewAsync([Summary("user", "要偷看的對象")] IUser user)
        {
            var myId = Context.User.Id;
            var targetId = user.Id;

            var answer = _service.GetDailyAnswer();
            if (string.IsNullOrEmpty(answer))
            {
                await Context.Interaction.SendErrorAsync("今日 Wordle 答案尚未設定，請稍後再試。");
                return;
            }

            var mySessionJson = await _redis.StringGetAsync($"wordle:{myId}");
            if (mySessionJson.IsNullOrEmpty)
            {
                await Context.Interaction.SendErrorAsync("你必須完成今日遊戲才可偷看他人。");
                return;
            }

            var mySession = JsonConvert.DeserializeObject<WordleSession>(mySessionJson!);
            // 如果是偷看自己，允許任何時候偷看
            if (myId == targetId)
            {
                try
                {
                    var imageBytes = DrawWordleImage(mySession.Guesses, answer, true, mySession);
                    using var memoryStream = new MemoryStream(imageBytes);
                    var embed = new EmbedBuilder()
                        .WithColor(Discord.Color.Purple)
                        .WithTitle($"{user.Username} 的猜題過程")
                        .WithImageUrl("attachment://wordle_view.png")
                        .WithFooter($"已猜 {mySession.Guesses.Count} 次" + (mySession.HardMode ? " | 困難模式" : ""));
                    await Context.Interaction.RespondWithFileAsync(memoryStream, "wordle_view.png", embed: embed.Build(), ephemeral: true);
                }
                catch (Exception)
                {
                    string emojiGrid = BuildEmojiGrid(mySession.Guesses, answer, true, mySession);
                    await Context.Interaction.SendConfirmAsync($"{user.Username} 的猜題過程\n\n{emojiGrid}", ephemeral: true);
                }
            }
            else
            {
                // 偷看他人時，必須完成今日遊戲
                bool myDone = IsSessionFinished(mySession);
                if (!myDone)
                {
                    await Context.Interaction.SendErrorAsync("你必須完成今日遊戲才可偷看他人。");
                    return;
                }

                var targetSessionJson = await _redis.StringGetAsync($"wordle:{targetId}");
                if (targetSessionJson.IsNullOrEmpty)
                {
                    await Context.Interaction.SendErrorAsync("對方今天還沒玩過 Wordle。");
                    return;
                }

                var targetSession = JsonConvert.DeserializeObject<WordleSession>(targetSessionJson!);

                try
                {
                    var imageBytes = DrawWordleImage(targetSession.Guesses, answer, true, targetSession);
                    using var memoryStream = new MemoryStream(imageBytes);
                    var embed = new EmbedBuilder()
                        .WithColor(Discord.Color.Purple)
                        .WithTitle($"{user.Username} 的猜題過程")
                        .WithImageUrl("attachment://wordle_view.png")
                        .WithFooter($"已猜 {targetSession.Guesses.Count} 次" + (targetSession.HardMode ? " | 困難模式" : ""));
                    await Context.Interaction.RespondWithFileAsync(memoryStream, "wordle_view.png", embed: embed.Build(), ephemeral: true);
                }
                catch (Exception)
                {
                    string emojiGrid = BuildEmojiGrid(targetSession.Guesses, answer, true, targetSession);
                    await Context.Interaction.SendConfirmAsync($"{user.Username} 的猜題過程\n\n{emojiGrid}", ephemeral: true);
                }
            }
        }

        [SlashCommand("score", "查詢你的 Wordle 總分")]
        public async Task ScoreAsync()
        {
            var userId = Context.User.Id;

            using var db = MainDbContext.GetDbContext();
            var setting = db.WordleUserSetting.FirstOrDefault(x => x.UserId == userId);
            if (setting == null || setting.FirstGuessDate == null)
            {
                await Context.Interaction.SendErrorAsync("你尚未開始過 Wordle 遊戲。");
                return;
            }

            int score = setting?.Score ?? 0;
            string msg = $"{Context.User.Mention} 從 {setting.FirstGuessDate?.ToString("yyyy/MM/dd") ?? "未知"} 到現在的 Wordle 總分: {score} 分";

            await Context.Interaction.SendConfirmAsync(msg, ephemeral: true);
        }

        // 判斷本日遊戲是否已完成（猜中或次數已滿）
        private bool IsSessionFinished(WordleSession session)
        {
            var answer = _service.GetDailyAnswer();
            return session.Guesses.Contains(answer) || session.Guesses.Count >= MaxGuesses;
        }

        // 取得使用者的 Wordle 模式設定
        private static WordleUserSetting GetUserSetting(ulong userId)
        {
            try
            {
                using var db = MainDbContext.GetDbContext();
                var setting = db.WordleUserSetting.FirstOrDefault(x => x.UserId == userId);
                if (setting == null)
                {
                    setting = new WordleUserSetting { UserId = userId, NightMode = false, ColorBlindMode = false, HardMode = false };
                    db.WordleUserSetting.Add(setting);
                    db.SaveChanges();
                }
                return setting;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Get user setting error: {userId}");
                throw;
            }
        }

        // 更新使用者的 Wordle 模式設定
        private static void UpdateUserSetting(ulong userId, bool? night, bool? colorBlind, bool? hard)
        {
            using var db = MainDbContext.GetDbContext();
            var setting = db.WordleUserSetting.FirstOrDefault(x => x.UserId == userId);
            if (setting == null)
            {
                setting = new WordleUserSetting
                {
                    UserId = userId,
                    NightMode = night ?? false,
                    ColorBlindMode = colorBlind ?? false,
                    HardMode = hard ?? false
                };
                db.WordleUserSetting.Add(setting);
            }
            else
            {
                if (night.HasValue) setting.NightMode = night.Value;
                if (colorBlind.HasValue) setting.ColorBlindMode = colorBlind.Value;
                if (hard.HasValue) setting.HardMode = hard.Value;
            }
            db.SaveChanges();
        }

        // 取得每個字母的最高狀態（綠>黃>灰）
        private static Dictionary<char, LetterState> GetKeyboardLetterStates(List<string> guesses, string answer)
        {
            var dict = new Dictionary<char, LetterState>();
            foreach (var guess in guesses)
            {
                var marks = MarkGuess(guess, answer);
                for (int i = 0; i < guess.Length; i++)
                {
                    char c = guess[i];
                    if (!dict.TryGetValue(c, out var state)) state = LetterState.None;
                    if (marks[i] == LetterState.Green)
                        dict[c] = LetterState.Green;
                    else if (marks[i] == LetterState.Yellow)
                    {
                        if (state != LetterState.Green)
                            dict[c] = LetterState.Yellow;
                    }
                    else if (marks[i] == LetterState.Gray)
                    {
                        if (state != LetterState.Green && state != LetterState.Yellow)
                            dict[c] = LetterState.Gray;
                    }
                }
            }
            return dict;
        }
    }
}
