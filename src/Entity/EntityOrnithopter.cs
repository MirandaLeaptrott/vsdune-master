using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace VsDune;


public class EntityOrnithopter : Entity, ISeatInstSupplier, IMountableListener
{
    public override bool AlwaysActive => true;
    public override bool AllowOutsideLoadedRange => true;
    public override bool ApplyGravity => false;
    public override bool IsInteractable => true;

    public EntityOrnithopter()
    {
        SimulationRange = 256;
    }

    public IMountableSeat CreateSeat(IMountable mountable, string seatId, SeatConfig config)
    {
        return new EntityOrnithopterSeat(mountable, seatId, config);
    }

    public void DidMount(EntityAgent entityAgent)
    {
        MarkShapeModified();
    }

    public void DidUnmount(EntityAgent entityAgent)
    {
        MarkShapeModified();
    }
}

public class EntityOrnithopterSeat : EntityRideableSeat
{

    private static readonly AnimationMetaData SitMountedAnim =
        new AnimationMetaData
        {
            Code = "sitboatidle",
            Animation = "sitboatidle",
            AnimationSpeed = 1f,
            EaseInSpeed = 2f,
        }.Init();

    public EntityOrnithopterSeat(IMountable mountablesupplier, string seatId, SeatConfig config)
        : base(mountablesupplier, seatId, config)
    {

        controls.OnAction = onSeatControls;


        DoTeleportOnUnmount = false;
    }

    public override EnumMountAngleMode AngleMode => config.AngleMode;

    public override AnimationMetaData SuggestedAnimation => SitMountedAnim;


    public override bool CanMount(EntityAgent entityAgent)
    {
        if (entityAgent == null) return false;
        if (entityAgent is EntityPlayer) return base.CanMount(entityAgent);
        return true;
    }

    private void onSeatControls(EnumEntityAction action, bool on, ref EnumHandling handled)
    {
        if (!on) return;
        if (Entity == null) return;


        if (Entity.Api.Side != EnumAppSide.Server) return;

        // G (FloorSit) triggers auto-land. Block only when already on the
        // ground or committed to a landing sequence.
        if (action == EnumEntityAction.FloorSit && config.Controllable)
        {
            string mode = Entity.WatchedAttributes.GetString(
                EntityBehaviorOrnithopterFlight.AttrMode,
                EntityBehaviorOrnithopterFlight.ModeAmbient
            );
            bool alreadyLanding =
                mode == EntityBehaviorOrnithopterFlight.ModeLanded
                || mode == EntityBehaviorOrnithopterFlight.ModeLanding;
            if (!alreadyLanding)
            {
                EntityBehaviorOrnithopterFlight.TriggerAutoLand(Entity);
            }
        }

    }
}
