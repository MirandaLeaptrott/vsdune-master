using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VsDune;

// Incoming shake packets ramp intensity up; silence lets it decay. Repeated worm proximity pulses build into a crescendo instead of resetting.
public class ScreenShakeClient : ModSystem
{
    private ICoreClientAPI capi;
    private IClientNetworkChannel channel;

    private float currentIntensity;
    private float decayRate;
    private bool active;

    private const float RampUpRate = 6.0f;
    private const float DecayPerSecond = 0.15f;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        channel = api.Network.RegisterChannel(GenVertwormScare.ShakeChannelName)
            .RegisterMessageType<ScreenShakePacket>()
            .SetMessageHandler<ScreenShakePacket>(OnShake);

        api.Event.RegisterGameTickListener(OnTick, 16);
    }

    private void OnShake(ScreenShakePacket packet)
    {
        // Ramp toward incoming intensity if higher; otherwise natural decay handles falloff.
        float target = packet.Intensity;
        if (target > currentIntensity)
        {
            currentIntensity += (target - currentIntensity) * Math.Min(1f, RampUpRate * 0.016f);
        }
        decayRate = DecayPerSecond;
        active = true;
    }

    private void OnTick(float dt)
    {
        if (!active) return;
        if (capi.IsGamePaused) return;
        if (capi.World.Player?.Entity == null) return;

        currentIntensity -= decayRate * dt;
        if (currentIntensity <= 0.001f)
        {
            currentIntensity = 0f;
            active = false;
            return;
        }

        double phase = (capi.InWorldEllapsedMilliseconds / 50.0) % (Math.PI * 2);
        float dPitch = (float)(Math.Sin(phase * 1.7) + Math.Sin(phase * 2.3) * 0.4) * currentIntensity * 0.05f;
        float dYaw = (float)(Math.Cos(phase * 1.9) + Math.Cos(phase * 3.1) * 0.4) * currentIntensity * 0.05f;

        capi.World.Player.Entity.Pos.Pitch += dPitch;
        capi.Input.MousePitch += dPitch;
        capi.Input.MouseYaw += dYaw;

        if (!capi.Input.MouseGrabbed)
        {
            capi.World.Player.Entity.Pos.Yaw = capi.Input.MouseYaw;
        }
    }
}
