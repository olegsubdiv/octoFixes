using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace HextechFormRuneCoopFix;

/// <summary>
/// HextechRunes' NeutralizeUpgradeRune: when the owner discards Neutralize/Suppress, Hextech auto-plays throw-away
/// clones (Repeats x each hittable enemy). Per-card state (e.g. the Momentum enchantment's accumulated damage) then
/// accrues on the clones and is lost. This replaces that with ONE auto-play of the REAL card whose play count is
/// multiplied to enemies x repeats x (normal play count), retargeting each iteration in Hextech's order
/// (repeat-major, enemy-minor).
///
/// Patches:
///  - prefix  HextechRunes.NeutralizeUpgradeRune.AfterCardDiscarded(PlayerChoiceContext choiceContext, CardModel card)  [bound lazily]
///  - postfix Hook.ModifyCardPlayCount(ICombatState combatState, CardModel card, int playCount, Creature? target, out List&lt;AbstractModel&gt; modifyingModels)
///  - prefix  Hook.BeforeCardPlayed(ICombatState combatState, CardPlay cardPlay)
/// </summary>
public static class NeutralizeRunePatches
{
    public const string RuneTypeName = "HextechRunes.NeutralizeUpgradeRune";
    public const string HookMethodName = "AfterCardDiscarded";
    public const int DefaultRepeats = 2;

    /// <summary>Manifest flag <c>neutralizeRuneRealCard</c>; set by ModMain.</summary>
    public static bool Enabled = true;

    private sealed class SeriesState
    {
        public required List<Creature> Enemies { get; init; }
        public required int Repeats { get; init; }
        public required ulong OwnerNetId { get; init; }
        public int BasePlayCount;
    }

    private static readonly object SeriesLock = new object();
    private static readonly Dictionary<CardModel, SeriesState> Series = new Dictionary<CardModel, SeriesState>(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<ulong> ActiveOwners = new HashSet<ulong>();
    private static int _activeCount;

    private static readonly MethodInfo? CardPlayTargetSetter = typeof(CardPlay).GetProperty(nameof(CardPlay.Target))?.GetSetMethod(nonPublic: true);
    private static readonly MethodInfo? CurrentTargetSetter = AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.CurrentTarget));

    // ------------------------------------------------------------------------------------------------------------
    // Pure logic (unit-tested)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Target for iteration <paramref name="playIndex"/> of a series: virtual play v = playIndex / basePlayCount,
    /// preferred enemy v % n (repeat-major, enemy-minor, same order as Hextech's loops); the first alive enemy at or
    /// after it (wrapping, list order only). -1 if none is alive.
    /// </summary>
    public static int SelectTargetIndex(int playIndex, int basePlayCount, IReadOnlyList<bool> alive)
    {
        int n = alive.Count;
        if (n == 0)
        {
            return -1;
        }
        int v = Math.Max(0, playIndex) / Math.Max(1, basePlayCount);
        int preferred = v % n;
        for (int k = 0; k < n; k++)
        {
            int i = (preferred + k) % n;
            if (alive[i])
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Total plays of a series: enemies x repeats x max(1, base).</summary>
    public static int TotalPlays(int enemies, int repeats, int basePlayCount)
    {
        return Math.Max(0, enemies) * Math.Max(1, repeats) * Math.Max(1, basePlayCount);
    }

    /// <summary>Sets the init-only <see cref="CardPlay.Target"/> through reflection.</summary>
    public static bool SetCardPlayTarget(CardPlay cardPlay, Creature? target)
    {
        if (CardPlayTargetSetter == null)
        {
            return false;
        }
        CardPlayTargetSetter.Invoke(cardPlay, new object?[] { target });
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Binding (called from FormRunePatches.EnsureBound, once)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Prefixes NeutralizeUpgradeRune.AfterCardDiscarded; returns true when verified via Harmony.GetPatchInfo.</summary>
    public static bool BindToAssembly(Assembly asm, Harmony harmony)
    {
        Type? rune = null;
        try
        {
            rune = asm.GetType(RuneTypeName, throwOnError: false);
        }
        catch
        {
            // handled below
        }
        if (rune == null)
        {
            FormRunePatches.LogWarn($"[NeutralizeRuneFix] {RuneTypeName} not found in HextechRunes; real-card replay feature skipped.");
            return false;
        }
        MethodInfo? method = rune.GetMethod(HookMethodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            null, new[] { typeof(PlayerChoiceContext), typeof(CardModel) }, null);
        if (method == null)
        {
            FormRunePatches.LogWarn($"[NeutralizeRuneFix] {RuneTypeName}.{HookMethodName}(PlayerChoiceContext, CardModel) not found; feature skipped.");
            return false;
        }
        try
        {
            if (!HasOurPrefix(method, harmony.Id))
            {
                harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(NeutralizeRunePatches), nameof(HookPrefix))));
            }
        }
        catch (Exception ex)
        {
            FormRunePatches.LogWarn($"[NeutralizeRuneFix] Patching {HookMethodName} threw: {ex.GetType().Name}: {ex.Message}");
        }
        bool ok = HasOurPrefix(method, harmony.Id);
        if (ok)
        {
            FormRunePatches.LogInfo("[NeutralizeRuneFix] Hextech clone hook replaced on NeutralizeUpgradeRune");
        }
        else
        {
            FormRunePatches.LogWarn("[NeutralizeRuneFix] could NOT verify the prefix on NeutralizeUpgradeRune.AfterCardDiscarded; Hextech's clone logic may still run.");
        }
        return ok;
    }

    public static bool HasOurPrefix(MethodBase method, string harmonyId)
    {
        Patches? info = Harmony.GetPatchInfo(method);
        return info != null && info.Prefixes.Any(p => p.owner == harmonyId && p.PatchMethod.Name == nameof(HookPrefix));
    }

    // ------------------------------------------------------------------------------------------------------------
    // 1. Rune hook replacement
    // ------------------------------------------------------------------------------------------------------------

    public static bool HookPrefix(object __instance, PlayerChoiceContext choiceContext, CardModel card, ref Task __result)
    {
        if (!Enabled || __instance is not RelicModel relic)
        {
            return true;
        }
        __result = Replacement(relic, choiceContext, card);
        return false;
    }

    private static async Task Replacement(RelicModel relic, PlayerChoiceContext choiceContext, CardModel card)
    {
        bool registered = false;
        ulong netId = 0;
        SeriesState? state = null;
        try
        {
            if (card == null || relic.Owner == null)
            {
                return;
            }
            Player owner = relic.Owner;
            netId = owner.NetId;
            if (card.Owner != owner || owner.Creature.IsDead || !(card is Neutralize || card is Suppress))
            {
                return;
            }
            ICombatState? combatState = owner.Creature.CombatState;
            if (combatState == null || !CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding)
            {
                return;
            }
            List<Creature> enemies = combatState.HittableEnemies.ToList();
            if (enemies.Count == 0)
            {
                return;
            }
            int repeats = DefaultRepeats;
            try
            {
                repeats = relic.DynamicVars["Repeats"].IntValue;
            }
            catch
            {
                repeats = DefaultRepeats;
            }
            repeats = Math.Max(1, repeats);

            lock (SeriesLock)
            {
                if (Series.ContainsKey(card) || ActiveOwners.Contains(netId))
                {
                    return;
                }
                state = new SeriesState { Enemies = enemies, Repeats = repeats, OwnerNetId = netId };
                Series[card] = state;
                ActiveOwners.Add(netId);
                Interlocked.Increment(ref _activeCount);
                registered = true;
            }

            try
            {
                relic.Flash();
            }
            catch
            {
                // visual only
            }

            await CardCmd.AutoPlay(choiceContext, card, enemies[0], AutoPlayType.Default, skipXCapture: false, skipCardPileVisuals: false);
        }
        catch (OperationCanceledException)
        {
            FormRunePatches.LogInfo($"[NeutralizeRuneFix] player {netId}: series cancelled (combat ending).");
        }
        catch (Exception ex)
        {
            FormRunePatches.LogError($"[NeutralizeRuneFix] player {netId}: real-card series failed: {ex}");
        }
        finally
        {
            if (registered)
            {
                lock (SeriesLock)
                {
                    Series.Remove(card);
                    ActiveOwners.Remove(netId);
                    Interlocked.Decrement(ref _activeCount);
                }
                if (state != null && state.BasePlayCount == 0)
                {
                    FormRunePatches.LogInfo($"[NeutralizeRuneFix] player {netId}: {card.Id} discarded -> real-card series not played (auto-play blocked or aborted before play count)");
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // 2. Play count and 3. per-iteration retargeting (static game hooks, patched at init)
    // ------------------------------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardPlayCount))]
    internal static class ModifyCardPlayCountPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CardModel card, ref int __result)
        {
            if (Volatile.Read(ref _activeCount) == 0 || card == null)
            {
                return;
            }
            try
            {
                SeriesState? s;
                int total;
                lock (SeriesLock)
                {
                    if (!Series.TryGetValue(card, out s) || s.BasePlayCount != 0)
                    {
                        return;
                    }
                    s.BasePlayCount = Math.Max(1, __result);
                    total = TotalPlays(s.Enemies.Count, s.Repeats, s.BasePlayCount);
                }
                __result = total;
                FormRunePatches.LogInfo($"[NeutralizeRuneFix] player {s.OwnerNetId}: {card.Id} discarded -> real-card series: " +
                                        $"{s.Enemies.Count} enemies x {s.Repeats} repeats x base {s.BasePlayCount} = {total} plays");
            }
            catch (Exception ex)
            {
                FormRunePatches.LogError($"[NeutralizeRuneFix] ModifyCardPlayCount postfix failed: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCardPlayed))]
    internal static class BeforeCardPlayedPatch
    {
        [HarmonyPrefix]
        private static void Prefix(CardPlay cardPlay)
        {
            if (Volatile.Read(ref _activeCount) == 0 || cardPlay?.Card == null)
            {
                return;
            }
            try
            {
                SeriesState? s;
                lock (SeriesLock)
                {
                    if (!Series.TryGetValue(cardPlay.Card, out s) || s.BasePlayCount == 0)
                    {
                        return;
                    }
                }
                List<bool> alive = s.Enemies.Select(IsHittable).ToList();
                int idx = SelectTargetIndex(cardPlay.PlayIndex, s.BasePlayCount, alive);
                if (idx < 0)
                {
                    return;
                }
                Creature target = s.Enemies[idx];
                if (!ReferenceEquals(cardPlay.Target, target))
                {
                    SetCardPlayTarget(cardPlay, target);
                    try
                    {
                        CurrentTargetSetter?.Invoke(cardPlay.Card, new object?[] { target });
                    }
                    catch
                    {
                        // preview-only field
                    }
                }
                if (FormRunePatches.VerboseLog)
                {
                    FormRunePatches.LogInfo($"[NeutralizeRuneFix] player {s.OwnerNetId}: {cardPlay.Card.Id} play {cardPlay.PlayIndex + 1}/{cardPlay.PlayCount} -> enemy #{idx}");
                }
            }
            catch (Exception ex)
            {
                FormRunePatches.LogError($"[NeutralizeRuneFix] BeforeCardPlayed prefix failed: {ex}");
            }
        }
    }

    // Same predicate as CombatState.HittableEnemies (Enemies.Where(e => e.IsHittable)).
    private static bool IsHittable(Creature c)
    {
        try
        {
            return c.IsHittable;
        }
        catch
        {
            return false;
        }
    }
}
