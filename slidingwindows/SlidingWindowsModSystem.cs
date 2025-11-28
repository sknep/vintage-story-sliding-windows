using Vintagestory.API.Common;
using SlidingWindows.BlockBehaviors;
using SlidingWindows.BlockEntityBehaviors;

namespace SlidingWindows
{
    public class SlidingWindowsModSystem : ModSystem
    {
        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            api.RegisterBlockBehaviorClass(
                "SlidingWindow", typeof(BlockBehaviorSlidingWindow)
            );
            api.RegisterBlockBehaviorClass(
                "GlassDoor", typeof(BlockBehaviorGlassDoor)
            );
            api.RegisterBlockBehaviorClass(
                "GlassTrapdoor", typeof(BlockBehaviorGlassTrapdoor)
            );

            api.RegisterBlockEntityBehaviorClass(
                "SlidingWindowBE", typeof(BEBehaviorSlidingWindow)
            );

            api.RegisterBlockEntityBehaviorClass(
                "GlassDoorBE", typeof(BEBehaviorGlassDoor)
            );

            api.RegisterBlockEntityBehaviorClass(
                "GlassTrapdoorBE", typeof(BEBehaviorGlassTrapdoor)
            );
        }
    }
}
