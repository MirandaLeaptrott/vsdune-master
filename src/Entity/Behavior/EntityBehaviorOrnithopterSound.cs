using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsDune;


public class EntityBehaviorOrnithopterSound : EntityBehavior
{
    private const float FlyingLoopVolume = 0.7f;
    private const float FlyingLoopFadeInSeconds = 2.5f;

    private ICoreClientAPI capi;
    private ILoadedSound flyingLoop;
    private string lastMode;

    public EntityBehaviorOrnithopterSound(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        if (entity.Api is not ICoreClientAPI clientApi) return;
        capi = clientApi;

        // Load the looped flight sound. ShouldLoop=true 
        flyingLoop = capi.World.LoadSound(new SoundParams
        {
            Location = new AssetLocation("vsdune:sounds/thopterflying"),
            ShouldLoop = true,
            Position = new Vec3f((float)entity.Pos.X, (float)entity.Pos.Y, (float)entity.Pos.Z),
            DisposeOnFinish = false,
            Volume = 0f,
            Range = 60f,
            RelativePosition = false,
        });

        entity.WatchedAttributes.RegisterModifiedListener(EntityBehaviorOrnithopterFlight.AttrMode, OnModeChanged);
        entity.WatchedAttributes.RegisterModifiedListener(EntityBehaviorOrnithopterFlight.AttrGlideMode, OnGlideChanged);

        lastMode = entity.WatchedAttributes.GetString(EntityBehaviorOrnithopterFlight.AttrMode, EntityBehaviorOrnithopterFlight.ModeAmbient);
        ApplyState(lastMode);
    }

    private void OnModeChanged()
    {
        if (capi == null) return;
        string mode = entity.WatchedAttributes.GetString(EntityBehaviorOrnithopterFlight.AttrMode, EntityBehaviorOrnithopterFlight.ModeAmbient);

        // Edge-triggered one-shot: ground -> takeoff 
        if (mode == EntityBehaviorOrnithopterFlight.ModeTakeoff && lastMode != EntityBehaviorOrnithopterFlight.ModeTakeoff)
        {
            capi.World.PlaySoundAt(
                new AssetLocation("vsdune:sounds/thopterstartingup"),
                entity, null, false, 96f, 1.0f
            );
        }

        ApplyState(mode);
        lastMode = mode;
    }

    private void OnGlideChanged()
    {
        if (capi == null) return;
        // Re-evaluate sound state with the current mode.
        string mode = entity.WatchedAttributes.GetString(EntityBehaviorOrnithopterFlight.AttrMode, EntityBehaviorOrnithopterFlight.ModeAmbient);
        ApplyState(mode);
    }

    private void ApplyState(string mode)
    {
        if (flyingLoop == null) return;

        // Loop is on for every mode where the wings are still flapping.
        bool gliding = entity.WatchedAttributes.GetBool(EntityBehaviorOrnithopterFlight.AttrGlideMode, false);
        bool flyingMode =
            mode == EntityBehaviorOrnithopterFlight.ModeAmbient ||
            mode == EntityBehaviorOrnithopterFlight.ModePilot ||
            mode == EntityBehaviorOrnithopterFlight.ModeTakeoff ||
            mode == EntityBehaviorOrnithopterFlight.ModeApproaching ||
            mode == EntityBehaviorOrnithopterFlight.ModeFleeing ||
            mode == EntityBehaviorOrnithopterFlight.ModeLanding ||
            mode == EntityBehaviorOrnithopterFlight.ModeFalling;
        bool shouldLoop = flyingMode && !gliding;

        if (shouldLoop && !flyingLoop.IsPlaying)
        {
            flyingLoop.SetVolume(0f);
            flyingLoop.Start();
            flyingLoop.FadeTo(FlyingLoopVolume, FlyingLoopFadeInSeconds, _ => { });
        }
        else if (!shouldLoop && flyingLoop.IsPlaying)
        {
            // Fade rather than hard-stop
            flyingLoop.FadeOutAndStop(0.8f);
        }
    }

    public override void OnGameTick(float dt)
    {
        if (capi == null) return;
        if (flyingLoop == null || !flyingLoop.IsPlaying) return;

        // Keep the looped sound's spatial position
        flyingLoop.SetPosition((float)entity.Pos.X, (float)entity.Pos.Y, (float)entity.Pos.Z);
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        base.OnEntityDespawn(despawn);
        flyingLoop?.Stop();
        flyingLoop?.Dispose();
        flyingLoop = null;
    }

    public override string PropertyName() => "vsdune.ornithoptersound";
}
