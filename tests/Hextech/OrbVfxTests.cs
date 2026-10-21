using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;
using OctoFixes;

internal static class OrbVfxTests
{
    public static void Run(Action<string, bool> check)
    {
        var harmony = new Harmony("octoFixes.Tests.OrbVfx");
        MethodInfo target = AccessTools.DeclaredMethod(typeof(NOrbVfx), "UpdateFocusPowerState");
        // Reproduce the real callback with a particle container not initialized by _Ready.
        // This failure path never allocates or accesses native Godot objects.
        var vfx = (NOrbVfx)RuntimeHelpers.GetUninitializedObject(typeof(NOrbVfx));
        var particles = (NParticlesContainer)RuntimeHelpers.GetUninitializedObject(typeof(NParticlesContainer));
        GC.SuppressFinalize(vfx);
        GC.SuppressFinalize(particles);
        AccessTools.Field(typeof(NOrbVfx), "_focusedParticles").SetValue(vfx, particles);
        bool reproduced = false;
        try { target.Invoke(vfx, null); }
        catch (TargetInvocationException ex) when (ex.InnerException is NullReferenceException) { reproduced = true; }
        check("uninitialized orb particles reproduce focus callback null reference", reproduced);
        OrbVfxPatches.Bind(harmony);
        OrbVfxPatches.Bind(harmony);
        check("orb visual finalizer binds once", Harmony.GetPatchInfo(target)!.Finalizers.Count == 1);
        bool laterSubscriber = false;
        Action powerApplied = () => target.Invoke(vfx, null);
        powerApplied += () => laterSubscriber = true;
        powerApplied();
        check("broken orb visuals do not abort subsequent power subscribers", laterSubscriber);
        AccessTools.Field(typeof(NOrbVfx), "_focusedParticles").SetValue(vfx, null);
        target.Invoke(vfx, null);
        check("orb callback without particles remains valid", true);
        check("successful focus callback stays successful", OrbVfxPatches.FinishFocusUpdate(null) == null);
        var unexpected = new InvalidOperationException("unrelated");
        check("unrelated exceptions preserved", ReferenceEquals(OrbVfxPatches.FinishFocusUpdate(unexpected), unexpected));
        harmony.UnpatchAll(harmony.Id);
    }
}
