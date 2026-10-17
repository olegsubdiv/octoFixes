using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using HextechFormRuneCoopFix;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

internal static class FormTurnTests
{
    public static void Run(Action<string, bool> check, Assembly hextech) => RunAsync(check, hextech).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool> check, Assembly hextech)
    {
        Type hooks = hextech.GetType("HextechRunes.HextechFormAutoPlayHooks", true)!;
        Type patch = hooks.GetNestedType("EndTurnPatch", BindingFlags.NonPublic)!;
        MethodInfo prefix = patch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool CanEndTurn() => (bool)prefix.Invoke(null, null)!;
        var harmony = new Harmony("octoFixes.Tests.FormEndTurn");
        harmony.CreateClassProcessor(patch).Patch();
        MethodInfo endTurn = AccessTools.Method(typeof(PlayerCmd), "EndTurn",
            new[] { typeof(Player), typeof(bool), typeof(Func<Task>) });
        check("native form end-turn prefix binds to installed PlayerCmd", Harmony.GetPatchInfo(endTurn)?.Owners.Contains(harmony.Id) == true);
        check("manual play can end turn outside form auto-play", CanEndTurn());
        await FormRunePatches.PlayWithTurnSuppressed(async () =>
        {
            check("start-of-combat form cannot end turn", !CanEndTurn());
            // The real patched command must return before accessing the deliberately null player.
            PlayerCmd.EndTurn(null!, false);
            await Task.Yield();
            check("form end-turn suppression survives await", !CanEndTurn());
            await FormRunePatches.PlayWithTurnSuppressed(() => Task.CompletedTask);
            check("nested form playback preserves outer suppression", !CanEndTurn());
        });
        check("normal end turn restored after auto-play", CanEndTurn());
        try
        {
            await FormRunePatches.PlayWithTurnSuppressed(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("play failed");
            });
        }
        catch (InvalidOperationException ex) { check("form playback failure propagated", ex.Message == "play failed"); }
        check("end turn restored after failed auto-play", CanEndTurn());
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Task pending = FormRunePatches.PlayWithTurnSuppressed(async () =>
        {
            entered.SetResult();
            await release.Task;
            check("pending form retains its own suppression", !CanEndTurn());
        });
        await entered.Task;
        check("pending form does not suppress unrelated caller", CanEndTurn());
        release.SetResult();
        await pending;
        harmony.UnpatchAll(harmony.Id);
    }
}
