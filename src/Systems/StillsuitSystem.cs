using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsDune;


public class StillsuitSystem : ModSystem
{
    private ICoreServerAPI sapi;
    private IServerNetworkChannel sipChannel;
    private readonly HashSet<int> stillsuitTopIds = new HashSet<int>();
    private readonly HashSet<int> stillsuitPantsIds = new HashSet<int>();
    private static MethodInfo modifyThirstMethod;
    private static bool reflectionResolved;

    // 18s / 1.5 per tick = ~20 real-time minutes from empty to 100.
    private const float TickIntervalSeconds = 18f;
    private const float WaterPerTick = 1.5f;

    // Default cap if the pants item attributes don't override it.
    private const float DefaultMaxWater = 100f;

    private const float HydrationPerUnit = 5f;

    public const string SipPacketChannel = "vsdune.stillsuitsip";
    private const string HoDThirstRateMulStat = "HoD:ThirstRateMul";
    private const string ThirstMulSourceCode = "vsdune-stillsuit";
    private const float ThirstMulSuitOn = 0.8f;
    private const float ThirstMulSuitOff = 1.0f;

    // Cool night air; the body sweats far less.
    private const string ThirstMulNightSourceCode = "vsdune-night";
    private const float ThirstMulNight = 0.6f;
    private const float ThirstMulDay = 1.0f;
    private const float NightStartHour = 19f;
    private const float NightEndHour = 6f;

    // Thopter cockpit acts as shade: reduced thirst while mounted.
    private const string ThirstMulThopterSourceCode = "vsdune-thopter";
    private const float ThirstMulThopterMounted = 0.7f;
    private const float ThirstMulThopterOff = 1.0f;

    // Faster sub-tick just for the thirst-multiplier stat. The water-
    // collection tick (18s) is too slow for "react to equip/unequip".
    private const float ThirstMulTickIntervalSeconds = 2f;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        sipChannel = api.Network.RegisterChannel(SipPacketChannel)
            .RegisterMessageType<SipRequestPacket>();
        sipChannel.SetMessageHandler<SipRequestPacket>(OnSipRequest);

        api.Event.RegisterGameTickListener(OnTick, (int)(TickIntervalSeconds * 1000));
        api.Event.RegisterGameTickListener(OnThirstMulTick, (int)(ThirstMulTickIntervalSeconds * 1000));
    }

    private void OnThirstMulTick(float dt)
    {
        // Calendar hour wraps 0-24; night band is 19:00-06:00.
        float hour = sapi.World.Calendar.HourOfDay;
        bool isNight = hour >= NightStartHour || hour < NightEndHour;
        float nightTarget = isNight ? ThirstMulNight : ThirstMulDay;

        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp) continue;
            if (sp.Entity == null) continue;

            // Only touch the stat if HoD already registered the category. If we Set before HoD registers, we'd create it as WeightedSum (additive) which makes multiplier values wrong. Checking the enumerator is O(n) over a tiny dict.
            bool hodRegistered = false;
            foreach (var kvp in sp.Entity.Stats)
            {
                if (kvp.Key == HoDThirstRateMulStat) { hodRegistered = true; break; }
            }
            if (!hodRegistered) continue;

            bool fullSuitOn = (stillsuitTopIds.Count > 0 && stillsuitPantsIds.Count > 0)
                              && FindEquippedPants(sp, requiresFullSuit: true) != null;
            float suitTarget = fullSuitOn ? ThirstMulSuitOn : ThirstMulSuitOff;

            bool inThopter = (sp.Entity as EntityAgent)?.MountedOn?.MountSupplier?.OnEntity is EntityOrnithopter;
            float thopterTarget = inThopter ? ThirstMulThopterMounted : ThirstMulThopterOff;

            sp.Entity.Stats.Set(HoDThirstRateMulStat, ThirstMulSourceCode, suitTarget, persistent: false);
            sp.Entity.Stats.Set(HoDThirstRateMulStat, ThirstMulNightSourceCode, nightTarget, persistent: false);
            sp.Entity.Stats.Set(HoDThirstRateMulStat, ThirstMulThopterSourceCode, thopterTarget, persistent: false);
        }
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        base.AssetsFinalize(api);
        if (api.Side != EnumAppSide.Server) return;

        RegisterVariant(api, "stillsuit-top", stillsuitTopIds);
        RegisterVariant(api, "stillsuit-top-ragged", stillsuitTopIds);
        RegisterVariant(api, "stillsuit-top-enhanced", stillsuitTopIds);
        RegisterVariant(api, "stillsuit-pants", stillsuitPantsIds);
        RegisterVariant(api, "stillsuit-pants-ragged", stillsuitPantsIds);
        RegisterVariant(api, "stillsuit-pants-enhanced", stillsuitPantsIds);

        if (stillsuitTopIds.Count == 0 || stillsuitPantsIds.Count == 0)
        {
            api.Logger.Warning("[VSDune] StillsuitSystem: no stillsuit variant items registered. Water collection disabled.");
        }
    }

    private static void RegisterVariant(ICoreAPI api, string code, HashSet<int> bucket)
    {
        var it = api.World.GetItem(new AssetLocation("vsdune", code));
        if (it != null) bucket.Add(it.Id);
    }

    private void OnTick(float dt)
    {
        if (stillsuitTopIds.Count == 0 || stillsuitPantsIds.Count == 0) return;

        foreach (var p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp) continue;
            if (sp.Entity == null) continue;
            if (sp.WorldData?.CurrentGameMode != EnumGameMode.Survival) continue;

            var pantsSlot = FindEquippedPants(sp, requiresFullSuit: true);
            if (pantsSlot == null || pantsSlot.Empty) continue;

            float current = pantsSlot.Itemstack.Attributes.GetFloat("stillsuitWater", 0f);
            float max = pantsSlot.Itemstack.Collectible?.Attributes?["stillsuitWaterMax"].AsFloat(DefaultMaxWater) ?? DefaultMaxWater;
            if (current >= max) continue;

            float updated = System.Math.Min(max, current + WaterPerTick);
            pantsSlot.Itemstack.Attributes.SetFloat("stillsuitWater", updated);
            pantsSlot.MarkDirty();
        }
    }

    private void OnSipRequest(IServerPlayer fromPlayer, SipRequestPacket packet)
    {
        if (stillsuitPantsIds.Count == 0) return;
        if (fromPlayer?.Entity == null) return;

        // Drinking requires the full suit equipped, same as collection.
        // The stillsuit only seals as a system when both pieces are
        // worn; a player carrying just the pants shouldn't be able to
        // drain the reservoir while the top is in their inventory.
        var pantsSlot = FindEquippedPants(fromPlayer, requiresFullSuit: true);
        if (pantsSlot == null || pantsSlot.Empty)
        {
            sapi.SendIngameError(fromPlayer, "vsdune-no-stillsuit", "Equip both stillsuit pieces to drink.");
            return;
        }

        float water = pantsSlot.Itemstack.Attributes.GetFloat("stillsuitWater", 0f);
        if (water <= 0f)
        {
            sapi.SendIngameError(fromPlayer, "vsdune-empty-stillsuit", "Stillsuit reclamation tank is empty.");
            return;
        }

        float hydration = water * HydrationPerUnit;
        ApplyHydration(fromPlayer.Entity, hydration);

        pantsSlot.Itemstack.Attributes.SetFloat("stillsuitWater", 0f);
        pantsSlot.MarkDirty();

        sapi.World.PlaySoundAt(
            new AssetLocation("game:sounds/player/drink"),
            fromPlayer.Entity, fromPlayer, true, 16f, 0.9f
        );
    }

    private ItemSlot FindEquippedPants(IServerPlayer player, bool requiresFullSuit)
    {
        var charInv = player.InventoryManager?.GetOwnInventory("character");
        if (charInv == null) return null;

        ItemSlot pants = null;
        bool hasTop = false;
        foreach (var slot in charInv)
        {
            if (slot == null || slot.Empty) continue;
            int id = slot.Itemstack.Item?.Id ?? -1;
            if (id < 0) continue;
            if (stillsuitTopIds.Contains(id)) hasTop = true;
            else if (stillsuitPantsIds.Contains(id)) pants = slot;
        }

        if (requiresFullSuit && !hasTop) return null;
        return pants;
    }

    private void ApplyHydration(EntityAgent entity, float amount)
    {
        var thirstBehavior = entity.GetBehavior("thirst");
        if (thirstBehavior == null) return;

        if (!reflectionResolved)
        {
            modifyThirstMethod = thirstBehavior.GetType().GetMethod(
                "ModifyThirst",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(float), typeof(float) },
                null
            );
            reflectionResolved = true;
        }

        modifyThirstMethod?.Invoke(thirstBehavior, new object[] { amount, 0f });
    }
}

[ProtoBuf.ProtoContract]
public class SipRequestPacket
{
}
