using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using HarmonyLib;

namespace OctoFixes;

public static class DarkMagicianPatches
{
    public const string OwnerId = "octoFixes.DarkMagician";
    public const string PatchType = "TheHeroExpansion.TheHeroExpansionCode.Patches.DarkMagicianPatch+OrbChannelPatch";
    private static readonly object Gate = new();
    private static bool _attempted;
    internal static Action<string> LogInfo = _ => { };
    internal static Action<string> LogWarn = _ => { };

    public static void Initialize()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => TryBind(e.LoadedAssembly);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) TryBind(assembly);
    }

    private static void TryBind(Assembly assembly)
    {
        if (assembly.GetName().Name != "TheHeroExpansion") return;
        lock (Gate)
        {
            if (_attempted) return;
            _attempted = true;
            try
            {
                Bind(assembly, new Harmony(OwnerId));
                LogInfo("[DarkMagicianFix] Bonus orb continuation now preserves the game context.");
            }
            catch (Exception ex) { LogWarn($"[DarkMagicianFix] Unsupported hook; original behavior retained. {ex.Message}"); }
        }
    }

    public static MethodInfo Bind(Assembly assembly, Harmony harmony)
    {
        MethodInfo method = AccessTools.DeclaredMethod(assembly.GetType(PatchType, true)!, "Postfix")
            ?? throw new MissingMethodException(PatchType, "Postfix");
        if (!method.IsStatic || method.ReturnType != typeof(void))
            throw new InvalidOperationException("Unexpected Dark Magician postfix API.");
        // Validate the exact call before mutating Harmony. Future versions may have fixed this themselves.
        Rewrite(PatchProcessor.GetOriginalInstructions(method)).ToList();
        if (!(Harmony.GetPatchInfo(method)?.Owners.Contains(harmony.Id) ?? false))
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(DarkMagicianPatches), nameof(Rewrite)));
        return method;
    }

    internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.Select(i => new CodeInstruction(i)).ToList();
        MethodInfo helper = AccessTools.Method(typeof(DarkMagicianPatches), nameof(ContinueInContext));
        var matches = code.Where(i => i.operand is MethodInfo m && m.DeclaringType == typeof(Task) &&
            m.Name == nameof(Task.ContinueWith) && m.IsGenericMethod &&
            m.ReturnType == typeof(Task<Task>) && m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType == typeof(Func<Task, Task>)).ToList();
        if (matches.Count != 1) throw new InvalidOperationException("Expected exactly one unsafe orb continuation.");
        // Stack shape is identical: instance Task + delegate become the helper's two arguments.
        matches[0].opcode = OpCodes.Call;
        matches[0].operand = helper;
        return code;
    }

    internal static async Task<Task> ContinueInContext(Task original, Func<Task, Task> continuation)
    {
        // Called by OrbCmd on the game thread. Await captures Godot's SynchronizationContext,
        // whereas the original ContinueWith selected the default thread-pool scheduler.
        await original;
        return continuation(original);
    }
}
