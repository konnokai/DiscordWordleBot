using Discord.Interactions;
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

                await _redis.StringSetAsync($"wordle:{userId}", JsonConvert.SerializeObject(session));

                await Context.Interaction.SendConfirmAsync($"Wordle 遊戲開始！請輸入 `/wordle guess <五字英文>` 來猜答案。你有 {MaxGuesses} 次機會。");
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

            var allResults = session.Guesses.Select(g => GetWordleResult(g, session.Answer)).ToList();
            var resultText = string.Join("\n", allResults);
            var finished = word == session.Answer || session.Guesses.Count >= MaxGuesses;
            if (word == session.Answer)
            {
                await Context.Interaction.SendConfirmAsync($"你的猜測：{word}\n結果：{GetWordleResult(word, session.Answer)}\n\n恭喜你答對了！\n\n全部猜測：\n{resultText}");
                await _redis.KeyDeleteAsync($"wordle:{userId}");
            }
            else if (finished)
            {
                await Context.Interaction.SendErrorAsync($"你已經猜了 {MaxGuesses} 次，遊戲結束！正確答案是：{session.Answer}\n\n全部猜測：\n{resultText}");
                await _redis.KeyDeleteAsync($"wordle:{userId}");
            }
            else
            {
                await Context.Interaction.SendConfirmAsync($"你的猜測：{word}\n結果：{GetWordleResult(word, session.Answer)}\n\n全部猜測：\n{resultText}\n\n你還有 {MaxGuesses - session.Guesses.Count} 次機會。");
            }
        }

        private static string GetWordleResult(string guess, string answer)
        {
            var result = new string[5];
            for (int i = 0; i < 5; i++)
            {
                if (guess[i] == answer[i]) result[i] = "🟩";
                else if (answer.Contains(guess[i])) result[i] = "🟨";
                else result[i] = "⬜";
            }
            return string.Join("", result);
        }
    }
}
