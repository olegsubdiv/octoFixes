using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace MultiEnchantmentCoopFix;

/// <summary>
/// SavedProperty *names* are never sent over the wire — only their index in
/// <c>ModelIdSerializationCache._netIdToPropertyNameMap</c> is. That table is filled in registration order:
/// names a mod registers during its initializer (MultiEnchantmentMod and EndlessMode both do this via
/// <c>CacheSavedPropertiesForTypeDebug</c>) come first, in *mod load order*, then everything the game finds in
/// <c>ModelDb</c> at <c>ModelIdSerializationCache.Init()</c>, then whatever BaseLib appends afterwards.
///
/// The game's handshake hash covers only the names it discovers itself inside <c>Init()</c>, so two peers whose
/// mods load in a different order pass the handshake and still disagree on which index means which name.
/// That is exactly what the divergence dump showed: host table = [EndlessMode×5, MultiEnchantment×7, base...],
/// client table = [MultiEnchantment×7, EndlessMode×5, base...] → the client's "MultiEnchantmentOrderData" (index 5
/// on the client) arrived on the host labelled "MultiEnchantmentCardInstanceId" (index 5 on the host).
///
/// This patch, right after <c>Init()</c>:
///  (a) canonicalises the table — sorts it ordinally and rebuilds the name→index map — so the mapping becomes a
///      pure function of the *set* of names and no longer depends on load order (<c>canonicalPropertyTable</c>);
///  (b) dumps the table to the log so two logs can be diffed;
///  (c) folds the table (names in index order + bit width) into the handshake hash so a peer with a different
///      *set* of names is refused up front instead of desyncing mid-run (<c>strictPropertyTableCheck</c>).
/// Nothing has serialised anything yet at that point (Init runs during startup), so re-indexing is safe.
/// </summary>
[HarmonyPatch]
internal static class PropertyTablePatches
{
    private const string NamesField = "_netIdToPropertyNameMap";
    private const string MapField = "_propertyNameToNetIdMap";

    [HarmonyPatch(typeof(ModelIdSerializationCache), nameof(ModelIdSerializationCache.Init))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void InitPostfix()
    {
        ApplyPropertyTable();
        ContentPoolPatches.Apply();
    }

    private static void ApplyPropertyTable()
    {
        try
        {
            List<string>? names = AccessTools.StaticFieldRefAccess<List<string>>(typeof(ModelIdSerializationCache), NamesField);
            Dictionary<string, int>? map = AccessTools.StaticFieldRefAccess<Dictionary<string, int>>(typeof(ModelIdSerializationCache), MapField);
            if (names == null || map == null)
            {
                ModMain.Log.Warn("[MECoopFix] Could not read the SavedProperty net-ID table; skipping canonicalisation/dump/strict check.");
                return;
            }

            if (ModMain.CanonicalPropertyTable)
            {
                int moved = CanonicalizeTable(names, map);
                ModMain.Log.Info($"[MECoopFix] SavedProperty net-ID table canonicalised (sorted ordinally); {moved} of {names.Count} names changed index. " +
                                 "Index→name mapping no longer depends on mod load order.");
            }

            ModMain.Log.Info($"[MECoopFix] SavedProperty net-ID table after Init ({names.Count} names, {ModelIdSerializationCache.PropertyIdBitSize} bits): {FormatTable(names)}");

            if (!ModMain.StrictPropertyTableCheck)
            {
                return;
            }

            uint original = ModelIdSerializationCache.Hash;
            uint folded = FoldTable(original, names, ModelIdSerializationCache.PropertyIdBitSize);
            MethodInfo? setter = AccessTools.PropertySetter(typeof(ModelIdSerializationCache), nameof(ModelIdSerializationCache.Hash));
            if (setter == null)
            {
                ModMain.Log.Warn("[MECoopFix] ModelIdSerializationCache.Hash has no setter; strict property-table check disabled.");
                return;
            }
            setter.Invoke(null, new object[] { folded });
            ModMain.Log.Info($"[MECoopFix] Handshake ID hash now also covers the SavedProperty net-ID table: {original} -> {folded}. " +
                             "If a peer is refused with a Model-ID-hash mismatch, diff the table lines above between the two logs.");
        }
        catch (Exception ex)
        {
            ModMain.Log.Warn($"[MECoopFix] ModelIdSerializationCache.Init postfix failed: {ex}");
        }
    }

    /// <summary>
    /// Sorts <paramref name="names"/> in place (ordinal) and rebuilds <paramref name="map"/> to match.
    /// Returns how many names changed index. Pure — exposed for tests.
    /// </summary>
    internal static int CanonicalizeTable(List<string> names, Dictionary<string, int> map)
    {
        List<string> before = new List<string>(names);
        names.Sort(StringComparer.Ordinal);
        map.Clear();
        int moved = 0;
        for (int i = 0; i < names.Count; i++)
        {
            map[names[i]] = i;
            if (!string.Equals(before[i], names[i], StringComparison.Ordinal))
            {
                moved++;
            }
        }
        return moved;
    }

    internal static string FormatTable(IReadOnlyList<string> names)
    {
        StringBuilder sb = new StringBuilder(names.Count * 24);
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(i).Append('=').Append(names[i]);
        }
        return sb.ToString();
    }

    /// <summary>FNV-1a over the original hash, the bit width and every name in index order (with separators).</summary>
    internal static uint FoldTable(uint original, IReadOnlyList<string> names, int bitSize)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint h = offset;
        void Mix(byte b) { h ^= b; h *= prime; }
        void MixUInt(uint v) { Mix((byte)v); Mix((byte)(v >> 8)); Mix((byte)(v >> 16)); Mix((byte)(v >> 24)); }

        MixUInt(original);
        MixUInt((uint)bitSize);
        MixUInt((uint)names.Count);
        foreach (string name in names)
        {
            foreach (byte b in Encoding.UTF8.GetBytes(name)) Mix(b);
            Mix(0);
        }
        return h;
    }
}
