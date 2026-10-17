using System;
using System.Linq;
using MegaCrit.Sts2.Core.Modding;

namespace OctoFixes;

[ModInitializer(nameof(Initialize))]
public static class EntryPoint
{
    public static void Initialize()
    {
        string[] legacy = { "MultiEnchantmentCoopFix", "HextechFormRuneCoopFix" };
        string[] loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? "")
            .Where(n => legacy.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (loaded.Length != 0)
        {
            throw new InvalidOperationException("octoFixes replaces the old companion mods. " +
                "Close the game and run install.bat to migrate them: " + string.Join(", ", loaded));
        }
        MultiEnchantmentCoopFix.ModMain.Initialize();
        HextechFormRuneCoopFix.ModMain.Initialize();
    }
}
