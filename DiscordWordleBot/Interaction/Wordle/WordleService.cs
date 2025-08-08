using StackExchange.Redis;
using System.Diagnostics;

namespace DiscordWordleBot.Interaction.Wordle
{
    public class WordleService : IInteractionService
    {
        private static Timer dailyWordleTimer;
        private static readonly object dailyLock = new();
        private static string _dailyAnswer = null;
        private readonly IDatabase _redis;
        private readonly List<string> _answers;
        private readonly HashSet<string> _validWords;

        public WordleService()
        {
            _redis = RedisConnection.RedisDb;

            _answers = LoadAnswers();
            _validWords = LoadValidWords();
            if (_answers.Count == 0)
            {
                Log.Error("Wordle 答案為空，請檢查 WordleAnswers.txt 資料的正確性");
            }

            InitDailyWordleTimer();
        }

        public List<string> GetAnswers()
        {
            return _answers;
        }

        public bool IsValidWord(string word)
        {
            return _validWords.Contains(word);
        }

        private static TimeSpan GetExpireTimeSpan()
        {
            var now = DateTime.Now;
            var tomorrow = now.Date.AddDays(1);
            return tomorrow - now;
        }

        private List<string> LoadAnswers()
        {
            try
            {
                if (!File.Exists(DiscordWordleBot.Utility.GetDataFilePath("WordleAnswers.txt")))
                    return [];

                return [.. File.ReadAllLines(DiscordWordleBot.Utility.GetDataFilePath("WordleAnswers.txt"))
                .Select(x => x.Trim().ToLowerInvariant())
                .Where(x => x.Length == 5)
                .Distinct()];
            }
            catch (Exception ex)
            {
                Log.Error(ex.Demystify(), "[Wordle] Load answers failed");
                return [];
            }
        }

        private void InitDailyWordleTimer()
        {
            SetDailyWordleAnswer();

            dailyWordleTimer = new Timer(_ =>
            {
                SetDailyWordleAnswer();
            }, null, GetExpireTimeSpan() + TimeSpan.FromSeconds(3), TimeSpan.FromDays(1));
        }

        private void SetDailyWordleAnswer()
        {
            try
            {
                if (_answers.Count == 0) return;

                if (_redis.KeyExists("wordle:daily"))
                {
                    Log.Info("[Wordle] Daily answer already set, skipping.");
                    return;
                }

                var random = new Random();
                var answer = _answers[random.Next(_answers.Count)];
                lock (dailyLock)
                {
                    _dailyAnswer = answer;
                }

                // 到半夜 12 點自動過期重刷
                _redis.StringSet("wordle:daily", answer, GetExpireTimeSpan());

                Log.Info($"[Wordle] Daily answer set: {answer}");
            }
            catch (Exception ex)
            {
                Log.Error(ex.Demystify(), "[Wordle] Set daily answer failed");
            }
        }

        public string GetDailyAnswer()
        {
            lock (dailyLock)
            {
                if (!string.IsNullOrEmpty(_dailyAnswer))
                    return _dailyAnswer;
            }

            var answer = _redis.StringGet("wordle:daily");
            if (!answer.IsNullOrEmpty)
            {
                lock (dailyLock)
                {
                    _dailyAnswer = answer;
                }
                return answer;
            }

            return null;
        }

        private HashSet<string> LoadValidWords()
        {
            try
            {
                var path = DiscordWordleBot.Utility.GetDataFilePath("ValidWordleWords.txt");
                if (!File.Exists(path))
                    return [];
                return [.. File.ReadAllLines(path)
                    .Select(x => x.Trim().ToLowerInvariant())
                    .Where(x => x.Length == 5)
                    .Distinct()];
            }
            catch (Exception ex)
            {
                Log.Error(ex.Demystify(), "[Wordle] Load valid words failed");
                return [];
            }
        }
    }
}
