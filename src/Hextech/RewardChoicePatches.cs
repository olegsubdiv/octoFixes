using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;

namespace HextechFormRuneCoopFix;

/// <summary>Bridge a custom relic reward to Double Vision's existing grant and network synchronization.</summary>
public static class RewardChoicePatches
{
    public const string RewardTypeName = "RelicRewardChoices.Rewards.RelicRewardChoiceReward";
    public const string RuneTypeName = "HextechRunes.DoubleVisionRune";
    private const string OwnerId = "octoFixes.RewardChoices";
    private static readonly object BindingLock = new();
    private static Harmony? _harmony;
    private static bool _bound;
    private static bool _binding;
    private static MethodInfo? _activeRunes;
    private static MethodInfo? _duplicate;
    private static FieldInfo? _obtained;
    private static FieldInfo? _selectedId;
    private static readonly ConditionalWeakTable<object, Context> Claims = new();
    public static Action<string> LogInfo = _ => { };
    public static Action<string> LogWarn = _ => { };

    // Reference identity matters: owning another copy of this relic before selection is normal.
    internal sealed class Claim<T> where T : class
    {
        private readonly HashSet<T> _before;
        private T? _source;
        private bool _committed;
        public bool Running { get; private set; }
        public Claim(IEnumerable<T> before) => _before = new(before, ReferenceEqualityComparer.Instance);
        public bool TryBegin(IEnumerable<T>? before = null)
        {
            if (Running || _committed) return false;
            // A cancelled menu may be reopened after another reward granted the same model ID.
            if (before != null && _source == null)
            {
                _before.Clear();
                foreach (T item in before) _before.Add(item);
            }
            Running = true;
            return true;
        }
        public void ReleaseAfterSynchronousFailure() => Running = false;
        public T? FindNew(IEnumerable<T> current, Func<T, bool> matches) =>
            current.FirstOrDefault(item => !_before.Contains(item) && matches(item));
        public async Task<bool> Complete(Task<bool> original, Func<T?> observe, Func<T, Task> duplicate)
        {
            try
            {
                bool complete = await original;
                _source ??= observe();
                if (complete && _source != null && !_committed)
                {
                    // An uncertain pickup must never be retried into an unlimited reward loop.
                    _committed = true;
                    await duplicate(_source);
                }
                return complete;
            }
            finally { Running = false; }
        }
    }

    private sealed class Context
    {
        public readonly Player Player;
        public readonly object[] Runes;
        public readonly Claim<RelicModel> Claim;
        public Context(Player player, object[] runes)
        {
            Player = player;
            Runes = runes;
            Claim = new(player.Relics);
        }
    }

    public static void Initialize()
    {
        _harmony = new Harmony(OwnerId);
        AppDomain.CurrentDomain.AssemblyLoad += (_, _) => EnsureBound();
        EnsureBound();
    }

    private static void EnsureBound()
    {
        lock (BindingLock)
        {
            if (_bound || _binding || _harmony == null) return;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Assembly? hex = assemblies.FirstOrDefault(a => a.GetName().Name == "HextechRunes");
            Assembly? choices = assemblies.FirstOrDefault(a => a.GetName().Name == "RelicRewardChoices");
            if (hex == null || choices == null) return;
            try
            {
                _binding = true;
                Bind(hex, choices, _harmony);
                _bound = true;
                LogInfo("[RewardChoiceFix] Double Vision enabled for RelicRewardChoices.");
            }
            catch (Exception ex)
            {
                // Avoid retrying a changed private API on every subsequent assembly load.
                _bound = true;
                LogWarn($"[RewardChoiceFix] Compatibility hook unavailable; rewards unchanged. {ex.Message}");
            }
            finally { _binding = false; }
        }
    }

    public static MethodInfo Bind(Assembly hex, Assembly choices, Harmony harmony)
    {
        Type rune = hex.GetType(RuneTypeName, throwOnError: true)!;
        Type reward = choices.GetType(RewardTypeName, throwOnError: true)!;
        MethodInfo active = AccessTools.DeclaredMethod(rune, "GetActiveRunes", new[] { typeof(Player) })
            ?? throw new MissingMethodException(RuneTypeName, "GetActiveRunes");
        MethodInfo duplicate = AccessTools.DeclaredMethod(rune, "DuplicateObtainedRelic",
            new[] { typeof(Player), typeof(RelicModel), typeof(bool) })
            ?? throw new MissingMethodException(RuneTypeName, "DuplicateObtainedRelic");
        MethodInfo select = AccessTools.DeclaredMethod(reward, "OnSelect", Type.EmptyTypes)
            ?? throw new MissingMethodException(RewardTypeName, "OnSelect");
        FieldInfo obtained = AccessTools.DeclaredField(reward, "_obtainCompleted")
            ?? throw new MissingFieldException(RewardTypeName, "_obtainCompleted");
        FieldInfo selected = AccessTools.DeclaredField(reward, "_selectedModelId")
            ?? throw new MissingFieldException(RewardTypeName, "_selectedModelId");
        if (!typeof(Reward).IsAssignableFrom(reward) || !active.IsStatic || duplicate.IsStatic ||
            !typeof(IEnumerable).IsAssignableFrom(active.ReturnType) || duplicate.ReturnType != typeof(Task) ||
            select.ReturnType != typeof(Task<bool>) || obtained.FieldType != typeof(bool) || selected.FieldType != typeof(string))
            throw new InvalidOperationException("Unexpected Double Vision or relic reward API.");
        _activeRunes = active;
        _duplicate = duplicate;
        _obtained = obtained;
        _selectedId = selected;
        if (!(Harmony.GetPatchInfo(select)?.Owners.Contains(harmony.Id) ?? false))
            harmony.Patch(select,
                prefix: new HarmonyMethod(typeof(RewardChoicePatches), nameof(BeforeSelect)),
                postfix: new HarmonyMethod(typeof(RewardChoicePatches), nameof(AfterSelect)),
                finalizer: new HarmonyMethod(typeof(RewardChoicePatches), nameof(SelectFailed)));
        return select;
    }

    private static void BeforeSelect(object __instance, out Context? __state)
    {
        __state = null;
        try
        {
            if (!Claims.TryGetValue(__instance, out Context? context))
            {
                // Reloaded or previously claimed rewards are not retroactively duplicated.
                if (_obtained?.GetValue(__instance) is not false) return;
                Player player = ((Reward)__instance).Player;
                // The upstream method enforces the owning peer/alive-player rules.
                object[] runes = ((_activeRunes!.Invoke(null, new object[] { player }) as IEnumerable)
                    ?? Array.Empty<object>()).Cast<object>().ToArray();
                if (runes.Length == 0) return;
                context = Claims.GetValue(__instance, _ => new Context(player, runes));
            }
            if (context.Claim.TryBegin(_obtained!.GetValue(__instance) is false ? context.Player.Relics : null))
                __state = context;
        }
        catch (Exception ex) { LogWarn($"[RewardChoiceFix] Could not track reward; original selection preserved. {ex.Message}"); }
    }

    private static void AfterSelect(object __instance, Context? __state, ref Task<bool> __result)
    {
        if (__state == null) return;
        Context context = __state;
        __result = context.Claim.Complete(__result, () =>
        {
            if (_obtained!.GetValue(__instance) is not true) return null;
            string? id = _selectedId!.GetValue(__instance) as string;
            return context.Claim.FindNew(context.Player.Relics, relic => relic.Id.ToString() == id);
        }, async source =>
        {
            try
            {
                foreach (object rune in context.Runes)
                    await (Task)_duplicate!.Invoke(rune, new object[] { context.Player, source, true })!;
                LogInfo($"[RewardChoiceFix] Applied Double Vision to selected {source.Id}.");
            }
            catch (Exception ex)
            {
                LogWarn($"[RewardChoiceFix] Bonus grant failed or is uncertain; automatic retry disabled. {ex}");
            }
        });
    }

    private static Exception? SelectFailed(Context? __state, Exception? __exception)
    {
        // Async failures release the claim in Complete's finally; synchronous failures never reached it.
        if (__exception != null && __state != null)
            __state.Claim.ReleaseAfterSynchronousFailure();
        return __exception;
    }
}
