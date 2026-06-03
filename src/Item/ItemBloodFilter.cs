using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsDune;


public class ItemBloodFilter : Item
{
    private const float WaterLitresPerUse = 1.0f;
    private const int DurabilityPerUse = 1;
    // Persisted on the corpse via its WatchedAttributes tree.
    private const string AttrBloodDrained = "vsdune.bloodDrained";

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        // Bail unless the right-click target is an entity. Block/air
        // right-clicks pass through to vanilla behavior.
        if (entitySel?.Entity == null)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
            return;
        }

        // Server-only: world-state mutation must be authoritative.
        // Client just sees the swing animation via vanilla routing.
        if (byEntity.World.Side != EnumAppSide.Server)
        {
            handling = EnumHandHandling.PreventDefault;
            return;
        }

        var target = entitySel.Entity;

        // Dead only: the tool harvests corpses, not wounded foes.
        if (target.Alive)
        {
            ((byEntity as EntityPlayer)?.Player as IServerPlayer)?.SendIngameError("bloodfilter-living", "Blood Filter only works on dead enemies.");
            handling = EnumHandHandling.PreventDefault;
            return;
        }

        // Don't filter player corpses.
        if (target is EntityPlayer)
        {
            ((byEntity as EntityPlayer)?.Player as IServerPlayer)?.SendIngameError("bloodfilter-player", "Cannot filter player corpses.");
            handling = EnumHandHandling.PreventDefault;
            return;
        }

        // Reject already-drained corpses.
        if (target.WatchedAttributes.GetBool(AttrBloodDrained, false))
        {
            ((byEntity as EntityPlayer)?.Player as IServerPlayer)?.SendIngameError("bloodfilter-drained", "This body has already been drained.");
            handling = EnumHandHandling.PreventDefault;
            return;
        }

        // Look up the player's available water containers and try to
        // pour 1L of waterportion into the first one that accepts it.
        bool poured = TryDepositWater(byEntity);
        if (!poured)
        {
            ((byEntity as EntityPlayer)?.Player as IServerPlayer)?.SendIngameError("bloodfilter-nocontainer", "No water container with room.");
            handling = EnumHandHandling.PreventDefault;
            return;
        }

        // Tool wear + flag the corpse drained. Body stays in world so
        // the death scene reads; filter rejects re-use via the flag.
        slot.Itemstack.Collectible.DamageItem(byEntity.World, byEntity, slot, DurabilityPerUse);
        target.WatchedAttributes.SetBool(AttrBloodDrained, true);
        target.WatchedAttributes.MarkPathDirty(AttrBloodDrained);

        byEntity.World.PlaySoundAt(
            new AssetLocation("game:sounds/effect/squish1"),
            target.Pos.X, target.Pos.Y, target.Pos.Z,
            null, true, 16f, 0.9f
        );

        handling = EnumHandHandling.PreventDefault;
    }

    private bool TryDepositWater(EntityAgent byEntity)
    {
        if (byEntity is not EntityPlayer player) return false;
        var inv = player.Player?.InventoryManager;
        if (inv == null) return false;

        // Resolve the water item once. waterportion is the standard
        // drinkable-water content stack vanilla containers accept.
        var water = byEntity.World.GetItem(new AssetLocation("game", "waterportion"));
        if (water == null) return false;
        var liquidStack = new ItemStack(water);

        // The active hand is the bloodfilter; skip it explicitly so we
        // don't try to pour water into the filter itself.
        ItemSlot held = inv.ActiveHotbarSlot;

        foreach (var slot in IterateCandidateSlots(player, held))
        {
            if (slot == null || slot.Empty) continue;
            var bClass = slot.Itemstack.Collectible as BlockLiquidContainerBase;
            if (bClass == null) continue;

            int moved = bClass.TryPutLiquid(slot.Itemstack, liquidStack, WaterLitresPerUse);
            if (moved > 0)
            {
                slot.MarkDirty();
                return true;
            }
        }
        return false;
    }

    private IEnumerable<ItemSlot> IterateCandidateSlots(EntityPlayer player, ItemSlot heldSkip)
    {
        var inv = player.Player.InventoryManager;
        var offhand = inv.GetOwnInventory("offhandhand");
        if (offhand != null)
        {
            foreach (var s in offhand) yield return s;
        }

        var hotbar = inv.GetHotbarInventory();
        if (hotbar != null)
        {
            foreach (var s in hotbar)
            {
                if (s == heldSkip) continue;
                yield return s;
            }
        }

        // Backpack / extra inventories.
        var backpack = inv.GetOwnInventory("backpack");
        if (backpack != null)
        {
            foreach (var s in backpack) yield return s;
        }
    }
}