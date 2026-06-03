using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace VsDune;

public class EdenInstinctRedirectSystem : ModSystem
{
    private const string HarmonyId = "vsdune.edeninstinctredirect";

    private Harmony harmony;
    internal static bool DebugLogEntry = false;

    internal static ILogger Logger;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        Logger = api.Logger;
        harmony = new Harmony(HarmonyId);
        try
        {
            harmony.PatchAll(typeof(EdenInstinctRedirectSystem).Assembly);
            api.Logger.Notification("[VSDune] EdenInstinct ability fueling redirected to spice saturation.");
        }
        catch (System.Exception ex)
        {
            api.Logger.Error("[VSDune] Failed to apply EdenInstinct redirect: " + ex);
        }
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(HarmonyId);
        base.Dispose();
    }

    internal static EntityAgent ExtractEntity(object[] args)
    {
        if (args == null) return null;
        foreach (var a in args)
        {
            if (a is IServerPlayer sp) return sp.Entity;
            if (a is EntityPlayer ep) return ep;
            if (a is EntityAgent ea) return ea;
        }
        return null;
    }

    internal static void LogEntry(MethodBase original, object[] args)
    {
        if (!DebugLogEntry || Logger == null) return;
        var ent = ExtractEntity(args);
        double sat = ent != null ? SpiceSaturationSystem.GetSaturation(ent) : double.NaN;
        Logger.Notification(
            "[VSDune] EdenInstinct patched call: {0}.{1}  spiceSaturation={2:F3}",
            original.DeclaringType?.Name ?? "?",
            original.Name,
            sat
        );
    }
}

[HarmonyPatch]
internal static class EdenInstinctRedirect_Patches
{

    [HarmonyTargetMethod]
    public static System.Reflection.MethodBase StartDrain_Target()
    {
        return AccessTools.Method("EdenInstinct.DrainStabilityServer:StartDrain");
    }

    [HarmonyPrefix]
    public static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        EdenInstinctRedirectSystem.LogEntry(__originalMethod, __args);
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        return TranspileShared.Replace(instructions);
    }
}

[HarmonyPatch]
internal static class EdenInstinctRedirect_DrainTick_Patches
{
    [HarmonyTargetMethod]
    public static System.Reflection.MethodBase DrainTick_Target()
    {
        return AccessTools.Method("EdenInstinct.DrainStabilityServer:DrainTick");
    }

    [HarmonyPrefix]
    public static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        EdenInstinctRedirectSystem.LogEntry(__originalMethod, __args);
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        return TranspileShared.Replace(instructions);
    }
}

[HarmonyPatch]
internal static class EdenInstinctRedirect_ClockmakerStart_Patches
{
    [HarmonyTargetMethod]
    public static System.Reflection.MethodBase StartStabilityRefill_Target()
    {
        return AccessTools.Method("EdenInstinct.ClockmakerServer:StartStabilityRefill");
    }

    [HarmonyPrefix]
    public static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        EdenInstinctRedirectSystem.LogEntry(__originalMethod, __args);
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        return TranspileShared.Replace(instructions);
    }
}

[HarmonyPatch]
internal static class EdenInstinctRedirect_ClockmakerTick_Patches
{
    [HarmonyTargetMethod]
    public static System.Reflection.MethodBase StabilityRefillTick_Target()
    {
        return AccessTools.Method("EdenInstinct.ClockmakerServer:StabilityRefillTick");
    }

    [HarmonyPrefix]
    public static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        EdenInstinctRedirectSystem.LogEntry(__originalMethod, __args);
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        return TranspileShared.Replace(instructions);
    }
}


internal static class TranspileShared
{
    public static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var ins in instructions)
        {
            if (ins.opcode == OpCodes.Ldstr && ins.operand is string s && s == "temporalStability")
            {
                yield return new CodeInstruction(OpCodes.Ldstr, SpiceSaturationSystem.AttrPath);
            }
            else
            {
                yield return ins;
            }
        }
    }
}
