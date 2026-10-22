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
        DarkMagicianPatches.LogInfo = s => MultiEnchantmentCoopFix.ModMain.Log.Info(s);
        DarkMagicianPatches.LogWarn = s => MultiEnchantmentCoopFix.ModMain.Log.Warn(s);
        DarkMagicianPatches.Initialize();
        OrbVfxPatches.LogWarn = s => MultiEnchantmentCoopFix.ModMain.Log.Warn(s);
        try { OrbVfxPatches.Bind(new HarmonyLib.Harmony(OrbVfxPatches.HarmonyId)); }
        catch (Exception ex) { MultiEnchantmentCoopFix.ModMain.Log.Warn($"[OrbVfxFix] Hook unavailable: {ex.Message}"); }
        HextechFormRuneCoopFix.ModMain.Initialize();
        HextechFormRuneCoopFix.RewardChoicePatches.LogInfo = s => MultiEnchantmentCoopFix.ModMain.Log.Info(s);
        HextechFormRuneCoopFix.RewardChoicePatches.LogWarn = s => MultiEnchantmentCoopFix.ModMain.Log.Warn(s);
        HextechFormRuneCoopFix.RewardChoicePatches.Initialize();
    }
}
