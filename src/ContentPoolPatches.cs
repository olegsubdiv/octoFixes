using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace MultiEnchantmentCoopFix;

/// <summary>
/// Second co-op footgun, found 2026-09-19: the game's handshake hash covers the *set of model IDs* of every
/// gameplay-affecting mod, but not *which of them actually ended up in a pool*. BaseLib's
/// <c>CustomCardModel(..., autoAdd)</c> lets a mod register a card in <c>ModelDb</c> but skip
/// <c>ModHelper.AddModelToPool</c>, and TheHeroExpansion drives <c>autoAdd</c> from a per-machine config
/// (<c>%APPDATA%\SlayTheSpire2\mod_configs\TheHeroExpansion.cfg</c>, one toggle per card). Host had
/// <c>Repurpose=False</c>/<c>Groundwork=False</c>, the client had the defaults: same ID set, same handshake
/// hash, but the client's Silent pool held one more Skill. Every pool-based roll — Skill/Attack Potion,
/// Discovery, card rewards, shops — shuffles the *whole* pool (Fisher–Yates, one RNG call per element),
/// so the client burned one RNG call more and both the offered cards and the RNG counter diverged
/// (<c>CombatCardGeneration: 187 vs 188</c>).
///
/// This patch, right after <c>ModelIdSerializationCache.Init()</c> (all mod initialisers have run by then,
/// and the game's own Init has already enumerated <c>ModelDb</c>): builds a canonical digest of every card,
/// relic and potion pool — sorted member IDs plus each card's <c>CanBeGeneratedInCombat</c> flag — logs the
/// per-pool counts/hashes and the modded members, and folds the digest into the handshake hash
/// (<c>strictContentPoolCheck</c>). A peer whose pools differ is then refused at connect time with the game's
/// own Model-ID-hash mismatch, instead of being kicked mid-run.
/// </summary>
internal static class ContentPoolPatches
{
    internal sealed record PoolDigest(string Pool, IReadOnlyList<string> Members);

    /// <summary>Called from <see cref="PropertyTablePatches.InitPostfix"/> so that the two folds happen in a fixed order.</summary>
    internal static void Apply()
    {
        try
        {
            List<PoolDigest> pools = CollectPools();
            foreach (PoolDigest pool in pools)
            {
                uint poolHash = FoldPools(0u, new[] { pool });
                string modded = string.Join(", ", pool.Members.Where(IsModded));
                ModMain.Log.Info($"[MECoopFix] Pool {pool.Pool}: {pool.Members.Count} members, hash {poolHash:X8}" +
                                 (modded.Length > 0 ? $"; modded: {modded}" : string.Empty));
                if (ModMain.VerboseLog)
                {
                    ModMain.Log.Info($"[MECoopFix] Pool {pool.Pool} members: {string.Join(", ", pool.Members)}");
                }
            }

            if (!ModMain.StrictContentPoolCheck)
            {
                return;
            }

            uint original = ModelIdSerializationCache.Hash;
            uint folded = FoldPools(original, pools);
            MethodInfo? setter = AccessTools.PropertySetter(typeof(ModelIdSerializationCache), nameof(ModelIdSerializationCache.Hash));
            if (setter == null)
            {
                ModMain.Log.Warn("[MECoopFix] ModelIdSerializationCache.Hash has no setter; strict content-pool check disabled.");
                return;
            }
            setter.Invoke(null, new object[] { folded });
            ModMain.Log.Info($"[MECoopFix] Handshake ID hash now also covers card/relic/potion pool composition: {original} -> {folded}. " +
                             "If a peer is refused with a Model-ID-hash mismatch, diff the 'Pool ...' lines above between the two logs " +
                             "(a per-machine mod config such as TheHeroExpansion.cfg is the usual culprit).");
        }
        catch (Exception ex)
        {
            ModMain.Log.Warn($"[MECoopFix] Content-pool digest failed: {ex}");
        }
    }

    /// <summary>Canonical digest of every card, relic and potion pool the game knows about.</summary>
    internal static List<PoolDigest> CollectPools()
    {
        List<PoolDigest> result = new List<PoolDigest>();
        foreach (CardPoolModel pool in ModelDb.AllCardPools)
        {
            result.Add(new PoolDigest(Name(pool), Sorted(pool.AllCards.Select(c => Member(c.Id, c.CanBeGeneratedInCombat)))));
        }
        foreach (RelicPoolModel pool in ModelDb.AllRelicPools)
        {
            result.Add(new PoolDigest(Name(pool), Sorted(pool.AllRelics.Select(r => Member(r.Id, true)))));
        }
        foreach (PotionPoolModel pool in ModelDb.AllPotionPools)
        {
            result.Add(new PoolDigest(Name(pool), Sorted(pool.AllPotions.Select(p => Member(p.Id, true)))));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Pool, b.Pool));
        return result;
    }

    private static string Name(AbstractModel pool) => pool.Id.Category + "." + pool.Id.Entry;

    /// <summary>Member token: "CATEGORY.ENTRY", with "!nogen" appended for cards that are excluded from in-combat generation.</summary>
    internal static string Member(ModelId id, bool canBeGeneratedInCombat) =>
        id.Category + "." + id.Entry + (canBeGeneratedInCombat ? string.Empty : "!nogen");

    private static IReadOnlyList<string> Sorted(IEnumerable<string> members)
    {
        List<string> list = members.Distinct(StringComparer.Ordinal).ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>Vanilla entries never contain '-'; BaseLib prefixes modded entries with "MODID-".</summary>
    internal static bool IsModded(string member) => member.IndexOf('-') >= 0;

    /// <summary>FNV-1a over the original hash and every pool (name, count, members in order). Pure — exposed for tests.</summary>
    internal static uint FoldPools(uint original, IReadOnlyList<PoolDigest> pools)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint h = offset;
        void Mix(byte b) { h ^= b; h *= prime; }
        void MixUInt(uint v) { Mix((byte)v); Mix((byte)(v >> 8)); Mix((byte)(v >> 16)); Mix((byte)(v >> 24)); }
        void MixString(string s) { foreach (byte b in Encoding.UTF8.GetBytes(s)) Mix(b); Mix(0); }

        MixUInt(original);
        MixUInt((uint)pools.Count);
        foreach (PoolDigest pool in pools)
        {
            MixString(pool.Pool);
            MixUInt((uint)pool.Members.Count);
            foreach (string member in pool.Members) MixString(member);
        }
        return h;
    }
}
