using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace MultiEnchantmentCoopFix;

/// <summary>
/// Companion mod for MultiEnchantmentMod (Snowline). Fixes co-op state divergence caused by
/// MultiEnchantmentMod's per-card metadata leaking into network-synchronised / checksummed card state.
///
/// What it does:
///  1. Strips the redundant "MultiEnchantmentOrderData" card property whenever it is trivially derivable
///     (card carries exactly one enchantment, no extra enchantments) so that vanilla-enchanted cards
///     serialise byte-for-byte like vanilla on every peer. See <see cref="SerializationPatches"/>.
///     Also strips orphaned order metadata when neither primary nor additional enchantments remain.
///  3. Folds a digest of every card/relic/potion pool (sorted member IDs + CanBeGeneratedInCombat) into the
///     handshake hash, so peers whose pools differ (e.g. per-machine mod config toggles such as
///     TheHeroExpansion.cfg) are refused at connect time. See <see cref="ContentPoolPatches"/>.
///  2. Canonicalises (sorts) the SavedProperty net-ID table right after the game builds it, so the index→name
///     mapping no longer depends on mod load order; logs the table; folds it into the multiplayer handshake hash
///     so a peer whose table still differs is refused at connect time instead of desyncing mid-run.
///     See <see cref="PropertyTablePatches"/>.
/// </summary>
public static class ModMain
{
    public const string HarmonyId = "octoFixes.MultiEnchantment";
    public const string MultiEnchantmentHarmonyId = "MultiEnchantmentMod";

    public static Logger Log { get; } = new Logger("octoFixes", LogType.Generic);

    public static bool VerboseLog { get; private set; }
    public static bool StripRedundantOrderData { get; private set; } = true;
    public static bool StrictPropertyTableCheck { get; private set; } = true;
    public static bool CanonicalPropertyTable { get; private set; } = true;
    public static bool StrictContentPoolCheck { get; private set; } = true;

    public static void Initialize()
    {
        VerboseLog = ReadManifestFlag("verboseLog", false);
        StripRedundantOrderData = ReadManifestFlag("stripRedundantOrderData", true);
        StrictPropertyTableCheck = ReadManifestFlag("strictPropertyTableCheck", true);
        CanonicalPropertyTable = ReadManifestFlag("canonicalPropertyTable", true);
        StrictContentPoolCheck = ReadManifestFlag("strictContentPoolCheck", true);

        Harmony harmony = new Harmony(HarmonyId);
        // Only this module's patches; Hextech installs its optional hooks separately.
        harmony.CreateClassProcessor(typeof(SerializationPatches)).Patch();
        harmony.CreateClassProcessor(typeof(PropertyTablePatches)).Patch();

        Log.Info($"[MECoopFix] Initialised v{Assembly.GetExecutingAssembly().GetName().Version} " +
                 $"(stripRedundantOrderData={StripRedundantOrderData}, canonicalPropertyTable={CanonicalPropertyTable}, " +
                 $"strictPropertyTableCheck={StrictPropertyTableCheck}, strictContentPoolCheck={StrictContentPoolCheck}, verboseLog={VerboseLog}).");
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
            Log.Warn($"[MECoopFix] Failed to read manifest flag '{key}', using default {defaultValue}: {ex.Message}");
            return defaultValue;
        }
    }
}
