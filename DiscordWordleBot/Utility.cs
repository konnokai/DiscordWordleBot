#nullable enable

using System.Runtime.InteropServices;

namespace DiscordWordleBot
{
    public static class Utility
    {
        public static bool InDocker { get { return Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"; } }

        public static object? GetEnvironmentVariable(string varName, Type T, bool exitIfNoVar = false)
        {
            string? value = Environment.GetEnvironmentVariable(varName);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (exitIfNoVar)
                {
                    Log.Error($"{varName} 遺失，請輸入至環境變數後重新運行");
                    if (!Console.IsInputRedirected)
                        Console.ReadKey();
                    Environment.Exit(3);
                }
                return default;
            }
            return Convert.ChangeType(value, T);
        }

        public static string GetDataFilePath(string fileName)
            => $"{AppDomain.CurrentDomain.BaseDirectory}Data{GetPlatformSlash()}{fileName}";

        public static string GetPlatformSlash()
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "\\" : "/";
    }
}
