using System;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VsDune;


public class FactionHostilitySystem : ModSystem
{
    private const string HarmonyId = "vsdune.factionhostility";

    private Harmony harmony;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        harmony = new Harmony(HarmonyId);

        TryPatch(api, "Vintagestory.GameContent.AiTaskBaseTargetable");
        TryPatch(api, "Vintagestory.GameContent.AiTaskBaseTargetableR");
    }

    private void TryPatch(ICoreAPI api, string typeName)
    {
        Type t = AccessTools.TypeByName(typeName);
        if (t == null)
        {
            api.Logger.Warning("[VSDune] FactionHostility: type {0} not found, skipping.", typeName);
            return;
        }
        // IsTargetableEntity(Entity target, float range)
        MethodInfo m = AccessTools.Method(t, "IsTargetableEntity",
            new Type[] { typeof(Entity), typeof(float) });
        if (m == null)
        {
            api.Logger.Warning("[VSDune] FactionHostility: {0}.IsTargetableEntity(Entity,float) not found, skipping.", typeName);
            return;
        }

        if (m.DeclaringType != t)
        {
            api.Logger.Notification("[VSDune] FactionHostility: {0}.IsTargetableEntity is inherited from {1}, skipping duplicate patch.", typeName, m.DeclaringType?.Name);
            return;
        }
        MethodInfo postfix = AccessTools.Method(typeof(FactionTargetFilter), nameof(FactionTargetFilter.Postfix));
        harmony.Patch(m, postfix: new HarmonyMethod(postfix));
        api.Logger.Notification("[VSDune] FactionHostility: patched {0}.IsTargetableEntity.", typeName);
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(HarmonyId);
    }
}

// The actual filter. Postfix on IsTargetableEntity: vanilla decided
// the target was valid, we get the chance to veto.
public static class FactionTargetFilter
{
    // Per-type FieldRef cache for the AiTaskBaseR fallback path. AiTask
    // Base is hit via direct cast (its 'entity' is public).
    private static readonly ConcurrentDictionary<Type, AccessTools.FieldRef<object, EntityAgent>> entityFieldCache = new();

    public static void Postfix(object __instance, Entity __0, float __1, ref bool __result)
    {
        if (!__result) return;
        if (__0 == null) return;
        Entity target = __0;

        EntityAgent attacker = GetAttackerEntity(__instance);
        if (attacker == null) return;

        string attackerFaction = attacker.Properties?.Attributes?["factionCode"].AsString(null);
        if (string.IsNullOrEmpty(attackerFaction)) return; // attacker isn't a faction outlaw, nothing to filter


        if (target is EntityPlayer playerTarget)
        {
            if (attackerFaction == "fremen")
            {
                string aggrievedUID = attacker.WatchedAttributes.GetString(EntityFactionUnit.AttrAggrievedPlayerUID, null);
                if (!string.IsNullOrEmpty(aggrievedUID) && aggrievedUID == playerTarget.PlayerUID)
                {
                    long aggrievedAt = attacker.WatchedAttributes.GetLong(EntityFactionUnit.AttrAggrievedAtMs, 0);
                    long elapsed = attacker.World.ElapsedMilliseconds - aggrievedAt;
                    if (elapsed >= 0 && elapsed < EntityFactionUnit.AggroWindowMs)
                    {
                        // Stay angry; vanilla targeting decision stands.
                        return;
                    }
                }
                __result = false;
            }
            return;
        }

        string targetFaction = target.Properties?.Attributes?["factionCode"].AsString(null);
        if (string.IsNullOrEmpty(targetFaction)) return; // non-faction target, vanilla decision stands

        if (targetFaction == attackerFaction)
        {
            __result = false;
        }
    }

    private static EntityAgent GetAttackerEntity(object instance)
    {
        // Fast path: AiTaskBase has 'entity' public. No reflection.
        if (instance is AiTaskBase t) return t.entity;

        // Fallback for AiTaskBaseR (entity is protected). Cached
        // delegate per concrete type so the reflection cost amortizes.
        Type type = instance.GetType();
        if (!entityFieldCache.TryGetValue(type, out var fieldRef))
        {
            try { fieldRef = AccessTools.FieldRefAccess<EntityAgent>(type, "entity"); }
            catch { fieldRef = null; }
            entityFieldCache[type] = fieldRef;
        }
        if (fieldRef == null) return null;
        return fieldRef(instance);
    }
}