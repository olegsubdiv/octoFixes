using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace HextechFormRuneCoopFix;

/// <summary>
/// Result of binding to the HextechRunes assembly.
/// </summary>
public sealed class BindResult
{
    /// <summary>Rune relic type -> Form card type (the TCard generic argument).</summary>
    public Dictionary<Type, Type> RuneToCard { get; } = new Dictionary<Type, Type>();
    /// <summary>Rune types whose closed AfterPlayerTurnStartLate carries our prefix (per Harmony.GetPatchInfo).</summary>
    public List<Type> Verified { get; } = new List<Type>();
    /// <summary>Rune types that could not be verified as patched.</summary>
    public List<Type> Unverified { get; } = new List<Type>();
}

/// <summary>
/// 1. Neutralises HextechRunes' <c>AutoPlayFormsAtCombatStartRuneBase&lt;TCard&gt;.AfterPlayerTurnStartLate</c>.
/// 2. Replays the same effect deterministically after <c>CombatManager.SetupPlayerTurn</c> on the player's turn 1.
/// </summary>
public static class FormRunePatches
{
    public const string HextechAssemblyName = "HextechRunes";
    public const string RuneBaseTypeName = "HextechRunes.AutoPlayFormsAtCombatStartRuneBase`1";
    public const string HookMethodName = "AfterPlayerTurnStartLate";

    // Settings / sinks are set by ModMain.Initialize (kept here so the tests never touch the game Logger).
    public static bool VerboseLog;
    public static bool DisableAutoPlayFix;
    public static Action<string> LogInfo = _ => { };
    public static Action<string> LogWarn = _ => { };
    public static Action<string> LogError = _ => { };
    internal static Harmony? Harmony;

    private static readonly object BindLock = new object();
    private static volatile bool _bound;
    private static bool _loggedNotLoaded;
    private static volatile Dictionary<Type, Type> _runeToCard = new Dictionary<Type, Type>();
    private static MethodInfo? _beginEndTurnSuppression;

    // Marker "already ran for (combat state, player NetId)". Weak keys: nothing to reset between combats.
    private static readonly ConditionalWeakTable<object, HashSet<ulong>> DoneByCombat = new ConditionalWeakTable<object, HashSet<ulong>>();

    private static readonly object SuppressedLock = new object();
    private static readonly Dictionary<Type, int> SuppressedCounts = new Dictionary<Type, int>();

    // ------------------------------------------------------------------------------------------------------------
    // Discovery / binding
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds every concrete type in <paramref name="asm"/> deriving from a closed
    /// <c>HextechRunes.AutoPlayFormsAtCombatStartRuneBase&lt;TCard&gt;</c> and maps it to TCard.
    /// Tolerates partially loadable assemblies (<see cref="ReflectionTypeLoadException"/>).
    /// </summary>
    public static Dictionary<Type, Type> DiscoverFormRunes(Assembly asm)
    {
        Dictionary<Type, Type> map = new Dictionary<Type, Type>();
        Type?[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        Type? genericDef = null;
        try
        {
            genericDef = asm.GetType(RuneBaseTypeName, throwOnError: false);
        }
        catch
        {
            // fall through to the scan below
        }
        genericDef ??= types.FirstOrDefault(t => t != null && t.FullName == RuneBaseTypeName);
        if (genericDef == null)
        {
            return map;
        }

        foreach (Type? t in types)
        {
            try
            {
                if (t == null || t.IsAbstract || t.IsInterface || t.ContainsGenericParameters)
                {
                    continue;
                }
                for (Type? b = t.BaseType; b != null; b = b.BaseType)
                {
                    if (b.IsGenericType && !b.ContainsGenericParameters && b.GetGenericTypeDefinition() == genericDef)
                    {
                        map[t] = b.GetGenericArguments()[0];
                        break;
                    }
                }
            }
            catch
            {
                // type not fully loadable - ignore
            }
        }
        return map;
    }

    internal static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        try
        {
            if (!_bound && args.LoadedAssembly.GetName().Name == HextechAssemblyName)
            {
                EnsureBound("assembly-load", args.LoadedAssembly);
            }
        }
        catch (Exception ex)
        {
            LogError($"[FormRuneFix] Binding on assembly load failed: {ex}");
        }
    }

    /// <summary>Thread-safe, binds at most once. Cheap to call repeatedly.</summary>
    internal static void EnsureBound(string trigger, Assembly? candidate = null)
    {
        if (_bound)
        {
            return;
        }
        lock (BindLock)
        {
            if (_bound || Harmony == null)
            {
                return;
            }
            Assembly? asm = candidate ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == HextechAssemblyName);
            if (asm == null)
            {
                if (!_loggedNotLoaded)
                {
                    _loggedNotLoaded = true;
                    LogInfo($"[FormRuneFix] HextechRunes not loaded yet ({trigger}); will bind when it loads. Without it this mod does nothing.");
                }
                return;
            }
            _bound = true;
            try
            {
                LogInfo($"[FormRuneFix] Binding to HextechRunes {asm.GetName().Version} ({trigger}).");
                if (!DisableAutoPlayFix)
                {
                    BindToAssembly(asm, Harmony);
                }
            }
            catch (Exception ex)
            {
                LogError($"[FormRuneFix] Binding to HextechRunes failed: {ex}");
            }
            try
            {
                if (NeutralizeRunePatches.Enabled)
                {
                    NeutralizeRunePatches.BindToAssembly(asm, Harmony);
                }
            }
            catch (Exception ex)
            {
                LogError($"[NeutralizeRuneFix] Binding to HextechRunes failed: {ex}");
            }
        }
    }

    /// <summary>
    /// Discovers the Form runes in <paramref name="asm"/>, prefixes each closed <c>AfterPlayerTurnStartLate</c> with
    /// <see cref="HookPrefix"/>, verifies via <see cref="Harmony.GetPatchInfo"/> and activates the replacement map.
    /// </summary>
    public static BindResult BindToAssembly(Assembly asm, Harmony harmony)
    {
        BindResult result = new BindResult();
        Dictionary<Type, Type> map = DiscoverFormRunes(asm);
        foreach (KeyValuePair<Type, Type> kv in map)
        {
            result.RuneToCard[kv.Key] = kv.Value;
        }
        if (map.Count == 0)
        {
            LogWarn($"[FormRuneFix] HextechRunes loaded ({asm.GetName().Version}) but no {RuneBaseTypeName} rune types found; nothing to do.");
            return result;
        }

        // Resolve before disabling the original hooks: the replacement must preserve this scope.
        MethodInfo? suppress = asm.GetType("HextechRunes.HextechFormAutoPlayHooks")?.GetMethod(
            "BeginEndTurnSuppression", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);
        if (suppress == null || !typeof(IDisposable).IsAssignableFrom(suppress.ReturnType))
            throw new MissingMethodException("Hextech form auto-play end-turn suppression unavailable; original hooks preserved.");
        _beginEndTurnSuppression = suppress;

        HarmonyMethod prefix = new HarmonyMethod(AccessTools.Method(typeof(FormRunePatches), nameof(HookPrefix)));
        Dictionary<Type, MethodInfo?> methods = new Dictionary<Type, MethodInfo?>();
        foreach (KeyValuePair<Type, Type> kv in map.OrderBy(k => k.Key.FullName, StringComparer.Ordinal))
        {
            MethodInfo? method = null;
            try
            {
                Type closed = FindClosedBase(kv.Key)
                              ?? throw new InvalidOperationException($"no closed {RuneBaseTypeName} base on {kv.Key}");
                method = closed.GetMethod(HookMethodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(PlayerChoiceContext), typeof(Player) }, null);
                if (method == null)
                {
                    LogWarn($"[FormRuneFix] {closed} has no {HookMethodName}(PlayerChoiceContext, Player).");
                }
                else if (!HasOurPrefix(method, harmony.Id))
                {
                    // Closed instantiations over reference types share one JIT body; each closed MethodInfo is still
                    // patched individually so Harmony records patch info for all of them. Re-detouring the same shared
                    // body with an identical prefix is harmless; failures are logged and settled by the verification.
                    harmony.Patch(method, prefix: prefix);
                }
            }
            catch (Exception ex)
            {
                LogWarn($"[FormRuneFix] Patching {HookMethodName} for {kv.Key.Name} threw (verification decides): {ex.GetType().Name}: {ex.Message}");
            }
            methods[kv.Key] = method;
        }

        foreach (KeyValuePair<Type, MethodInfo?> kv in methods)
        {
            bool ok = false;
            try
            {
                ok = kv.Value != null && HasOurPrefix(kv.Value, harmony.Id);
            }
            catch (Exception ex)
            {
                LogWarn($"[FormRuneFix] GetPatchInfo for {kv.Key.Name} failed: {ex.Message}");
            }
            (ok ? result.Verified : result.Unverified).Add(kv.Key);
        }

        string Describe(IEnumerable<Type> ts) => string.Join(", ", ts.Select(t => $"{t.Name}<{map[t].Name}>"));
        if (result.Unverified.Count == 0)
        {
            LogInfo($"[FormRuneFix] Hextech auto-play hook disabled on {result.Verified.Count} rune type(s): {Describe(result.Verified)}");
        }
        else
        {
            LogWarn($"[FormRuneFix] Hextech auto-play hook disabled on {result.Verified.Count} rune type(s): {Describe(result.Verified)}; " +
                    $"could NOT verify on {result.Unverified.Count}: {Describe(result.Unverified)} (their original hook may still fire).");
        }

        _runeToCard = new Dictionary<Type, Type>(map);
        return result;
    }

    private static Type? FindClosedBase(Type rune)
    {
        for (Type? b = rune.BaseType; b != null; b = b.BaseType)
        {
            if (b.IsGenericType && !b.ContainsGenericParameters && b.GetGenericTypeDefinition().FullName == RuneBaseTypeName)
            {
                return b;
            }
        }
        return null;
    }

    public static bool HasOurPrefix(MethodBase method, string harmonyId)
    {
        Patches? info = Harmony.GetPatchInfo(method);
        return info != null && info.Prefixes.Any(p => p.owner == harmonyId && p.PatchMethod.Name == nameof(HookPrefix));
    }

    /// <summary>Number of times the Hextech hook was suppressed per rune runtime type (used by the self-check).</summary>
    public static int SuppressedCount(Type runeType)
    {
        lock (SuppressedLock)
        {
            return SuppressedCounts.TryGetValue(runeType, out int n) ? n : 0;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // 1. Hextech hook neutraliser
    // ------------------------------------------------------------------------------------------------------------

    public static bool HookPrefix(object __instance, ref Task __result)
    {
        if (DisableAutoPlayFix)
        {
            return true;
        }
        __result = Task.CompletedTask;
        try
        {
            Type t = __instance?.GetType() ?? typeof(object);
            int n;
            lock (SuppressedLock)
            {
                SuppressedCounts.TryGetValue(t, out n);
                SuppressedCounts[t] = ++n;
            }
            if (n == 1)
            {
                LogInfo($"[FormRuneFix] suppressed HextechRunes {t.Name}.{HookMethodName} (first call; further calls only with verboseLog).");
            }
            else if (VerboseLog)
            {
                LogInfo($"[FormRuneFix] suppressed HextechRunes {t.Name}.{HookMethodName} (#{n}).");
            }
        }
        catch
        {
            // never throw into the game
        }
        return false;
    }

    // ------------------------------------------------------------------------------------------------------------
    // 2. Deterministic replacement: postfix on CombatManager.SetupPlayerTurn (private async Task).
    //    Original: SetupPlayerTurn(CombatTurnState turnState, Player player, HookPlayerChoiceContext playerChoiceContext)
    //    CombatTurnState is internal, so the postfix does not bind it (the combat state comes from player.Creature).
    // ------------------------------------------------------------------------------------------------------------

    [HarmonyPatch]
    internal static class SetupPlayerTurnPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn")
                   ?? throw new MissingMethodException(nameof(CombatManager), "SetupPlayerTurn");
        }

        private static void Postfix(ref Task __result, Player player, HookPlayerChoiceContext playerChoiceContext)
        {
            try
            {
                if (DisableAutoPlayFix)
                {
                    return;
                }
                EnsureBound("SetupPlayerTurn");
                if (_runeToCard.Count == 0 || player == null || __result == null)
                {
                    return;
                }
                __result = AfterSetupPlayerTurn(__result, player, playerChoiceContext);
            }
            catch (Exception ex)
            {
                LogError($"[FormRuneFix] SetupPlayerTurn postfix failed: {ex}");
            }
        }
    }

    private static async Task AfterSetupPlayerTurn(Task original, Player player, PlayerChoiceContext choiceContext)
    {
        // Preserve the original's completion semantics (including cancellation) exactly.
        await original;
        try
        {
            await RunReplacement(player, choiceContext);
        }
        catch (OperationCanceledException)
        {
            LogInfo($"[FormRuneFix] player {player.NetId}: auto-play cancelled (combat ending).");
        }
        catch (Exception ex)
        {
            LogError($"[FormRuneFix] player {player.NetId}: auto-play replacement failed: {ex}");
        }
    }

    private static async Task RunReplacement(Player player, PlayerChoiceContext choiceContext)
    {
        Dictionary<Type, Type> map = _runeToCard;
        List<(RelicModel Relic, Type CardType)> runes = new List<(RelicModel, Type)>();
        foreach (RelicModel relic in player.Relics)
        {
            if (relic != null && map.TryGetValue(relic.GetType(), out Type? cardType))
            {
                runes.Add((relic, cardType));
            }
        }
        if (runes.Count == 0)
        {
            return;
        }

        string who = $"player {player.NetId}";
        PlayerCombatState? pcs = player.PlayerCombatState;
        if (pcs == null)
        {
            LogInfo($"[FormRuneFix] {who}: skipped: no PlayerCombatState");
            return;
        }
        if (pcs.TurnNumber != 1)
        {
            if (VerboseLog)
            {
                LogInfo($"[FormRuneFix] {who} turn {pcs.TurnNumber}: skipped: not turn 1");
            }
            return;
        }
        if (player.Creature.IsDead)
        {
            LogInfo($"[FormRuneFix] {who} turn 1: skipped: player dead");
            return;
        }
        if (CombatManager.Instance.IsOverOrEnding)
        {
            LogInfo($"[FormRuneFix] {who} turn 1: skipped: combat over or ending");
            return;
        }
        object? combatState = player.Creature.CombatState;
        if (combatState == null)
        {
            LogInfo($"[FormRuneFix] {who} turn 1: skipped: creature has no combat state");
            return;
        }
        HashSet<ulong> done = DoneByCombat.GetValue(combatState, _ => new HashSet<ulong>());
        lock (done)
        {
            if (!done.Add(player.NetId))
            {
                LogInfo($"[FormRuneFix] {who} turn 1: skipped: already ran for this combat");
                return;
            }
        }

        foreach ((RelicModel relic, Type cardType) in runes)
        {
            if (relic.IsMelted)
            {
                LogInfo($"[FormRuneFix] {who} turn 1: rune {relic.Id} skipped: melted");
                continue;
            }
            if (!IsAvailableForCharacter(relic, player))
            {
                LogInfo($"[FormRuneFix] {who} turn 1: rune {relic.Id} skipped: not available for this character (same gate as HextechRunes)");
                continue;
            }

            List<CardModel> cards = pcs.AllCards
                .Where(c => c.GetType() == cardType && c.Owner == player && IsDrawHandDiscard(c.Pile?.Type))
                .ToList();
            string cardName = cards.Count > 0 ? cards[0].Id.ToString() : cardType.Name;
            if (cards.Count == 0)
            {
                LogInfo($"[FormRuneFix] {who} turn 1: rune {relic.Id} found 0 {cardName} card(s) in Draw/Hand/Discard -> nothing to play");
                continue;
            }
            LogInfo($"[FormRuneFix] {who} turn 1: rune {relic.Id} found {cards.Count} {cardName} card(s) in Draw/Hand/Discard -> auto-playing");

            try
            {
                relic.Flash();
            }
            catch
            {
                // visual only
            }

            int played = 0;
            foreach (CardModel card in cards)
            {
                if (CombatManager.Instance.IsOverOrEnding)
                {
                    LogInfo($"[FormRuneFix] {who} turn 1: rune {relic.Id} stopped: combat over or ending");
                    return;
                }
                PileType? from = card.Pile?.Type;
                // Moving out of Hand must run the game's visual path: it detaches the hand holder.
                // Keep off-hand forms silent, but preserve visuals through the entire play for hand cards.
                bool skipVisuals = from != PileType.Hand;
                await CardPileCmd.Add(card, PileType.Play, CardPilePosition.Bottom, null, skipVisuals: skipVisuals);
                if (card.Pile?.Type == PileType.Play)
                {
                    if (VerboseLog)
                    {
                        LogInfo($"[FormRuneFix] {who}: auto-playing {card.Id} (from {from})");
                    }
                    await PlayWithTurnSuppressed(() => CardCmd.AutoPlay(choiceContext, card, null,
                        AutoPlayType.Default, skipXCapture: false, skipCardPileVisuals: skipVisuals));
                    played++;
                }
                else if (VerboseLog)
                {
                    LogInfo($"[FormRuneFix] {who}: {card.Id} not in Play pile after move (now {card.Pile?.Type}), not played");
                }
            }
            if (VerboseLog)
            {
                LogInfo($"[FormRuneFix] {who} turn 1: rune {relic.Id} auto-played {played}/{cards.Count}");
            }
        }
    }

    internal static async Task PlayWithTurnSuppressed(Func<Task> play)
    {
        // Matches Hextech's original scope; AsyncLocal confines it to this awaited auto-play.
        using IDisposable scope = (IDisposable)(_beginEndTurnSuppression?.Invoke(null, null)
            ?? throw new InvalidOperationException("Form end-turn suppression is not bound."));
        await play();
    }

    private static bool IsDrawHandDiscard(PileType? t)
    {
        return t == PileType.Draw || t == PileType.Hand || t == PileType.Discard;
    }

    // Mirrors HextechRunes' own gate (protected abstract CardUpgradeRuneBase<T>.IsAvailableForCharacter(Player)).
    // Character-based, therefore deterministic across peers. If it cannot be called, assume available.
    private static bool IsAvailableForCharacter(RelicModel relic, Player player)
    {
        try
        {
            MethodInfo? m = relic.GetType().GetMethod("IsAvailableForCharacter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(Player) }, null);
            if (m == null || m.ReturnType != typeof(bool))
            {
                return true;
            }
            return (bool)m.Invoke(relic, new object[] { player })!;
        }
        catch
        {
            return true;
        }
    }
}
