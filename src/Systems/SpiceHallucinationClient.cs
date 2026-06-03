using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VsDune;

public class SpiceHallucinationClient : ModSystem
{
    private ICoreClientAPI capi;

    private const float HallucinationOnset = 0.55f;
    private const float MaxSaturationContribution = 1.0f;

    // 0.985 per 250ms tick brings 1.5 down by ~63% over ~30s: the
    // "eat spice and ride out the trip" window.
    private const float DecayPerTick = 0.985f;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        api.Event.RegisterGameTickListener(OnTick, 250);
    }

    private void OnTick(float dt)
    {
        if (capi.World.Player?.Entity == null) return;
        if (capi.World.Player.WorldData?.CurrentGameMode == EnumGameMode.Spectator) return;

        // Saturation is stored as Double server side (so EdenInstinct's
        // GetDouble redirect lands on the right value). Cast for math.
        float saturation = (float)capi.World.Player.Entity.WatchedAttributes.GetDouble(SpiceSaturationSystem.AttrPath, 0.0);

        float saturationFloor;
        if (saturation <= HallucinationOnset)
        {
            saturationFloor = 0f;
        }
        else
        {
            float t = (saturation - HallucinationOnset) / (1f - HallucinationOnset);
            saturationFloor = GameMath.Clamp(t, 0f, 1f) * MaxSaturationContribution;
        }

        var attrs = capi.World.Player.Entity.WatchedAttributes;
        float current = attrs.GetFloat("psychedelic", 0f);
        float target = System.Math.Max(saturationFloor, current * DecayPerTick);
        target = GameMath.Clamp(target, 0f, 2f);

        if (System.Math.Abs(current - target) > 0.005f)
        {
            attrs.SetFloat("psychedelic", target);
        }
    }
}
