using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using HextechFormRuneCoopFix;

internal static class RewardChoiceTests
{
    public static void Run(Action<string, bool> check, string gameData)
    {
        string steamApps = Directory.GetParent(Directory.GetParent(gameData)!.FullName)!.Parent!.FullName;
        string workshop = Path.Combine(steamApps, "workshop", "content", "2868840");
        string choices = Path.Combine(workshop, "3795496596", "RelicRewardChoices", "RelicRewardChoices.dll");
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            string path = Path.Combine(workshop, "3737335127", "BaseLib", name);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        check("RelicRewardChoices integration DLL available", File.Exists(choices));
        if (!File.Exists(choices)) throw new FileNotFoundException("RelicRewardChoices integration DLL required", choices);
        Assembly hex = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HextechRunes");
        MethodInfo target = RewardChoicePatches.Bind(hex, Assembly.LoadFrom(choices), new Harmony("octoFixes.ChoiceTest"));
        var info = Harmony.GetPatchInfo(target)!;
        check("custom reward selection hook binds to installed API", info.Owners.Contains("octoFixes.ChoiceTest"));
        check("custom reward prefix/postfix/finalizer installed", info.Prefixes.Count(p => p.owner == "octoFixes.ChoiceTest") == 1 &&
            info.Postfixes.Count(p => p.owner == "octoFixes.ChoiceTest") == 1 && info.Finalizers.Count(p => p.owner == "octoFixes.ChoiceTest") == 1);
        RewardChoicePatches.Bind(hex, Assembly.LoadFrom(choices), new Harmony("octoFixes.ChoiceTest"));
        check("compatibility binding is idempotent", Harmony.GetPatchInfo(target)!.Postfixes.Count(p => p.owner == "octoFixes.ChoiceTest") == 1);

        object existing = new(), selected = new(), unrelated = new();
        int grants = 0;
        var claim = new RewardChoicePatches.Claim<object>(new[] { existing });
        check("existing owned copy excluded", claim.FindNew(new[] { existing }, _ => true) == null);
        check("only newly selected copy identified", ReferenceEquals(claim.FindNew(new[] { existing, unrelated, selected }, x => ReferenceEquals(x, selected)), selected));
        check("claim begins once", claim.TryBegin() && !claim.TryBegin());
        bool result = claim.Complete(Task.FromResult(false), () => selected, _ => { grants++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        check("failed finalization does not duplicate", !result && grants == 0);
        check("failed finalization releases retry", claim.TryBegin());
        result = claim.Complete(Task.FromResult(true), () => null, _ => { grants++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        check("retry duplicates remembered pickup exactly once", result && grants == 1 && !claim.TryBegin());
        claim.Complete(Task.FromResult(true), () => selected, _ => { grants++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        check("repeated completion never duplicates again", grants == 1);

        var skipped = new RewardChoicePatches.Claim<object>(Array.Empty<object>());
        skipped.TryBegin();
        skipped.Complete(Task.FromResult(true), () => null, _ => { grants++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        check("skip without pickup gives no bonus", grants == 1);
        var cancelled = new RewardChoicePatches.Claim<object>(Array.Empty<object>());
        cancelled.TryBegin();
        cancelled.Complete(Task.FromResult(false), () => null, _ => Task.CompletedTask).GetAwaiter().GetResult();
        cancelled.TryBegin(new[] { unrelated });
        check("cancelled menu excludes relic acquired from another reward before retry",
            ReferenceEquals(cancelled.FindNew(new[] { unrelated, selected }, _ => true), selected));
        var faulted = new RewardChoicePatches.Claim<object>(Array.Empty<object>());
        faulted.TryBegin();
        try { faulted.Complete(Task.FromException<bool>(new InvalidOperationException("original")), () => selected, _ => Task.CompletedTask).GetAwaiter().GetResult(); }
        catch (InvalidOperationException ex) { check("original async failure propagated", ex.Message == "original"); }
        check("original async failure releases retry", faulted.TryBegin());
        faulted.ReleaseAfterSynchronousFailure();
        check("synchronous failure releases retry", faulted.TryBegin());
        int uncertainAttempts = 0;
        try { faulted.Complete(Task.FromResult(true), () => selected, _ => { uncertainAttempts++; throw new InvalidOperationException("uncertain grant"); }).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { }
        check("uncertain grant cannot be repeated", uncertainAttempts == 1 && !faulted.TryBegin());
    }
}
