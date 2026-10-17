using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using HextechFormRuneCoopFix;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

// Minimal self-checking harness (no test framework): loads the real HextechRunes.dll, runs the mod's discovery and
// Harmony binding against it, and checks the SetupPlayerTurn postfix binds to the real game method.
// Usage: dotnet HextechFormRuneCoopFix.Tests.dll [path\to\HextechRunes.dll] [path\to\data_sts2_windows_x86_64]
internal static class Program
{
    private const string DefaultHextech = @"F:\SteamLibrary\steamapps\workshop\content\2868840\3747501308\lib\0.111.0\HextechRunes.dll";
    private const string DefaultGameData = @"F:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";

    private static int _failed;

    private static int Main(string[] args)
    {
        string hextechPath = args.Length > 0 ? args[0] : DefaultHextech;
        string gameData = args.Length > 1 ? args[1] : DefaultGameData;

        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in new[] { gameData, Path.GetDirectoryName(hextechPath) ?? "." })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p))
                {
                    return Assembly.LoadFrom(p);
                }
            }
            return null;
        };

        FormRunePatches.LogInfo = s => Console.WriteLine("    [info] " + s);
        FormRunePatches.LogWarn = s => Console.WriteLine("    [warn] " + s);
        FormRunePatches.LogError = s => Console.WriteLine("    [error] " + s);

        // 0. The game method we postfix: signature, parameter names, HookPlayerChoiceContext : PlayerChoiceContext.
        {
            MethodInfo? m = AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn");
            Check("CombatManager.SetupPlayerTurn exists", m != null);
            if (m != null)
            {
                string sig = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                Console.WriteLine("    SetupPlayerTurn(" + sig + ") -> " + m.ReturnType.Name);
                ParameterInfo[] ps = m.GetParameters();
                Check("SetupPlayerTurn returns Task", m.ReturnType == typeof(Task));
                Check("SetupPlayerTurn params (turnState, player, playerChoiceContext)",
                    ps.Length == 3 && ps[0].Name == "turnState" && ps[1].Name == "player" && ps[2].Name == "playerChoiceContext"
                    && ps[2].ParameterType == typeof(HookPlayerChoiceContext));
            }
            Check("HookPlayerChoiceContext derives from PlayerChoiceContext", typeof(PlayerChoiceContext).IsAssignableFrom(typeof(HookPlayerChoiceContext)));
        }

        // 1. Harmony accepts our SetupPlayerTurn postfix (parameter names are validated at patch time).
        {
            Harmony h = new Harmony("HextechFormRuneCoopFix.Tests.SetupPlayerTurn");
            try
            {
                h.PatchAll(typeof(FormRunePatches).Assembly);
                MethodInfo m = AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn");
                Patches? info = Harmony.GetPatchInfo(m);
                Check("SetupPlayerTurn postfix applied", info != null && info.Postfixes.Any(p => p.owner == h.Id));
                MethodInfo mpc = AccessTools.Method(typeof(Hook), nameof(Hook.ModifyCardPlayCount));
                Console.WriteLine("    Hook.ModifyCardPlayCount(" + string.Join(", ", mpc.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ") -> " + mpc.ReturnType.Name);
                Check("Hook.ModifyCardPlayCount postfix applied", Harmony.GetPatchInfo(mpc)?.Postfixes.Any(p => p.owner == h.Id) == true);
                MethodInfo bcp = AccessTools.Method(typeof(Hook), nameof(Hook.BeforeCardPlayed));
                Console.WriteLine("    Hook.BeforeCardPlayed(" + string.Join(", ", bcp.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ") -> " + bcp.ReturnType.Name);
                Check("Hook.BeforeCardPlayed prefix applied", Harmony.GetPatchInfo(bcp)?.Prefixes.Any(p => p.owner == h.Id) == true);
            }
            catch (Exception ex)
            {
                Check("SetupPlayerTurn postfix applied (threw " + ex.GetType().Name + ": " + ex.Message + ")", false);
            }
        }

        // 2. Discovery against a non-Hextech assembly finds nothing.
        Check("no runes in sts2.dll", FormRunePatches.DiscoverFormRunes(typeof(CombatManager).Assembly).Count == 0);

        // 3. Load the real HextechRunes.dll.
        if (!File.Exists(hextechPath))
        {
            Console.WriteLine("  FAIL HextechRunes.dll not found at " + hextechPath + " (pass the path as the first argument)");
            return 2;
        }
        Assembly hextech;
        try
        {
            hextech = Assembly.LoadFrom(hextechPath);
            Console.WriteLine("    loaded " + hextech.FullName);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL cannot load HextechRunes.dll: " + ex);
            return 3;
        }
        try
        {
            hextech.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Console.WriteLine("    note: GetTypes partial, " + ex.Types.Count(t => t == null) + " type(s) failed to load; first: "
                              + ex.LoaderExceptions.FirstOrDefault()?.Message);
        }

        // 4. Discovery: exactly the 5 Form runes mapped to their Form card types.
        Dictionary<string, string> expected = new Dictionary<string, string>
        {
            ["HextechRunes.ReaperFormUpgradeRune"] = "MegaCrit.Sts2.Core.Models.Cards.ReaperForm",
            ["HextechRunes.DemonFormUpgradeRune"] = "MegaCrit.Sts2.Core.Models.Cards.DemonForm",
            ["HextechRunes.EchoFormUpgradeRune"] = "MegaCrit.Sts2.Core.Models.Cards.EchoForm",
            ["HextechRunes.SerpentFormUpgradeRune"] = "MegaCrit.Sts2.Core.Models.Cards.SerpentForm",
            ["HextechRunes.VoidFormUpgradeRune"] = "MegaCrit.Sts2.Core.Models.Cards.VoidForm",
        };
        Dictionary<Type, Type> map = FormRunePatches.DiscoverFormRunes(hextech);
        foreach (KeyValuePair<Type, Type> kv in map.OrderBy(k => k.Key.FullName))
        {
            Console.WriteLine("    discovered " + kv.Key.FullName + " -> " + kv.Value.FullName);
        }
        Check("discovered exactly 5 rune types", map.Count == 5);
        foreach (KeyValuePair<string, string> e in expected)
        {
            KeyValuePair<Type, Type> hit = map.FirstOrDefault(k => k.Key.FullName == e.Key);
            Check(e.Key + " -> " + e.Value, hit.Key != null && hit.Value.FullName == e.Value);
        }

        // 5. Bind: prefix every closed AfterPlayerTurnStartLate and verify via Harmony.GetPatchInfo.
        Harmony harmony = new Harmony("HextechFormRuneCoopFix.Tests");
        BindResult bind = FormRunePatches.BindToAssembly(hextech, harmony);
        Check("bind verified 5 rune types", bind.Verified.Count == 5);
        Check("bind unverified 0", bind.Unverified.Count == 0);

        // 6. Runtime proof: calling the (shared-generic) hook on each rune type hits our prefix and returns a completed
        //    task without running Hextech's body. Instances are created without running constructors.
        foreach (Type rune in map.Keys.OrderBy(t => t.FullName))
        {
            string label = "runtime suppression on " + rune.Name;
            try
            {
                object instance = RuntimeHelpers.GetUninitializedObject(rune);
                MethodInfo hook = rune.GetMethod(FormRunePatches.HookMethodName, new[] { typeof(PlayerChoiceContext), typeof(MegaCrit.Sts2.Core.Entities.Players.Player) })!;
                int before = FormRunePatches.SuppressedCount(rune);
                object? ret = hook.Invoke(instance, new object?[] { null, null });
                Check(label, ret is Task t && t.IsCompletedSuccessfully && FormRunePatches.SuppressedCount(rune) == before + 1);
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                Check(label + " (threw " + inner.GetType().Name + ": " + inner.Message + ")", false);
            }
        }

        // 7. NeutralizeUpgradeRune: found in the real DLL, hook signature/parameter names, prefix verified.
        {
            Type? nrune = hextech.GetType(NeutralizeRunePatches.RuneTypeName, false);
            Check("NeutralizeUpgradeRune found", nrune != null);
            MethodInfo? m = nrune?.GetMethod(NeutralizeRunePatches.HookMethodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
                null, new[] { typeof(PlayerChoiceContext), typeof(CardModel) }, null);
            if (m != null)
            {
                Console.WriteLine("    NeutralizeUpgradeRune.AfterCardDiscarded(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            }
            Check("AfterCardDiscarded(choiceContext, card) declared", m != null && m.GetParameters()[0].Name == "choiceContext" && m.GetParameters()[1].Name == "card");
            Check("NeutralizeUpgradeRune prefix verified", NeutralizeRunePatches.BindToAssembly(hextech, harmony));
            Check("NeutralizeUpgradeRune prefix present in GetPatchInfo", m != null && NeutralizeRunePatches.HasOurPrefix(m, harmony.Id));
        }

        // 8. CardPlay.Target (init-only) can be set through our reflection setter.
        {
            try
            {
                CardPlay play = (CardPlay)RuntimeHelpers.GetUninitializedObject(typeof(CardPlay));
                Creature c = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));
                bool set = NeutralizeRunePatches.SetCardPlayTarget(play, c);
                Check("CardPlay.Target set via reflection", set && ReferenceEquals(play.Target, c));
                NeutralizeRunePatches.SetCardPlayTarget(play, null);
                Check("CardPlay.Target reset to null", play.Target == null);
            }
            catch (Exception ex)
            {
                Check("CardPlay.Target set via reflection (threw " + ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            Check("CardModel.CurrentTarget has a setter", AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.CurrentTarget)) != null);
        }

        // 9. SelectTargetIndex table tests.
        {
            bool[] all3 = { true, true, true };
            Check("base 1: 0,1,2,0,1,2", Seq(6, 1, all3) == "0,1,2,0,1,2");
            Check("base 2: 0,0,1,1,2,2,0,0,1,1,2,2", Seq(12, 2, all3) == "0,0,1,1,2,2,0,0,1,1,2,2");
            Check("base 3: 0,0,0,1,1,1", Seq(6, 3, all3) == "0,0,0,1,1,1");
            Check("middle dead skipped: 0,2,2,0,2,2", Seq(6, 1, new[] { true, false, true }) == "0,2,2,0,2,2");
            Check("last dead wraps: 0,1,0,0,1,0", Seq(6, 1, new[] { true, true, false }) == "0,1,0,0,1,0");
            Check("first dead: 1,1,2,1,1,2", Seq(6, 1, new[] { false, true, true }) == "1,1,2,1,1,2");
            Check("all dead -> -1", Seq(3, 1, new[] { false, false, false }) == "-1,-1,-1");
            Check("no enemies -> -1", NeutralizeRunePatches.SelectTargetIndex(0, 1, Array.Empty<bool>()) == -1);
            Check("base 0 treated as 1", Seq(3, 0, all3) == "0,1,2");
            Check("single enemy", Seq(4, 1, new[] { true }) == "0,0,0,0");
        }

        // 10. Play-count math.
        {
            Check("3 enemies x 2 repeats x base 1 = 6", NeutralizeRunePatches.TotalPlays(3, 2, 1) == 6);
            Check("3 enemies x 2 repeats x base 2 = 12", NeutralizeRunePatches.TotalPlays(3, 2, 2) == 12);
            Check("1 enemy x 2 repeats x base 1 = 2", NeutralizeRunePatches.TotalPlays(1, 2, 1) == 2);
            Check("repeats 0 clamped to 1", NeutralizeRunePatches.TotalPlays(2, 0, 1) == 2);
            Check("base 0 clamped to 1", NeutralizeRunePatches.TotalPlays(2, 2, 0) == 4);
        }

        RewardChoiceTests.Run(Check, gameData);
        Console.WriteLine(_failed == 0 ? "ALL TESTS PASSED" : _failed + " TEST(S) FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static string Seq(int plays, int basePlayCount, bool[] alive)
    {
        return string.Join(",", Enumerable.Range(0, plays).Select(i => NeutralizeRunePatches.SelectTargetIndex(i, basePlayCount, alive)));
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
        if (!ok) _failed++;
    }
}
