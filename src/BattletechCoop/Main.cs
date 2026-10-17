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
                Sync.SyncPatches.PatchInvokes(harmony);
                Log.Info($"BattletechCoop {Assembly.GetExecutingAssembly().GetName().Version} loaded from {directory}");
                CoopController.Create(directory);
            }
            catch (Exception e)
            {
                Log.Error("Init failed", e);
            }
        }
    }

    internal static class Log
    {
        private static readonly object gate = new object();
        private static StreamWriter writer;

        // A second game process on the same install (local co-op testing) finds the log locked
        // and writes to BattletechCoop.<pid>.log instead.
        public static void Init(string path)
        {
            try
            {
                writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            }
            catch (IOException)
            {
                var alt = Path.Combine(Path.GetDirectoryName(path), $"{Path.GetFileNameWithoutExtension(path)}.{System.Diagnostics.Process.GetCurrentProcess().Id}.log");
                writer = new StreamWriter(new FileStream(alt, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            }
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception e = null) =>
            Write("ERROR", e == null ? message : message + "\n" + e);

        private static void Write(string level, string message)
        {
            lock (gate) writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
        }
    }
}
