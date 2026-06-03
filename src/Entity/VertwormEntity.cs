using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VsDune;


public class VertwormEntity : EntityAgent
{
    public override double FrustumSphereRadius => 30.0;

    public override bool AlwaysActive => true;

    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
      
        requirePosesOnServer = true;
        base.Initialize(properties, api, InChunkIndex3d);
    }
}
