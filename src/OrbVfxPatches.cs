using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Orbs;

namespace OctoFixes;

// This visual subscriber runs inside PowerModel.ApplyInternal. A null reference here
// must not abort the power command on one peer while another finishes the card.
public static class OrbVfxPatches
{
    public const string HarmonyId = "octoFixes.OrbVfx";
    private static int _warned;
    internal static Action<string> LogWarn = _ => { };

    public static MethodInfo Bind(Harmony harmony)
    {
        MethodInfo target = AccessTools.DeclaredMethod(typeof(NOrbVfx), "UpdateFocusPowerState", Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(NOrbVfx), "UpdateFocusPowerState");
        if (target.ReturnType != typeof(void) || target.IsStatic)
            throw new InvalidOperationException("Unexpected orb focus VFX API.");
        if (!(Harmony.GetPatchInfo(target)?.Owners.Contains(harmony.Id) ?? false))
            harmony.Patch(target, finalizer: new HarmonyMethod(typeof(OrbVfxPatches), nameof(FinishFocusUpdate)));
        return target;
    }

    internal static Exception? FinishFocusUpdate(Exception? __exception)
    {
        if (__exception is not NullReferenceException) return __exception;
        if (Interlocked.Exchange(ref _warned, 1) == 0)
            LogWarn("[OrbVfxFix] Ignored null reference in orb focus visuals; power application continues.");
        return null;
    }
}
