using System;
using System.IO;
using System.Reflection;
using Harmony;
using Newtonsoft.Json;

namespace BattletechCoop
{
    public class Settings
    {
        public bool LogInvocations = true;
    }

    // Entry point for ModTek and the HBS mod loader (see mod.json DLLEntryPoint).
    public static class Main
    {
        public static Settings Settings = new Settings();
        public static string ModDirectory;

        public static void Init(string directory, string settingsJson)
        {
            ModDirectory = directory;
            Log.Init(Path.Combine(directory, "BattletechCoop.log"));
            try
            {
                if (!string.IsNullOrEmpty(settingsJson))
                    Settings = JsonConvert.DeserializeObject<Settings>(settingsJson) ?? new Settings();

                var harmony = HarmonyInstance.Create("battletech.coop");
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.Info($"BattletechCoop {Assembly.GetExecutingAssembly().GetName().Version} loaded from {directory}");
            }
            catch (Exception e)
            {
                Log.Error("Init failed", e);
            }
        }
    }

    internal static class Log
    {
        private static StreamWriter writer;

        public static void Init(string path)
        {
            writer = new StreamWriter(path, append: false) { AutoFlush = true };
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception e = null) =>
            Write("ERROR", e == null ? message : message + "\n" + e);

        private static void Write(string level, string message) =>
            writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
    }
}
