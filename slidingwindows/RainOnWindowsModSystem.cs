using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.API.Common.Entities;
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
        public struct PlayerAt
        {
            public IPlayer Player;
            public EntityPos Pos;
            public IBlockAccessor BlockAccessor;
            public int Px;
            public int Py;
            public int Pz;
            public int MinY;
            public int MaxY;
        }

        bool WheresThePlayer(IPlayer player, IBlockAccessor ba, out PlayerAt here)
        {
            here = default;

            if (player.Entity == null || ba == null)
            {
                return false;
            }

            var pos = player.Entity.Pos;

            int px = (int)pos.X;
            int py = (int)pos.Y;
            int pz = (int)pos.Z;

            here = new PlayerAt
            {
                Player = player,
                Pos = pos,
                BlockAccessor = ba,
                Px = px,
                Py = py,
                Pz = pz,
                MinY = py - 1,
                MaxY = py + 3
            };

            return true;
        }

        /// <summary>
        /// Query the new precipitation system at the players position.
        /// Uses both WeatherSystemBase.GetPrecipitation()
        /// and legacy weatherSys.BlendedWeatherData()
        /// </summary>
        bool WhatsTheWeatherAt(Vec3d pos, out EnumPrecipitationType precType, out float intensity)
        {
            precType = EnumPrecipitationType.Auto;
            intensity = 0f;

            if (weatherSys == null || !weatherSys.playerChunkLoaded)
            {
                return false;
            }

            // normalizes the game's private method getPrecipNoise() between 0-1
            intensity = GameMath.Clamp(weatherSys.GetPrecipitation(pos), 0f, 1f);

            // precType is sometimes "auto" even when its raining so...

            // Prefer blended snapshot type if it’s set
            var snap = weatherSys.BlendedWeatherData;
            if (snap != null && snap.BlendedPrecType != EnumPrecipitationType.Auto)
            {
                precType = snap.BlendedPrecType;
            }
            else
            {
                // Ask the same slow-access helper the engine uses in WeatherSystemBase.GetPrecipitationState
                if (weatherSys.WeatherDataSlowAccess != null)
                {
                    precType = weatherSys.WeatherDataSlowAccess.GetPrecType(pos);
                }
            }

            // Still Auto? Infer from live climate
            if (precType == EnumPrecipitationType.Auto)
            {
                BlockPos bpos = new BlockPos((int)pos.X, (int)pos.Y, (int)pos.Z);
                // old way if VS grumbles about not being dimension aware:
                // BlockPos bpos = new BlockPos(0);
                // bpos.Set((int)pos.X, (int)pos.Y, (int)pos.Z);

                var climate = capi.World.BlockAccessor.GetClimateAt(bpos, EnumGetClimateMode.NowValues);

                precType = climate.Temperature <= 0f
                    ? EnumPrecipitationType.Snow
                    : EnumPrecipitationType.Rain;
            }

            // if we wouldn't even call this rain, then exit
            if (intensity < 0.1f || precType == EnumPrecipitationType.Snow) return false;

            // bump up sprinkles so that we can still hear them
            const float intensityFloor = 0.25f;
            intensity = Math.Max(intensity, intensityFloor);

            return true;
        }

        bool WhatsItSoundLike(EnumPrecipitationType type, out string soundKey)
        {
            int variant = capi.World.Rand.Next(1, 4);  // generates 1, 2, or 3

            soundKey = type switch
            {
                EnumPrecipitationType.Rain => $"slidingwindows:sounds/weather/rain-on-glass-{variant}",
                EnumPrecipitationType.Hail => $"slidingwindows:sounds/weather/hail-on-glass-{variant}",
                _ => null
            };            
            return soundKey != null;
        }

        bool AnyWindowsNearby(PlayerAt here, out List<(BlockPos pos, bool isOpen)> candidates)
        {
            candidates = new List<(BlockPos, bool)>();
            var ba = here.BlockAccessor;
            BlockPos thisWindowHere = new BlockPos(0);

            for (int x = here.Px - searchRadius; x <= here.Px + searchRadius; x++)
            {
                for (int z = here.Pz - searchRadius; z <= here.Pz + searchRadius; z++)
                {
                    for (int y = here.MinY; y <= here.MaxY; y++)
                    {
                        thisWindowHere.Set(x, y, z); 
                        Block block = ba.GetBlock(thisWindowHere);

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

                        var be = ba.GetBlockEntity(thisWindowHere);
                        if (be != null)
                        {
                            // our sliding-window behavior or vanilla door behavior
                            isOpen =
                                be.GetBehavior<BEBehaviorSlidingWindow>()?.Opened ??
                                be.GetBehavior<BEBehaviorDoor>()?.Opened ??
                                false;
                        }
                        candidates.Add((thisWindowHere.Copy(), isOpen));
                    }
                }
            }
            return candidates.Count > 0;
        }

        bool IsItRainingOverhead(
            float currentCover,
            PlayerAt here,
            float dt,
            out float newCover,
            out float coverStrength,
            out float entryBias)
        {
            var player = here.Player;
            var ba = here.BlockAccessor;
            // the player is probably inside if there's something above player head blocking rain
            double eyeY = here.Pos.Y + player.Entity.LocalEyePos.Y;
            double rainY = ba.GetRainMapHeightAt(here.Px, here.Pz);
            double depthBelowRain = rainY - eyeY;  // >0 means below the rain plane

            // how deep to still hear the rain on the surface
            const double maxDepthBelowRainForSound = 8.0;
            bool underCover = depthBelowRain > 0.05 && depthBelowRain <= maxDepthBelowRainForSound;

            // increases or decreases hit chance when entering or leaving cover
            // by setting step size for counting up or down in the cooldown
            // for entering (riseSpeed) or leaving (fallSpeed) cover
            const float riseSpeed = 2f;  // ~0.8 per tick → ~1.5–2 ticks to reach +1
            const float fallSpeed = 4f;  // now please
            
            float safeDt = Math.Min(dt, 0.5f);  // cap at 0.5 seconds for sleeps/teleports
            float coverFactor = currentCover;

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
                if (coverFactor <= -0.99f)
                {
                    newCover = coverFactor;
                    coverStrength = 1f;
                    entryBias = 1f;
                    return false;
                }
            }

            coverStrength = Math.Abs(coverFactor);  // 0 → 1

            // 1 when player is newly come under cover (coverStrength near 0), moving towards
            // 0 when inside for a while (coverStrength near 1).
            float entryAmount = 1f - coverStrength;
            
            // start at 1, this will 'catch up' on rain hits if the player just walked inside
            // we also use it to make hits less likely to spawn if player just stepped outside
            
            // Stronger initial boost but same "length":
            // 1.6x when we just came in, easing back to 1.0 quickly as coverStrength climbs.
            entryBias = 1f + entryAmount * 0.6f;          // 1.6 → 1.0

            // If player is outside (negative coverFactor) but still near enough windows for
            // this mod to do some math, fade out in case they're about to dip back in (a cooldown)
            if (coverFactor < 0)
            {
                entryBias = 1f;
                float exitBias = 1f - GameMath.Clamp(-coverFactor * 0.8f, 0f, 0.8f);  // 1 → 0.2
                entryBias *= exitBias;
            }
            // the above is sort of an alternative to a real debounce, in case player walks in and out rapidly
            newCover = coverFactor;
            return true;
        }


        float HowLoudIsTheRain(float intensity, float coverStrength, float entryBias)
        {
            // Volume scales with precipitation intensity (0–1 from vanilla system) and is affected
            // by how recently player moved into or out of the rain
            return 1f * intensity * coverStrength * entryBias;
        }

        float HowHardIsItRaining(
            float intensity, float coverStrength, float entryBias, int windowCount)
        {
            // Each game tick (400 ms) runs 2.5 times per second -- this desiredHits thing is still heavily scaled later
            float desiredHitsPerSecond = 0.5f;         // at full intensity & full cover strength
            float ticksPerSecond = 1000f / tickMs;     // 1000 / 400 = 2.5

            float baseHitChance = desiredHitsPerSecond * intensity / ticksPerSecond;

            // ^^ This is the hit chance per second, per window, at full intensity & full cover,
            // scaled by rain intensity. Now, scale by # of windows, cover strength, and entry/exit bias.
            // Bsically that answers "how hard is it raining?". Now, what windows are being rained on?

            // Normalize so that more nearby windows reduce the chance per window, still dependent on 
            // whether you just walked in or not
            float windowFactor = Math.Max(1, windowCount);

            // normalize with a curve, not linearly, so that even a few windows still get sounds.
            // Assume 8 windows = 1.0 normal baseline, limits at 0.25x and 2x for extremes
            // only 2 windows in range? 2.5x their hit chance. 32 or more? basically 1/4th the hit chance

            float normalization = GameMath.Clamp((float)Math.Pow(8f / windowFactor, 1.3f), 0.25f, 2.5f);
            return baseHitChance * normalization * coverStrength * entryBias;
        }


        void IsItRainingOnThisWindow(
            IClientWorldAccessor world,
            string soundKey,
            BlockPos bpos,
            bool isOpen,
            float baseVolume,
            float baseHitChance,
            float playerScale)
        {
            var rand = world.Rand;
            if (rand.NextDouble() > baseHitChance) return;

            double sx = bpos.X + 0.5;
            double sy = bpos.Y + 1.0;  // play from top of the block
            double sz = bpos.Z + 0.5;

            // open windows sound louder and brighter
            float opennessFactor = isOpen ? 1f : 0.9f;
            float jitterVol = 0.8f + (float)rand.NextDouble() * 0.4f;
            float jitterPitch = (float)(rand.NextDouble() * 0.1f - 0.05f);
            float pitch = opennessFactor + jitterPitch;

            float volume = baseVolume * opennessFactor * jitterVol;
            volume = GameMath.Clamp(volume, 0f, 1.5f);
            volume *= playerScale;

            world.PlaySoundAt(
                new AssetLocation(soundKey),
                sx, sy, sz,
                null,
                EnumSoundType.Weather,
                pitch,
                24f,
                volume
            );
        }

        void OnGameTick(float dt)
        {
            // this might be a little too defensive but whatever
            if (config == null || !config.EnableRainSoundsOnWindows) return;
            
            var world = capi.World;
            if (world == null) return;

            var player = world.Player;
            if (player == null || player.Entity == null) return;
            
            var ba = world.BlockAccessor;
            if (!WheresThePlayer(player, ba, out var here)) return;
            if (!WhatsTheWeatherAt(here.Pos.XYZ, out var precType, out float intensity)) return;
            if (!WhatsItSoundLike(precType, out var soundKey)) return;
            if (!IsItRainingOverhead(coverFactor, here, dt,
                out coverFactor, out var coverStrength, out var entryBias)) return;
            if (!AnyWindowsNearby(here, out var candidates)) return;
            float baseVolume = HowLoudIsTheRain(intensity, coverStrength, entryBias);
            float baseHitChance = HowHardIsItRaining(intensity, coverStrength, entryBias, candidates.Count);
            float playerScale = GameMath.Clamp(config.VolumeScale, 0f, 2f);
            foreach (var (bpos, isOpen) in candidates)
            {
                IsItRainingOnThisWindow(world, soundKey, bpos, isOpen, baseVolume, baseHitChance, playerScale);
            }

        }
    }
}
