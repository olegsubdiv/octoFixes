using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace HextechFormRuneCoopFix;

/// <summary>
/// Companion mod for HextechRunes (Natsuki). Fixes co-op state divergence ("After player turn start" checksum)
/// caused by the Form upgrade runes (Reaper/Demon/Echo/Serpent/Void Form) auto-playing their Form cards at the
/// start of combat on one peer only.
///
/// What it does:
///  1. Neutralises HextechRunes' own <c>AutoPlayFormsAtCombatStartRuneBase&lt;TCard&gt;.AfterPlayerTurnStartLate</c>
///     (Harmony prefix returning a completed task), bound lazily once the HextechRunes assembly is loaded.
///  2. Re-implements the effect deterministically: right after <c>CombatManager.SetupPlayerTurn</c> of the player's
///     turn 1 (runs on every peer for every player), once per (combat, player), moves the owner's Form cards from
///     Draw/Hand/Discard to Play and auto-plays them. See <see cref="FormRunePatches"/>.
///  3. NeutralizeUpgradeRune: plays the REAL discarded Neutralize/Suppress as one replay series instead of clones.
///     See <see cref="NeutralizeRunePatches"/>.
/// Without HextechRunes the mod does nothing.
/// </summary>
public static class ModMain
{
    public const string HarmonyId = "octoFixes.Hextech";

    public static Logger Log { get; } = new Logger("octoFixes", LogType.Generic);

    public static void Initialize()
    {
        FormRunePatches.VerboseLog = ReadManifestFlag("verboseLog", false);
        FormRunePatches.DisableAutoPlayFix = ReadManifestFlag("disableAutoPlayFix", false);
        FormRunePatches.LogInfo = s => Log.Info(s);
        FormRunePatches.LogWarn = s => Log.Warn(s);
        FormRunePatches.LogError = s => Log.Error(s);

        NeutralizeRunePatches.Enabled = ReadManifestFlag("neutralizeRuneRealCard", true);

        Log.Info($"[FormRuneFix] Initialised v{Assembly.GetExecutingAssembly().GetName().Version} " +
                 $"(disableAutoPlayFix={FormRunePatches.DisableAutoPlayFix}, neutralizeRuneRealCard={NeutralizeRunePatches.Enabled}, " +
                 $"verboseLog={FormRunePatches.VerboseLog}).");
        if (FormRunePatches.DisableAutoPlayFix)
        {
            Log.Info("[FormRuneFix] disableAutoPlayFix=true -> Form runes behave as shipped by HextechRunes.");
        }
        if (!NeutralizeRunePatches.Enabled)
        {
            Log.Info("[NeutralizeRuneFix] neutralizeRuneRealCard=false -> NeutralizeUpgradeRune behaves as shipped (clones).");
        }
        if (FormRunePatches.DisableAutoPlayFix && !NeutralizeRunePatches.Enabled)
        {
            return;
        }

        Harmony harmony = new Harmony(HarmonyId);
        FormRunePatches.Harmony = harmony;
        bool anyActive = false;
        if (!FormRunePatches.DisableAutoPlayFix)
        {
            anyActive |= PatchClass(harmony, typeof(FormRunePatches.SetupPlayerTurnPatch), "[FormRuneFix] CombatManager.SetupPlayerTurn");
        }
        if (NeutralizeRunePatches.Enabled)
        {
            bool a = PatchClass(harmony, typeof(NeutralizeRunePatches.ModifyCardPlayCountPatch), "[NeutralizeRuneFix] Hook.ModifyCardPlayCount");
            bool b = PatchClass(harmony, typeof(NeutralizeRunePatches.BeforeCardPlayedPatch), "[NeutralizeRuneFix] Hook.BeforeCardPlayed");
            if (!(a && b))
            {
                // Without both game hooks the replacement would play the real card only once: keep Hextech's clones.
                NeutralizeRunePatches.Enabled = false;
                harmony.UnpatchAll(HarmonyId);
                anyActive = false;
                if (!FormRunePatches.DisableAutoPlayFix)
                {
                    anyActive = PatchClass(harmony, typeof(FormRunePatches.SetupPlayerTurnPatch), "[FormRuneFix] CombatManager.SetupPlayerTurn");
                }
                Log.Error("[NeutralizeRuneFix] game hooks could not be patched -> feature disabled, NeutralizeUpgradeRune behaves as shipped.");
            }
            else
            {
                anyActive = true;
            }
        }
        if (!anyActive)
        {
            Log.Error("[FormRuneFix] no patch could be applied, mod inactive.");
            return;
        }

        AppDomain.CurrentDomain.AssemblyLoad += FormRunePatches.OnAssemblyLoad;
        FormRunePatches.EnsureBound("init");
    }

    private static bool PatchClass(Harmony harmony, Type patchClass, string what)
    {
        try
        {
            harmony.CreateClassProcessor(patchClass).Patch();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"{what}: patch failed: {ex}");
            return false;
        }
    }

    private static bool ReadManifestFlag(string key, bool defaultValue)
    {
        try
        {
            string dir = Path.GetDirectoryName(typeof(ModMain).Assembly.Location) ?? ".";
            string path = Path.Combine(dir, "octoFixes.json");
            if (!File.Exists(path))
            {
                return defaultValue;
            }
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty(key, out JsonElement value))
            {
                return defaultValue;
            }
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => defaultValue,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"[FormRuneFix] Failed to read manifest flag '{key}', using default {defaultValue}: {ex.Message}");
            return defaultValue;
        }
    }
}
