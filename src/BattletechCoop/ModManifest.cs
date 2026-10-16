using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BattletechCoop
{
    // Fingerprint of the installed mods, compared on join: lockstep needs every player to run
    // the same mods. Covers each mod folder's mod.json and every DLL under Mods/, so a changed
    // version or a different build of a mod both count as a mismatch. ModTek's own cache and
    // logs (.modtek), per-machine state files, and the ModTek/mod.json that ModTek generates on
    // first start (its DLLs already pin its version) are excluded.
    public static class ModManifest
    {
        public static string Compute(string modsDirectory)
        {
            using (var sha = SHA256.Create())
            {
                var files = Directory.GetFiles(modsDirectory, "*", SearchOption.AllDirectories)
                    .Select(path => path.Substring(modsDirectory.Length).TrimStart('\\', '/').Replace('\\', '/'))
                    .Where(rel => !rel.StartsWith(".modtek/", StringComparison.OrdinalIgnoreCase))
                    .Where(rel => !rel.Equals("ModTek/mod.json", StringComparison.OrdinalIgnoreCase))
                    .Where(rel => rel.EndsWith("/mod.json", StringComparison.OrdinalIgnoreCase)
                                  || rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var builder = new StringBuilder();
                foreach (var rel in files)
                {
                    var hash = sha.ComputeHash(File.ReadAllBytes(Path.Combine(modsDirectory, rel)));
                    builder.Append(rel.ToLowerInvariant()).Append('=').Append(BitConverter.ToString(hash)).Append('\n');
                }
                var total = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                return BitConverter.ToString(total).Replace("-", "").Substring(0, 16) + $" ({files.Count} files)";
            }
        }
    }
}
