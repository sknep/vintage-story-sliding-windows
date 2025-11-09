using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using SlidingWindows.BlockEntityBehaviors;

namespace SlidingWindows
{
    public class RainOnWindowsModSystem : ModSystem
    {
        const int tickMs = 400;
        const int searchRadius = 8; // max distance from player for where sounds may emit
        float coverFactor = 0f;   // -1 = outside, 1 = fully under cover, sort of a debounce

        private ICoreClientAPI capi;
        private WeatherSystemClient weatherSys;
        private RainOnWindowsConfig config;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            LoadConfig(api);
            RainOnWindowsConfig.TryRegisterWithConfigLib(api, config, newCfg => config = newCfg);
            
            weatherSys = api.ModLoader.GetModSystem<WeatherSystemClient>();
            api.Event.RegisterGameTickListener(OnGameTick, tickMs);
        }
        private void LoadConfig(ICoreAPI api)
        {
            // Try load existing config
            config = api.LoadModConfig<RainOnWindowsConfig>("RainOnWindowsConfig.json");

            if (config == null)
            {
                // First run: create defaults and save them
                config = new RainOnWindowsConfig();
                api.StoreModConfig(config, "RainOnWindowsConfig.json");
            }
        }

        /// <summary>
        /// Query the new precipitation system at the players position.
        /// Uses both WeatherSystemBase.GetPrecipitation()
        /// and legacy weatherSys.BlendedWeatherData()
        /// </summary>
        bool WhatsTheWeather(out EnumPrecipitationType precType, out float intensity)
        {
            precType = EnumPrecipitationType.Auto;
            intensity = 0f;

            if (weatherSys == null || !weatherSys.playerChunkLoaded)
            {
                return false;
            }

            var player = capi.World.Player;
            if (player == null || player.Entity == null) return false;

            Vec3d pos = player.Entity.Pos.XYZ;

            // normalizes the game's private method getPrecipNoise() between 0-1
            intensity = GameMath.Clamp(weatherSys.GetPrecipitation(pos), 0f, 1f);

            // 1) Prefer blended snapshot type if it’s set
            var snap = weatherSys.BlendedWeatherData;
            if (snap != null && snap.BlendedPrecType != EnumPrecipitationType.Auto)
            {
                precType = snap.BlendedPrecType;
            }
            else
            {
                // 2) Ask the same slow-access helper the engine uses in WeatherSystemBase.GetPrecipitationState
                if (weatherSys.WeatherDataSlowAccess != null)
                {
                    precType = weatherSys.WeatherDataSlowAccess.GetPrecType(pos);
                }
            }

            // 3) Still Auto? Infer from live climate
            if (precType == EnumPrecipitationType.Auto)
            {
                BlockPos bpos = new BlockPos((int)pos.X, (int)pos.Y, (int)pos.Z);
                var climate = capi.World.BlockAccessor.GetClimateAt(bpos, EnumGetClimateMode.NowValues);

                precType = climate.Temperature <= 0f
                    ? EnumPrecipitationType.Snow
                    : EnumPrecipitationType.Rain;
            }
            return true;
        }

        string WhatsItSoundLike(EnumPrecipitationType type)
        {
            int variant = capi.World.Rand.Next(1, 4);  // generates 1, 2, or 3
            
            switch (type)
            {
                case EnumPrecipitationType.Rain:
                    return $"slidingwindows:sounds/weather/rain-on-glass-{variant}";

                case EnumPrecipitationType.Hail:
                    return $"slidingwindows:sounds/weather/hail-on-glass-{variant}";

                case EnumPrecipitationType.Snow:
                case EnumPrecipitationType.Auto:
                default:
                    return null;
            }
        }


        void OnGameTick(float dt)
        {
            // Config might not be loaded for some weird reason; be defensive.
            if (config == null || !config.EnableRainSoundsOnWindows) return;
            
            var world = capi.World;

            if (!WhatsTheWeather(out var precType, out float intensity)) return;
            
            // is the weather interesting?
            if (intensity < 0.05f || precType == EnumPrecipitationType.Snow) return;

            var soundKey = WhatsItSoundLike(precType);
            if (soundKey == null)
            {
            capi.Logger.Debug("[RainOnGlass] soundKey is null for PrecType={0}", precType);
                return;
            }

            var player = world.Player;
            if (player == null || player.Entity == null) return;

            var pos = player.Entity.Pos;
            var ba = world.BlockAccessor;

            int px = (int)pos.X;
            int pz = (int)pos.Z;
            int py = (int)pos.Y;
            int minY = py - 1;
            int maxY = py + 3;

            // now , are there even any windows nearby

            List<(BlockPos pos, bool isOpen)> candidates = new List<(BlockPos, bool)>();

            for (int x = px - searchRadius; x <= px + searchRadius; x++)
            {
                for (int z = pz - searchRadius; z <= pz + searchRadius; z++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        BlockPos bpos = new BlockPos(x, y, z);
                        Block block = ba.GetBlock(bpos);

                        if (block == null || block.Id == 0) continue;

                        // this mod uses the glass attribute to store the glass color variant
                        string glassAttr = block.Attributes?["glass"]?.AsString(null);
                        bool isGlassy = !string.IsNullOrEmpty(glassAttr);

                        if (!isGlassy && config.EnableRainSoundsOnAllGlass)
                        {
                            // vanilla Material blocktype
                            if (block.BlockMaterial == EnumBlockMaterial.Glass) isGlassy = true;
                        }
                        if (!isGlassy) continue;

                        // windows and doors generally start closed
                        bool isOpen = false;

                        var be = ba.GetBlockEntity(bpos);
                        if (be != null)
                        {
                            // our sliding-window behavior or vanilla door behavior
                            isOpen =
                                be.GetBehavior<BEBehaviorSlidingWindow>()?.Opened ??
                                be.GetBehavior<BEBehaviorDoor>()?.Opened ??
                                false;
                        }
                        candidates.Add((bpos, isOpen));
                    }
                }
            }
            if (candidates.Count == 0) return;

            // now determine whether to make it sound like rain
            // so many scaling factors 

            // player is probably inside, something above player head blocking rain
            double eyeY = pos.Y + player.Entity.LocalEyePos.Y;
            double rainY = ba.GetRainMapHeightAt(px, pz);
            double depthBelowRain = rainY - eyeY;  // >0 means below the rain plane

            // how deep to still hear the rain
            const double maxDepthBelowRainForSound = 8.0;
            bool underCover = depthBelowRain > 0.05 && depthBelowRain <= maxDepthBelowRainForSound;

            // How hard is it raining?

            // increases or decreases hit chance when entering or leaving cover
            // by setting step size for counting up or down in the cooldown
            // for entering (riseSpeed) or leaving (fallSpeed) cover
            const float riseSpeed = 2f;  // ~0.8 per tick → ~1.5–2 ticks to reach +1
            const float fallSpeed = 4f;  // now please
            
            float safeDt = Math.Min(dt, 0.5f);  // cap at 0.5 seconds for sleeps/teleports

            if (underCover)
            {
                // if we were outside (negative), snap back to 0 so entry boost can kick in immediately
                if (coverFactor < 0) coverFactor = 0;
                
                // increment the state of coverFactor to a max of +1
                coverFactor = GameMath.Clamp(coverFactor + riseSpeed * safeDt, -1f, 1f);
            }
            else
            {
                // decrement the state of coverFactor, to a min of -1
                coverFactor = GameMath.Clamp(coverFactor - fallSpeed * safeDt, -1f, 1f);

                // stop calculating rain sounds, we're done here
                if (coverFactor <= -0.99f) return;
            }

            float coverStrength = Math.Abs(coverFactor);  // 0 → 1

            // 1 when player is newly come under cover (coverStrength near 0), moving towards
            // 0 when inside for a while (coverStrength near 1).
            float entryAmount = 1f - coverStrength;
            
            // start at 1, this will 'catch up' on rain hits if the player just walked inside
            // we also use it to make hits less likely to spawn if player just stepped outside
            
            // Stronger initial boost but same "length":
            // 1.6x when we just came in, easing back to 1.0 quickly as coverStrength climbs.
            float entryBias = 1f + entryAmount * 0.6f;          // 1.6 → 1.0

            // If player is outside (negative coverFactor) but still near enough windows for
            // this mod to do some math, fade out in case they're about to dip back in (a cooldown)
            if (coverFactor < 0)
            {
                entryBias = 1f;
                float exitBias = 1f - GameMath.Clamp(-coverFactor * 0.8f, 0f, 0.8f);  // 1 → 0.2
                entryBias *= exitBias;
            }
            // the above is sort of an alternative to a real debounce, in case player walks in and out rapidly

            // Volume scales with precipitation intensity (0–1 from vanilla system) and is affected
            // by how recently player moved into or out of the rain
            float baseVolume = 0.15f * intensity * coverStrength * entryBias;

            // Each game tick (400 ms) runs 2.5 times per second -- this desiredHits thing is still heavily scaled later
            float desiredHitsPerSecond = 0.3f;         // at full intensity & full cover strength
            float ticksPerSecond = 1000f / tickMs;     // 1000 / 400 = 2.5
            float baseHitChance = desiredHitsPerSecond * intensity / ticksPerSecond;

            // ^^ This is the hit chance per second, per window, at full intensity & full cover,
            // scaled by rain intensity. Now, scale by # of windows, cover strength, and entry/exit bias.
            // Bsically that answers "how hard is it raining?". Now, what windows are being rained on?

            // Normalize so that more nearby windows reduce the chance per window, still dependent on 
            // whether you just walked in or not
            float windowFactor = Math.Max(1, candidates.Count);

            // normalize with a curve, not linearly, so that even a few windows still get sounds.
            // Assume 8 windows = 1.0 normal baseline, limits at 0.25x and 2x for extremes
            // only 2 windows in range? 2x their hit chance. 32 or more? basically 1/4th the hit chance

            float normalization = GameMath.Clamp((float)Math.Pow(8f / windowFactor, 1.3f), 0.25f, 2f);
            baseHitChance *= normalization * coverStrength * entryBias;

            // which windows get rain sounds?
            foreach (var (bpos, isOpen) in candidates)
            {
                // whether opened windows are more or less likely to register -- this nerfs closed windows
                // because opening windows is loud
                float chance = baseHitChance * (isOpen ? 1.3f : 1.0f);

                if (capi.World.Rand.NextDouble() > chance) continue;

                // location for sound to play at
                double sx = bpos.X + 0.5;
                double sy = bpos.Y + 0.0; // top of the block that was hit
                double sz = bpos.Z + 0.5;

                // opened windows sound louder and a little brighter, jitter the pitch/vol so its not a metronome
                float opennessFactor = isOpen ? 1f : 0.9f;
                float jitterVol = 0.8f + (float)capi.World.Rand.NextDouble() * 0.4f;
                float jitterPitch = (float)(capi.World.Rand.NextDouble() * 0.1f - 0.05f);
                float pitch = opennessFactor + jitterPitch;
                float volume = baseVolume * opennessFactor * jitterVol;
                float playerScale = GameMath.Clamp(config.VolumeScale, 0f, 2f);
                // finally, apply player's volumee preference
                volume *= playerScale;

                world.PlaySoundAt(
                    new AssetLocation(soundKey),
                    sx, sy, sz,
                    null,
                    EnumSoundType.Ambient, // so players can adjust the volume in the related sound setting
                    pitch,
                    16f, // range at which to fall off ... maybe scale this? check after testing w/ multiplayer
                    volume
                );
            }

        }
    }
}
