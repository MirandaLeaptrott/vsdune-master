using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace VsDune;


public class EntityBehaviorHandInv : EntityBehaviorContainer
{
    private readonly InventoryGeneric inv;

    public EntityBehaviorHandInv(Entity entity) : base(entity)
    {
        inv = new InventoryGeneric(2, null, null);
    }

    public override InventoryBase Inventory => inv;

    public override string InventoryClassName => "vsdune.handinv";

    public override string PropertyName() => "vsdune.handinventory";

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        Api = entity.World.Api;
        inv.LateInitialize("vsdune.handinv-" + entity.EntityId, Api);
        loadInv();

        base.Initialize(properties, attributes);
    }
}
