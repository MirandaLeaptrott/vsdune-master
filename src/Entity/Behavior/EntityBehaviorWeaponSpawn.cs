using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace VsDune;


public class EntityBehaviorWeaponSpawn : EntityBehavior
{
    private string itemCode;
    private bool resolved;

    public EntityBehaviorWeaponSpawn(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        itemCode = attributes?["itemCode"].AsString(null);
    }

    public override void AfterInitialized(bool onFirstSpawn)
    {
        base.AfterInitialized(onFirstSpawn);
        TryEquip();
    }

    public override void OnEntitySpawn()
    {
        base.OnEntitySpawn();
        TryEquip();
    }

    private void TryEquip()
    {
        if (resolved) return;
        if (entity.World.Side != EnumAppSide.Server) return;
        if (string.IsNullOrEmpty(itemCode)) { resolved = true; return; }
        if (entity is not EntityAgent agent) { resolved = true; return; }

        var slot = agent.RightHandItemSlot;
        if (slot == null || !slot.Empty) { resolved = true; return; }

        var loc = new AssetLocation(itemCode);
        var item = entity.World.GetItem(loc);
        if (item == null)
        {
            entity.World.Logger.Warning("[VSDune] WeaponSpawn: item '{0}' not found for {1}; leaving empty-handed.", itemCode, entity.Code);
            resolved = true;
            return;
        }
        slot.Itemstack = new ItemStack(item);
        slot.MarkDirty();
        resolved = true;
    }

    public override string PropertyName() => "vsdune.weaponspawn";
}
