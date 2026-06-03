using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsDune;


public class EntityBehaviorOrnithopterFlight : EntityBehavior
{
    // Watched-attribute paths.
    public const string AttrFlightDirX = "vsdune.flightDirX";
    public const string AttrFlightDirY = "vsdune.flightDirY";
    public const string AttrFlightDirZ = "vsdune.flightDirZ";
    public const string AttrFlightSpeed = "vsdune.flightSpeed";
    public const string AttrFlightStarted = "vsdune.flightStartedMs";
    public const string AttrMode = "vsdune.ornithopterMode";
    public const string AttrTakeoffStartedMs = "vsdune.takeoffStartedMs";
    public const string AttrLandingStartedMs = "vsdune.landingStartedMs";
    // Set on the entity before triggering takingoff to tell the flight controller what to do once the takeoff sequence completes
    public const string AttrTakeoffNextMode = "vsdune.takeoffNextMode";

    // True when the pilot toggled to glide mode (wings locked, fast
    // forward, low descent). False = default hover/flap mode.
    public const string AttrGlideMode = "vsdune.glideMode";

    public const string ModeAmbient = "ambient";
    public const string ModePilot = "pilot";
    public const string ModeLanded = "landed";
    public const string ModeTakeoff = "takingoff";
    // Pilot left mid-flight or never had one
    public const string ModeFalling = "falling";
    // Inverse of takingoff
    public const string ModeLanding = "landing";
    // New modes for the spice-blow raid choreography
    public const string ModeApproaching = "approaching";
    public const string ModeFleeing = "fleeing";

    // Approach target coords.
    public const string AttrTargetX = "vsdune.targetX";
    public const string AttrTargetY = "vsdune.targetY";
    public const string AttrTargetZ = "vsdune.targetZ";

    private const double FlightLifetimeSeconds = 180.0;
    private const double DespawnIfFartherThan = 320.0;
    private const float FlightAnimSpeed = 1.2f;

    // Falling: gravity-only descent when pilot leaves mid-air
    private const float FallGravity = 9.81f;
    private const float FallMaxSpeed = 16f;

    // Pilot-mode thrust tunables.
    private const float PilotCruiseSpeed = 8f;
    private const float PilotSprintSpeed = 14f;
    private const float PilotReverseSpeed = -3f;
    private const float PilotIdleDrift = 1.5f;
    private const float PilotClimbSpeed = 5f;
    private const float PilotSpeedLerp = 4f; // per-second lerp toward target
    private const float PilotStrafeSpeed = 4f;

    // Glide mode (tap Ctrl to toggle): wings lock, ~75 percent of the
    // old dive speed, ~5 percent of the descent. Auto-stalls back to
    // hover if forward speed drops below StallSpeed.
    private const float PilotGlideSpeed = 24f;
    private const float PilotGlideDescent = 0.25f;
    private const float PilotGlideSpeedLerp = 6f;
    private const float StallSpeed = 8f;

    // Hold-Sneak dismount timer
    private const long DismountHoldMs = 2000;
    private readonly Dictionary<string, long> sneakHoldStartBySeat = new Dictionary<string, long>();

    // Takeoff sequence
    private const float TakeoffDurationSeconds = 7.33f;
    private const float TakeoffVerticalLift = 8f; // total blocks up

    // Landing sequence
    private const float LandingDurationSeconds = 7.0f;
    private const float LandingDescentFraction = 0.429f;
    // Altitude above the target at which TickApproaching hands off to TickLanding
    private const float LandingHandoffAltitude = 6f;

    // Internal smoothed speed for pilot mode
    private float smoothedSpeed = 0f;

    // Per-instance fall velocity for ModeFalling.
    private float fallVelY = 0f;

    // Tracks the last mode we observed so we can reset per-seat Sneak
    // timers on any transition.
    private string lastMode = null;

    // Edge-detection for the glide-mode toggle (tap Ctrl).
    private bool prevCtrlPressed = false;

    // Per-behavior CachingCollisionTester. The world's CollisionTester
    // is shared with other systems (picking, raycasts) and its
    // class-level min/max + CollisionBoxList cache gets clobbered
    // between calls, returning stale boxes that let us phase through
    // terrain. Owning our own tester and calling NewTick before each
    // ApplyTerrainCollision gives us a fresh, consistent collision
    // result every move.
    private readonly CachingCollisionTester collisionTester = new CachingCollisionTester();

    public EntityBehaviorOrnithopterFlight(Entity entity) : base(entity) { }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        if (entity.Api.Side != EnumAppSide.Server) return;

        var w = entity.WatchedAttributes;
        if (!w.HasAttribute(AttrFlightStarted))
        {
            w.SetDouble(AttrFlightStarted, entity.World.ElapsedMilliseconds / 1000.0);
            w.MarkPathDirty(AttrFlightStarted);
        }
        if (!w.HasAttribute(AttrMode))
        {
            // Default new spawns to ambient
            w.SetString(AttrMode, ModeAmbient);
            w.MarkPathDirty(AttrMode);
        }
    }

    public override void OnGameTick(float dt)
    {
        if (entity.Api.Side != EnumAppSide.Server) return;
        if (!entity.Alive) return;

        string mode = entity.WatchedAttributes.GetString(AttrMode, ModeAmbient);

        if (mode != lastMode)
        {
            lastMode = mode;
            // Reset all per-seat Sneak-hold timers on any mode change
            sneakHoldStartBySeat.Clear();

            // Clear glide mode on any mode change so re-entering pilot
            // starts in hover.
            if (entity.WatchedAttributes.GetBool(AttrGlideMode, false))
            {
                entity.WatchedAttributes.SetBool(AttrGlideMode, false);
                entity.WatchedAttributes.MarkPathDirty(AttrGlideMode);
            }
            prevCtrlPressed = false;
        }

        // Detect pilot
        var pilot = GetActivePilot();
        if (pilot != null && mode == ModeAmbient)
        {
            SetMode(ModePilot);
            mode = ModePilot;
        }
        else if (pilot == null && mode == ModePilot)
        {
            // Pilot dismounted mid-air
            SetMode(ModeFalling);
            mode = ModeFalling;
        }
        // Landed -> takingoff transition
        if (mode == ModeLanded && pilot != null)
        {
            var pilotControls = pilot.MountedOn?.Controls ?? pilot.Controls;
            if (pilotControls.Forward)
            {
                entity.WatchedAttributes.SetDouble(AttrTakeoffStartedMs, entity.World.ElapsedMilliseconds);
                entity.WatchedAttributes.MarkPathDirty(AttrTakeoffStartedMs);
                SetMode(ModeTakeoff);
                mode = ModeTakeoff;
            }
        }

        switch (mode)
        {
            case ModeAmbient: TickAmbient(dt); break;
            case ModePilot: if (pilot != null) TickPilot(dt, pilot); break;
            case ModeLanded: TickLanded(dt); break;
            case ModeTakeoff: TickTakeoff(dt); break;
            case ModeLanding: TickLanding(dt); break;
            case ModeApproaching: TickApproaching(dt); break;
            case ModeFleeing: TickFleeing(dt); break;
            case ModeFalling: TickFalling(dt); break;
        }

        TickDismountTimers(mode);

        // Despawn checks only run in flying modes
        if (mode == ModeAmbient)
        {
            double startedSec = entity.WatchedAttributes.GetDouble(AttrFlightStarted, 0);
            double nowSec = entity.World.ElapsedMilliseconds / 1000.0;
            if (nowSec - startedSec > FlightLifetimeSeconds)
            {
                entity.Die(EnumDespawnReason.Expire);
                return;
            }
            if (NoPlayerWithinRange())
            {
                entity.Die(EnumDespawnReason.Removed);
                return;
            }
        }
    }

    // Moves the entity by the given delta, respecting terrain collision.
    // The 1.22 API takes Motion (velocity) and a dtFactor; we reconstruct
    // velocity from our per-tick delta by dividing by dt. NewTick resets
    // our local tester's cached block-box list so collision always runs
    // against fresh blocks.
    private void MoveWithCollision(double dx, double dy, double dz, float dt)
    {
        entity.Pos.Motion.Set(dx / dt, dy / dt, dz / dt);

        Vec3d newPos = new Vec3d();
        collisionTester.NewTick(entity.Pos);
        collisionTester.ApplyTerrainCollision(
            entity,
            entity.Pos,
            dt,
            ref newPos
        );

        entity.Pos.X = newPos.X;
        entity.Pos.Y = newPos.Y;
        entity.Pos.Z = newPos.Z;

        // Zero out motion so the engine doesn't re-apply it on the same tick.
        entity.Pos.Motion.Set(0, 0, 0);
    }

    private void TickAmbient(float dt)
    {
        var w = entity.WatchedAttributes;
        double dirX = w.GetDouble(AttrFlightDirX, 0);
        double dirY = w.GetDouble(AttrFlightDirY, 0);
        double dirZ = w.GetDouble(AttrFlightDirZ, 0);
        float speed = w.GetFloat(AttrFlightSpeed, 0f);

        MoveWithCollision(dirX * speed * dt, dirY * speed * dt, dirZ * speed * dt, dt);

        if (dirX != 0 || dirZ != 0)
        {
            entity.Pos.Yaw = (float)Math.Atan2(dirX, dirZ);
        }

        PlayFlightLoop();
    }

    private void TickPilot(float dt, EntityAgent pilot)
    {
        // When mounted, EntityPlayer routes input to the seat controls.
        var controls = pilot.MountedOn?.Controls ?? pilot.Controls;

        // Align ornithopter yaw with pilot's view yaw.
        entity.Pos.Yaw = pilot.Pos.Yaw;

        // Tap-Ctrl edge: toggle between hover and glide.
        bool ctrlNow = controls.CtrlKey;
        bool ctrlPressEdge = ctrlNow && !prevCtrlPressed;
        prevCtrlPressed = ctrlNow;

        bool gliding = entity.WatchedAttributes.GetBool(AttrGlideMode, false);
        if (ctrlPressEdge)
        {
            gliding = !gliding;
            entity.WatchedAttributes.SetBool(AttrGlideMode, gliding);
            entity.WatchedAttributes.MarkPathDirty(AttrGlideMode);
        }

        // Pick target speed from mode + controls.
        float target;
        float lerpRate;
        if (gliding)
        {
            // Glide locks wings: forward thrust if held, else drag down
            // to a stall. No reverse/strafe.
            target = controls.Forward ? PilotGlideSpeed : 0f;
            lerpRate = PilotGlideSpeedLerp;
        }
        else
        {
            if (controls.Forward) target = controls.Sprint ? PilotSprintSpeed : PilotCruiseSpeed;
            else if (controls.Backward) target = PilotReverseSpeed;
            else target = PilotIdleDrift;
            lerpRate = PilotSpeedLerp;
        }

        smoothedSpeed = GameMath.Lerp(smoothedSpeed, target, GameMath.Clamp(lerpRate * dt, 0f, 1f));

        // Glide stall: if forward speed drops below StallSpeed, force
        // hover. Animation + descent reset on next tick's hover branch.
        if (gliding && smoothedSpeed < StallSpeed)
        {
            gliding = false;
            entity.WatchedAttributes.SetBool(AttrGlideMode, false);
            entity.WatchedAttributes.MarkPathDirty(AttrGlideMode);
        }

        double dirX = Math.Sin(entity.Pos.Yaw);
        double dirZ = Math.Cos(entity.Pos.Yaw);

        double vy;
        if (gliding)
        {
            // Passive sink, with Sneak as a "nose down" push so the
            // pilot can intentionally lose altitude in glide.
            vy = controls.Sneak ? -PilotClimbSpeed : -PilotGlideDescent;
        }
        else if (controls.Jump) vy = PilotClimbSpeed;
        else if (controls.Sneak) vy = -PilotClimbSpeed;
        else vy = 0;

        // Strafe only in hover (wings locked = no lateral thrust).
        double strafeX = 0;
        double strafeZ = 0;
        if (!gliding)
        {
            float strafe = 0;
            if (controls.Right) strafe += PilotStrafeSpeed;
            if (controls.Left) strafe -= PilotStrafeSpeed;
            if (strafe != 0)
            {
                strafeX = -Math.Cos(entity.Pos.Yaw) * strafe;
                strafeZ = Math.Sin(entity.Pos.Yaw) * strafe;
            }
        }

        MoveWithCollision(
            (dirX * smoothedSpeed + strafeX) * dt,
            vy * dt,
            (dirZ * smoothedSpeed + strafeZ) * dt,
            dt
        );

        if (gliding)
        {
            // landedwingsstowed holds the wings folded flat with no
            // keyframe motion, which is the look glide wants. (Door
            // posture is shared with parked but that's fine in air.)
            SetSingleAnim("landedwingsstowed");
        }
        else
        {
            PlayFlightLoop();
        }
    }

    // Single source of truth for which animation is playing right now
    private void SetSingleAnim(string code)
    {
        var anim = entity.AnimManager;
        if (anim == null) return;
        var active = anim.ActiveAnimationsByAnimCode;
        if (active.Count == 1 && active.ContainsKey(code)) return;
        anim.StopAllAnimations();
        anim.StartAnimation(code);
    }

    private void TickLanded(float dt)
    {
        // Parked pose
        SetSingleAnim("landedwingsstoweddooropen");
    }

    private void TickTakeoff(float dt)
    {
        // Time-driven scripted sequence

        double startMs = entity.WatchedAttributes.GetDouble(AttrTakeoffStartedMs, 0);
        double elapsedMs = entity.World.ElapsedMilliseconds - startMs;
        double t = GameMath.Clamp(elapsedMs / (TakeoffDurationSeconds * 1000.0), 0, 1);

        // Vertical ascent
        float avgRate = TakeoffVerticalLift / TakeoffDurationSeconds;
        float yRate;
        if (t < 0.455) yRate = 0f;
        else if (t < 0.591) yRate = avgRate * 0.5f;
        else if (t < 0.727) yRate = avgRate * 1.5f;
        else yRate = avgRate * 2.5f;
        entity.Pos.Y += dt * yRate;

        // Phase animation selection
        string phaseAnim;
        if (t < 0.182) phaseAnim = "landedwingsstoweddoorclose";
        else if (t < 0.455) phaseAnim = "landeddeploywings";
        else if (t < 0.591) phaseAnim = "landedstartingup";
        else if (t < 0.727) phaseAnim = "takingoff";
        else phaseAnim = "takingoff2";

        SetSingleAnim(phaseAnim);

        if (t >= 1.0)
        {
            // Where to hand off depends on what queued this takeoff.
            string next = entity.WatchedAttributes.GetString(AttrTakeoffNextMode, "pilot");
            // Clear the request so the next takeoff isn't sticky
            entity.WatchedAttributes.SetString(AttrTakeoffNextMode, "pilot");
            entity.WatchedAttributes.MarkPathDirty(AttrTakeoffNextMode);

            if (next == ModeFleeing)
            {
                // SetFleeing pre-set direction/speed
                var w = entity.WatchedAttributes;
                w.SetDouble(AttrFlightStarted, entity.World.ElapsedMilliseconds / 1000.0);
                w.MarkPathDirty(AttrFlightStarted);
                SetMode(ModeFleeing);
                return;
            }

            // Default: if we still have a pilot, hand off to pilot
            var pilot = GetActivePilot();
            if (pilot != null)
            {
                SetMode(ModePilot);
                smoothedSpeed = PilotIdleDrift;
            }
            else
            {
                SetMode(ModeFalling);
            }
        }
    }

    private void TickLanding(float dt)
    {
        // Inverse of TickTakeoff

        double startMs = entity.WatchedAttributes.GetDouble(AttrLandingStartedMs, 0);
        double elapsedMs = entity.World.ElapsedMilliseconds - startMs;
        double t = GameMath.Clamp(elapsedMs / (LandingDurationSeconds * 1000.0), 0, 1);

        // Descent: cover remaining altitude (target Y - current Y)
        double ty = entity.WatchedAttributes.GetDouble(AttrTargetY);
        if (t < LandingDescentFraction)
        {
            double dy = ty - entity.Pos.Y;
            double timeRemaining = (LandingDescentFraction - t) * LandingDurationSeconds;
            if (timeRemaining <= dt)
            {
                entity.Pos.Y = ty;
            }
            else
            {
                entity.Pos.Y += dy * dt / timeRemaining;
            }
        }
        else
        {
            // Snap to target Y
            entity.Pos.Y = ty;
        }

        // Phase animation selection
        string phaseAnim;
        if (t < 0.143) phaseAnim = "landingapproach";
        else if (t < 0.429) phaseAnim = "landingfinal";
        else if (t < 0.571) phaseAnim = "landingshutdown";
        else if (t < 0.857) phaseAnim = "landingstowwings";
        else phaseAnim = "landedwingsstoweddooropen";

        SetSingleAnim(phaseAnim);

        if (t >= 1.0)
        {
            // Sequence complete
            SetMode(ModeLanded);
        }
    }

    private void PlayFlightLoop()
    {
        // SetSingleAnim guarantees only "flight" is in the active dict
        SetSingleAnim("flight");
    }

    private void TickApproaching(float dt)
    {
        // Fly toward the target stored in WatchedAttributes
        var w = entity.WatchedAttributes;
        double tx = w.GetDouble(AttrTargetX);
        double ty = w.GetDouble(AttrTargetY);
        double tz = w.GetDouble(AttrTargetZ);

        // Cruise-down target Y is LandingHandoffAltitude
        double cruiseTargetY = ty + LandingHandoffAltitude;

        double dx = tx - entity.Pos.X;
        double dz = tz - entity.Pos.Z;
        double horizDist = System.Math.Sqrt(dx * dx + dz * dz);

        const double ApproachLandRadius = 4.0;
        const double HandoffAltTolerance = 1.5;
        const float ApproachSpeed = 9f;

        if (horizDist < ApproachLandRadius
            && Math.Abs(entity.Pos.Y - cruiseTargetY) < HandoffAltTolerance)
        {
            // Snap horizontal to exact target and kick the landing
            entity.Pos.X = tx;
            entity.Pos.Z = tz;
            w.SetDouble(AttrLandingStartedMs, entity.World.ElapsedMilliseconds);
            w.MarkPathDirty(AttrLandingStartedMs);
            SetMode(ModeLanding);
            return;
        }

        double approachDX = 0;
        double approachDY = 0;
        double approachDZ = 0;

        if (horizDist > 0.001)
        {
            double inv = 1.0 / horizDist;
            double dirX = dx * inv;
            double dirZ = dz * inv;

            approachDX = dirX * ApproachSpeed * dt;
            approachDZ = dirZ * ApproachSpeed * dt;

            entity.Pos.Yaw = (float)Math.Atan2(dirX, dirZ);

            // Y eases toward cruiseTargetY
            double dy = cruiseTargetY - entity.Pos.Y;
            double progressFraction = (ApproachSpeed * dt) / horizDist;
            approachDY = dy * progressFraction;
        }
        else
        {
            // Already over the target horizontally
            double dy = cruiseTargetY - entity.Pos.Y;
            approachDY = dy * Math.Min(1.0, dt * 0.5);
        }

        MoveWithCollision(approachDX, approachDY, approachDZ, dt);

        PlayFlightLoop();
    }

    // Gravity-only drop. No pilot, no anim drive.
    private void TickFalling(float dt)
    {
        fallVelY = Math.Min(FallMaxSpeed, fallVelY + FallGravity * dt);
        double desiredDY = -(double)(fallVelY * dt);

        entity.Pos.Motion.Set(0, -fallVelY, 0);
        Vec3d newPos = new Vec3d();
        collisionTester.NewTick(entity.Pos);
        collisionTester.ApplyTerrainCollision(entity, entity.Pos, dt, ref newPos);

        double actualDY = newPos.Y - entity.Pos.Y;

        entity.Pos.X = newPos.X;
        entity.Pos.Y = newPos.Y;
        entity.Pos.Z = newPos.Z;

        // Zero out motion so the engine doesn't re-apply it on the same tick.
        entity.Pos.Motion.Set(0, 0, 0);

        // If terrain clipped our downward motion, we've hit something
        if (Math.Abs(actualDY - desiredDY) > 0.001)
        {
            fallVelY = 0f;
            SetMode(ModeLanded);
            return;
        }

        // Rainmap surface fallback
        int groundY = entity.World.BlockAccessor.GetRainMapHeightAt(
            new BlockPos((int)entity.Pos.X, 0, (int)entity.Pos.Z)
        );
        if (entity.Pos.Y <= groundY + 1)
        {
            entity.Pos.Y = groundY + 1;
            fallVelY = 0f;
            SetMode(ModeLanded);
            return;
        }

        // Keep wings flapping during the fall
        PlayFlightLoop();
    }

    // Called from the seat's onControls when the pilot presses Sneak
    public static void TriggerAutoLand(Entity entity)
    {
        var w = entity.WatchedAttributes;
        int groundY = entity.World.BlockAccessor.GetRainMapHeightAt(
            new BlockPos((int)entity.Pos.X, 0, (int)entity.Pos.Z)
        );
        // Target slightly above the ground block
        w.SetDouble(AttrTargetX, entity.Pos.X);
        w.SetDouble(AttrTargetY, groundY + 1);
        w.SetDouble(AttrTargetZ, entity.Pos.Z);
        w.SetDouble(AttrLandingStartedMs, entity.World.ElapsedMilliseconds);
        w.SetString(AttrMode, ModeLanding);
        w.MarkPathDirty(AttrTargetX);
        w.MarkPathDirty(AttrTargetY);
        w.MarkPathDirty(AttrTargetZ);
        w.MarkPathDirty(AttrLandingStartedMs);
        w.MarkPathDirty(AttrMode);
    }

    private void TickFleeing(float dt)
    {
        // Pre-set flight direction
        var w = entity.WatchedAttributes;
        double dirX = w.GetDouble(AttrFlightDirX, 0);
        double dirY = w.GetDouble(AttrFlightDirY, 1); // default up
        double dirZ = w.GetDouble(AttrFlightDirZ, 0);
        float speed = w.GetFloat(AttrFlightSpeed, 14f); // fast

        MoveWithCollision(dirX * speed * dt, dirY * speed * dt, dirZ * speed * dt, dt);

        if (dirX != 0 || dirZ != 0)
        {
            entity.Pos.Yaw = (float)Math.Atan2(dirX, dirZ);
        }

        PlayFlightLoop();

        // Despawn once far enough from all players or after timeout.
        double startedSec = w.GetDouble(AttrFlightStarted, 0);
        double nowSec = entity.World.ElapsedMilliseconds / 1000.0;
        if (nowSec - startedSec > FlightLifetimeSeconds || NoPlayerWithinRange())
        {
            entity.Die(EnumDespawnReason.Removed);
        }
    }

    // Spawner helper: configure thopter to approach a target.
    public static void SetApproachTarget(Entity entity, Vec3d target)
    {
        var w = entity.WatchedAttributes;
        w.SetDouble(AttrTargetX, target.X);
        w.SetDouble(AttrTargetY, target.Y);
        w.SetDouble(AttrTargetZ, target.Z);
        w.SetString(AttrMode, ModeApproaching);
        w.MarkPathDirty(AttrTargetX);
        w.MarkPathDirty(AttrTargetY);
        w.MarkPathDirty(AttrTargetZ);
        w.MarkPathDirty(AttrMode);
    }

    // Spawner helper: switch thopter to fleeing in a given direction.
    public static void SetFleeing(Entity entity, Vec3d fleeDirection, float speed = 14f)
    {
        SetFlight(entity, fleeDirection, speed);
        var w = entity.WatchedAttributes;
        // Restart the lifetime clock
        w.SetDouble(AttrFlightStarted, entity.World.ElapsedMilliseconds / 1000.0);
        w.SetString(AttrMode, ModeFleeing);
        w.MarkPathDirty(AttrFlightStarted);
        w.MarkPathDirty(AttrMode);
    }

    // Per-seat Sneak-hold dismount detector. Runs every server tick.
    private void TickDismountTimers(string mode)
    {
        var seatable = entity.GetBehavior<EntityBehaviorSeatable>();
        if (seatable?.Seats == null) return;

        long now = entity.World.ElapsedMilliseconds;
        foreach (var seat in seatable.Seats)
        {
            if (seat == null) continue;
            string seatId = seat.SeatId;
            if (seatId == null) continue;

            var passenger = seat.Passenger as EntityAgent;
            if (passenger == null)
            {
                sneakHoldStartBySeat.Remove(seatId);
                continue;
            }

            bool sneaking = seat.Controls?.Sneak ?? false;
            if (!sneaking)
            {
                sneakHoldStartBySeat.Remove(seatId);
                continue;
            }

            // Pilot seat: only allow dismount while Landed
            if (seat.CanControl && mode != ModeLanded)
            {
                sneakHoldStartBySeat.Remove(seatId);
                continue;
            }

            if (!sneakHoldStartBySeat.TryGetValue(seatId, out long startMs))
            {
                sneakHoldStartBySeat[seatId] = now;
                continue;
            }

            if (now - startMs >= DismountHoldMs)
            {
                sneakHoldStartBySeat.Remove(seatId);
                passenger.TryUnmount();
            }
        }
    }

    private EntityAgent GetActivePilot()
    {
        // Two gates to qualify as an active pilot

        var attachable = entity.GetBehavior<EntityBehaviorAttachable>();
        if (attachable != null)
        {
            var keySlot = attachable.GetSlotConfigFromAPName("ControlPanelAP");
            bool hasKey = keySlot != null
                && !keySlot.Empty
                && keySlot.Itemstack?.Item?.Code?.Domain == "vsdune"
                && keySlot.Itemstack?.Item?.Code?.Path == "ornithopter-controlkey";
            if (!hasKey) return null;
        }

        var seatable = entity.GetBehavior<EntityBehaviorSeatable>();
        if (seatable?.Seats == null) return null;
        foreach (var seat in seatable.Seats)
        {
            if (seat == null) continue;
            if (!seat.CanControl) continue;
            if (seat.Passenger is EntityAgent ea) return ea;
        }
        return null;
    }

    private bool NoPlayerWithinRange()
    {
        double dsq = DespawnIfFartherThan * DespawnIfFartherThan;
        foreach (var p in entity.World.AllOnlinePlayers)
        {
            if (p?.Entity == null) continue;
            double pdx = p.Entity.Pos.X - entity.Pos.X;
            double pdz = p.Entity.Pos.Z - entity.Pos.Z;
            if (pdx * pdx + pdz * pdz <= dsq) return false;
        }
        return true;
    }

    private void SetMode(string mode)
    {
        entity.WatchedAttributes.SetString(AttrMode, mode);
        entity.WatchedAttributes.MarkPathDirty(AttrMode);
    }

    // Public helpers used by the spawners.
    public static void SetFlight(Entity entity, Vec3d direction, float speed)
    {
        var w = entity.WatchedAttributes;
        w.SetDouble(AttrFlightDirX, direction.X);
        w.SetDouble(AttrFlightDirY, direction.Y);
        w.SetDouble(AttrFlightDirZ, direction.Z);
        w.SetFloat(AttrFlightSpeed, speed);
        w.MarkPathDirty(AttrFlightDirX);
        w.MarkPathDirty(AttrFlightDirY);
        w.MarkPathDirty(AttrFlightDirZ);
        w.MarkPathDirty(AttrFlightSpeed);
    }

    public static void SetLanded(Entity entity)
    {
        var w = entity.WatchedAttributes;
        w.SetString(AttrMode, ModeLanded);
        w.MarkPathDirty(AttrMode);
    }

    // Begin the takeoff animation sequence with a request to switch straight to ModeFleeing
    public static void BeginTakeoffThenFlee(Entity entity, Vec3d fleeDirection, float speed = 14f)
    {
        SetFlight(entity, fleeDirection, speed);
        var w = entity.WatchedAttributes;
        w.SetString(AttrTakeoffNextMode, ModeFleeing);
        w.SetDouble(AttrTakeoffStartedMs, entity.World.ElapsedMilliseconds);
        w.SetString(AttrMode, ModeTakeoff);
        w.MarkPathDirty(AttrTakeoffNextMode);
        w.MarkPathDirty(AttrTakeoffStartedMs);
        w.MarkPathDirty(AttrMode);
    }

    public override string PropertyName() => "vsdune.ornithopterflight";
}