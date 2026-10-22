using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using OctoFixes;

internal static class DarkMagicianTests
{
    private static Task _original = Task.CompletedTask;
    private static int _bonusCalls;
    private static int _bonusThread;
    private static bool OriginalChannel(ref Task __result) { __result = _original; return false; }
    private static bool BonusChannel(ref Task __result)
    {
        _bonusCalls++;
        _bonusThread = Environment.CurrentManagedThreadId;
        __result = Task.CompletedTask;
        return false;
    }

    public static void Run(Action<string, bool> check, string gameData)
    {
        string steamApps = Directory.GetParent(Directory.GetParent(gameData)!.FullName)!.Parent!.FullName;
        string path = Path.Combine(steamApps, "workshop", "content", "2868840", "3749294247", "TheHeroExpansion.dll");
        Assembly hero = Assembly.LoadFrom(path);
        Type patchType = hero.GetType(DarkMagicianPatches.PatchType, true)!;
        var native = new Harmony("octoFixes.Tests.DarkMagician.Native");
        var fix = new Harmony("octoFixes.Tests.DarkMagician.Fix");
        MethodInfo channel = AccessTools.DeclaredMethod(typeof(OrbCmd), "Channel",
            new[] { typeof(PlayerChoiceContext), typeof(OrbModel), typeof(Player) });
        MethodInfo darkChannel = typeof(OrbCmd).GetMethods().Single(m => m.Name == "Channel" && m.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(DarkOrb));
        native.Patch(channel, prefix: new HarmonyMethod(typeof(DarkMagicianTests), nameof(OriginalChannel)));
        native.Patch(darkChannel, prefix: new HarmonyMethod(typeof(DarkMagicianTests), nameof(BonusChannel)));
        var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
        var creature = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));
        var powers = new List<PowerModel>();
        AccessTools.Field(typeof(Player), "<Creature>k__BackingField").SetValue(player, creature);
        AccessTools.Field(typeof(Creature), "_powers").SetValue(creature, powers);
        var power = (PowerModel)RuntimeHelpers.GetUninitializedObject(hero.GetType(
            "TheHeroExpansion.TheHeroExpansionCode.Powers.DarkMagicianPower", true)!);
        powers.Add(power);
        var lightning = (LightningOrb)RuntimeHelpers.GetUninitializedObject(typeof(LightningOrb));

        // Both loading orders must work: patching the postfix before or after its OrbCmd wrapper exists.
        for (int order = 0; order < 2; order++)
        {
            if (order == 0) native.CreateClassProcessor(patchType).Patch();
            MethodInfo hook = DarkMagicianPatches.Bind(hero, fix);
            DarkMagicianPatches.Bind(hero, fix);
            if (order == 1) native.CreateClassProcessor(patchType).Patch();
            check("Dark Magician binding idempotent order " + order,
                Harmony.GetPatchInfo(hook)!.Transpilers.Count(p => p.owner == fix.Id) == 1);
            _bonusCalls = 0;
            int mainThread = Environment.CurrentManagedThreadId;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _original = completion.Task;
            Pump.Run(async () =>
            {
                Task result = OrbCmd.Channel(null!, lightning, player);
                check("bonus waits for original channel order " + order, _bonusCalls == 0 && !result.IsCompleted);
                await Task.Run(() => completion.SetResult());
                await result;
                check("real Hero postfix creates one bonus on game context order " + order,
                    _bonusCalls == 1 && _bonusThread == mainThread);
            });
            native.Unpatch(channel, AccessTools.DeclaredMethod(patchType, "Postfix"));
            fix.UnpatchAll(fix.Id);
        }
        native.CreateClassProcessor(patchType).Patch();
        DarkMagicianPatches.Bind(hero, fix);
        Pump.Run(async () =>
        {
            _bonusCalls = 0;
            _original = Task.FromException(new InvalidOperationException("original failure"));
            try { await OrbCmd.Channel(null!, lightning, player); check("original channel fault propagated", false); }
            catch (InvalidOperationException ex) { check("original channel fault propagated", ex.Message == "original failure"); }
            check("failed channel gives no bonus", _bonusCalls == 0);
            _original = Task.FromCanceled(new CancellationToken(true));
            try { await OrbCmd.Channel(null!, lightning, player); check("original cancellation propagated", false); }
            catch (OperationCanceledException) { check("original cancellation propagated", true); }
            check("cancelled channel gives no bonus", _bonusCalls == 0);
            _original = Task.CompletedTask;
            powers.Clear();
            check("without Dark Magician original task preserved", ReferenceEquals(OrbCmd.Channel(null!, lightning, player), _original));
            powers.Add(power);
            var frost = (FrostOrb)RuntimeHelpers.GetUninitializedObject(typeof(FrostOrb));
            check("non-lightning orb unchanged", ReferenceEquals(OrbCmd.Channel(null!, frost, player), _original));
            var bonus = new TaskCompletionSource();
            Task pending = DarkMagicianPatches.ContinueInContext(Task.CompletedTask, _ => bonus.Task).Unwrap();
            check("result awaits bonus completion", !pending.IsCompleted);
            bonus.SetResult();
            await pending;
        });
        fix.UnpatchAll(fix.Id);
        native.UnpatchAll(native.Id);
    }

    private sealed class Pump : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public static void Run(Func<Task> action)
        {
            var previous = Current;
            var context = new Pump();
            SetSynchronizationContext(context);
            try
            {
                Task task = action();
                var timer = Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("game context continuation stalled");
                    if (context._queue.TryTake(out var item, 50)) item.Callback(item.State);
                }
                task.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); context._queue.Dispose(); }
        }
    }
}
