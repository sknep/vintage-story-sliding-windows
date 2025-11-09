using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SlidingWindows
{
    public class RainOnWindowsConfig
    {
        /// <summary>
        /// Master switch: if false, the mod does nothing.
        /// </summary>
        public bool EnableRainSoundsOnWindows { get; set; } = true;

        /// <summary>
        /// If true, treat ANY glass-material block as a candidate,
        /// not just blocks with the "glass" attribute.
        /// </summary>
        public bool EnableRainSoundsOnAllGlass { get; set; } = false;

        /// <summary>
        /// Extra volume multiplier (1.0 = default, range is 0-2). Players can nerf or boost.
        /// </summary>
        public float VolumeScale { get; set; } = 1f;

        public static void TryRegisterWithConfigLib(
            ICoreClientAPI api,
            RainOnWindowsConfig currentConfig,
            Action<RainOnWindowsConfig> applyNewConfig
        )
        {
            var configLib = api.ModLoader.GetModSystem("ConfigLib");
            if (configLib == null) return;

            try
            {
                var method = configLib.GetType().GetMethod("RegisterConfig");
                if (method == null) return;

                method.Invoke(configLib, new object[]
                {
                    "RainOnWindowsConfig",
                    currentConfig,
                    (Action<RainOnWindowsConfig>)(newCfg =>
                    {
                        // Hand the new config back to whoever called us
                        applyNewConfig(newCfg);
                        api.StoreModConfig(newCfg, "RainOnWindowsConfig.json");
                    }),
                    "Rain on Windows"
                });

                api.Logger.Notification("[RainOnWindows] Registered with Config UI library.");
            }
            catch (Exception e)
            {
                api.Logger.Warning("[RainOnWindows] Config UI registration failed: {0}", e);
            }
        }

    }
}
