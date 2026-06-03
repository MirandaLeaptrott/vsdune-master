using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace VsDune;

public class VsDune : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        api.RegisterBlockEntityClass("SpicesandDrying", typeof(BlockEntitySpicesandDrying));
        api.RegisterBlockEntityClass("ThopterWreck", typeof(BlockEntityThopterWreck));
        api.RegisterBlockEntityClass("Thumper", typeof(BlockEntityThumper));
        api.RegisterBlockClass("BlockDewdrop", typeof(BlockDewdrop));
        api.RegisterBlockClass("BlockThumper", typeof(BlockThumper));

        api.RegisterEntityBehaviorClass("vsdune.randomoutfit", typeof(EntityBehaviorRandomOutfit));
        api.RegisterEntityBehaviorClass("vsdune.weaponspawn", typeof(EntityBehaviorWeaponSpawn));
        api.RegisterEntityBehaviorClass("vsdune.handinventory", typeof(EntityBehaviorHandInv));

        api.RegisterEntity("VertwormEntity", typeof(VertwormEntity));
        api.RegisterEntityBehaviorClass("vsdune.vertwormai", typeof(EntityBehaviorVertwormAI));

        api.RegisterEntityBehaviorClass("vsdune.outlawarrakis", typeof(EntityBehaviorOutlawArrakis));

        api.RegisterItemClass("ItemSpice", typeof(ItemSpice));
        api.RegisterItemClass("ItemBloodFilter", typeof(ItemBloodFilter));
        api.RegisterEntity("EntityFactionUnit", typeof(EntityFactionUnit));
        api.RegisterEntity("EntityOrnithopter", typeof(EntityOrnithopter));
        api.RegisterEntityBehaviorClass("vsdune.ornithopterflight", typeof(EntityBehaviorOrnithopterFlight));
        api.RegisterEntityBehaviorClass("vsdune.ornithoptersound", typeof(EntityBehaviorOrnithopterSound));
        AiTaskRegistry.Register<AiTaskMeleeAttack>("melee");
        AiTaskRegistry.Register<AiTaskSeekEntity>("engageentity");
        AiTaskRegistry.Register<AiTaskStayCloseToEntity>("stayclosetoherd");
        AiTaskRegistry.Register<AiTaskShootAtEntityR>("shootatentity");
        AiTaskRegistry.Register<AiTaskNoOp>("morale");
        AiTaskRegistry.Register<AiTaskNoOp>("reacttoprojectiles");
        AiTaskRegistry.Register<AiTaskNoOp>("playanimationatrange");

        api.Logger.Notification("[VSDune] Loaded. The spice must flow.");
    }
}
