using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using SlidingWindows.BlockBehaviors;
using Vintagestory.GameContent;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using System.Linq;
using System.Diagnostics;
using Vintagestory.API.Util;

#nullable disable

namespace SlidingWindows.BlockEntityBehaviors
{
    /// <summary>
    /// Block entity behavior for sliding windows, structurally aligned with vanilla BEBehaviorDoor
    /// but using sliding animation instead of swing.
    /// </summary>
    public class BEBehaviorSlidingWindow : BEBehaviorAnimatable, IInteractable, IRotatable
    {
        public float RotateYRad;

        protected bool isOpen;
        private string lastStartedAnimCode;
        protected bool invertHandles;
        protected MeshData closedMesh;
        private MeshData openedMesh;

        // Open/closed collision + selection boxes for this controller.
        // For sliding windows we treat Block.CollisionBoxes (from the shape file)
        // as [frame, sashClosed, sashOpen] and build two explicit box sets
        // instead of rotating shapes like vanilla does, because our shapes are more complex.
        protected Cuboidf[] boxesClosed, boxesOpened;

        public BlockFacing windowFacing { get { return BlockFacing.HorizontalFromYaw(RotateYRad); } }

        public BlockBehaviorSlidingWindow windowBh;

        public Cuboidf[] ColSelBoxes => isOpen ? boxesOpened : boxesClosed;

        public bool Opened => isOpen;
        public bool InvertHandles => invertHandles;

        protected Vec3i leftWindowOffset;
        protected Vec3i rightWindowOffset;
        private bool _tickRegistered;
        private long _tickId;

        public BEBehaviorSlidingWindow LeftWindow
        {
            get
            {
                if (leftWindowOffset == null) return null;
                var be = BlockBehaviorSlidingWindow.getSlidingWindowAt(Api.World, Pos.AddCopy(leftWindowOffset));
                if (be == null) leftWindowOffset = null;
                return be;
            }
            protected set
            {
                leftWindowOffset = value == null ? null : value.Pos.SubCopy(Pos).ToVec3i();
            }
        }

        public BEBehaviorSlidingWindow RightWindow
        {
            get
            {
                if (rightWindowOffset == null) return null;
                var be = BlockBehaviorSlidingWindow.getSlidingWindowAt(Api.World, Pos.AddCopy(rightWindowOffset));
                if (be == null) rightWindowOffset = null;
                return be;
            }
            protected set
            {
                rightWindowOffset = value == null ? null : value.Pos.SubCopy(Pos).ToVec3i();
            }
        }

        public BEBehaviorSlidingWindow(BlockEntity blockentity) : base(blockentity)
        {
            windowBh = Block.GetBehavior<BlockBehaviorSlidingWindow>();
        }

        Stopwatch LastEventTime;

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);

            Api.Logger.Debug(">>> [Before first tick] Initialize Called");
            LastEventTime = new Stopwatch();
            if (!_tickRegistered) {
                Api.Event.RegisterGameTickListener(OnGameTick, 13);
                Api.Event.RegisterEventBusListener(OnEventBusEvent, 10000.0);
                _tickRegistered = true;
            }
            SetupMeshAndBoxes(false);

            if (isOpen && animUtil != null && !animUtil.activeAnimationsByAnimCode.ContainsKey("opening"))
            {
                ToggleWindowSash(true);
            }
        }

        private void OnEventBusEvent(string eventName, ref EnumHandling handling, IAttribute data)
        {
            Api.Logger.Debug($">>> Got event: {eventName}, {data}");
        }

        private void OnGameTick(float dt)
        {
            _tickId++;

            var animator = animUtil.animator;
            if (animator == null) return;

            // Who owns rendering this frame?
            int activeCount = animUtil?.animator?.ActiveAnimationCount ?? 0;
            bool latched = animUtil?.renderer?.ShouldRender == true;
            // string owner = latched ? "ENTITY (animated)" : "CHUNK (static)";
            // Api.Logger.Debug($">>> [TICK #{_tickId}] Owner={owner} ShouldRender={latched} active={activeCount} last={lastStartedAnimCode ?? "-"} pos={Pos}");
            
            // If we have a clip in flight, keep the animated renderer latched ON every tick.
            // This prevents a one-tick unlatch -> static blip.
            if (lastStartedAnimCode != null && animUtil?.renderer != null && animUtil.renderer.ShouldRender == false)
            {
                animUtil.renderer.ShouldRender = true;
                // no MarkDirty needed; entity renderer stays in charge this frame
            }

            var state = lastStartedAnimCode != null ? animUtil.animator?.GetAnimationState(lastStartedAnimCode) : null;

                    
            // 1) Strong “end of clip” finish: progress at end means we finish NOW, regardless of Running/Active.
            if (state != null && state.AnimProgress >= 0.99f)
            {
                bool opening = lastStartedAnimCode == "opening";
                animUtil.StopAnimation(lastStartedAnimCode);
                animUtil.StopAnimation("opening");
                animUtil.StopAnimation("closing");
                if (animUtil?.renderer != null) animUtil.renderer.ShouldRender = false;

                isOpen = opening;                    // flip to match the clip we played
                var finishedCode = lastStartedAnimCode;
                lastStartedAnimCode = null;

                Blockentity.MarkDirty(true);
                Api.Logger.Debug($">>> [TICK #{_tickId}] Finished animation \"{finishedCode}\" -> isOpen={isOpen} (unlatched; static will draw) for block at {Pos}");
                return;
            }

            // If there is no current state at all, fall back to static and clear latch.
            if (lastStartedAnimCode == null || state == null)
            {
                if (animUtil?.renderer != null) animUtil.renderer.ShouldRender = false;
                // Api.Logger.Debug($">>> [TICK #{_tickId}] (This tick should be the very next tick after finish) Hold static at {Pos} (no active clip)");
                return;
            }

            if (lastStartedAnimCode == null)
            {
                if (animUtil?.renderer != null) animUtil.renderer.ShouldRender = false;
                // Api.Logger.Debug($">>> [TICK #{_tickId}] Idle static (state missing) for block at {Pos}");
                return;
            }

            // 3) (Optional safety) If the animator went idle (no active clips) while latched, fail open to static.
            // if ((animUtil?.animator?.ActiveAnimationCount ?? 0) == 0 && animUtil?.renderer?.ShouldRender == true)
            // {
            //     animUtil.renderer.ShouldRender = false;
            //     Blockentity.MarkDirty(true);
            //     Api.Logger.Debug($"[Latch->Static] Animator idle; static takes over at {Pos}");
            // }

            // 4) Debug current state (harmless; after finish checks to avoid logging stale)
            Api.Logger.Debug($">>> [TICK #{_tickId}] SAMPLE anim={state.Animation?.Code} prog={state.AnimProgress:0.###} ease={state.EasingFactor:0.###} running={state.Running} active={state.Active}");
            
        }


        public Vec3i getAdjacentOffset(int right, int back = 0, int up = 0)
        {
            return getAdjacentOffset(right, back, up, RotateYRad, invertHandles);
        }

        public static Vec3i getAdjacentOffset(int right, int back, int up, float rotateYRad, bool invertHandles)
        {
            if (invertHandles) right = -right;
            return new Vec3i(
                right * (int)Math.Round(Math.Sin(rotateYRad + GameMath.PIHALF)) - back * (int)Math.Round(Math.Sin(rotateYRad)),
                up,
                right * (int)Math.Round(Math.Cos(rotateYRad + GameMath.PIHALF)) - back * (int)Math.Round(Math.Cos(rotateYRad))
            );
        }

        internal void SetupMeshAndBoxes(bool initialSetup)
        {
            if (LeftWindow == this || RightWindow == this) return;

            // Early out if we know this window is ace
            if (initialSetup && windowBh.pairable)
            {
                if (BlockBehaviorSlidingWindow.HasCombinableLeftWindow(Api.World, RotateYRad, Pos, windowBh.width, out BEBehaviorSlidingWindow otherWindow, out int offset))
                {
                    if (otherWindow.LeftWindow == null && otherWindow.RightWindow == null && otherWindow.windowFacing == windowFacing)
                    {
                        if (Api.Side == EnumAppSide.Server)
                        {
                            if (otherWindow.invertHandles)
                            {
                                if (otherWindow.windowBh.width > 1)
                                {
                                    Api.World.BlockAccessor.SetBlock(0, otherWindow.Pos);
                                    BlockPos leftWindowPos = Pos.AddCopy(windowFacing.GetCW(), (otherWindow.windowBh.width + windowBh.width - 1));
                                    Api.World.BlockAccessor.SetBlock(otherWindow.Block.Id, leftWindowPos);
                                    otherWindow = Block.GetBEBehavior<BEBehaviorSlidingWindow>(leftWindowPos);
                                    otherWindow.RotateYRad = RotateYRad;
                                    otherWindow.windowBh.placeMultiblockParts(Api.World, leftWindowPos);
                                    LeftWindow = otherWindow;
                                    LeftWindow.RightWindow = this;
                                    LeftWindow.SetupMeshAndBoxes(true);
                                }
                                else
                                {
                                    otherWindow.invertHandles = false;
                                    LeftWindow = otherWindow;
                                    LeftWindow.RightWindow = this;
                                    LeftWindow.Blockentity.MarkDirty(true);
                                    LeftWindow.SetupMeshAndBoxes(false);
                                }
                            }
                            else
                            {
                                LeftWindow = otherWindow;
                                LeftWindow.RightWindow = this;
                            }
                        }

                        invertHandles = true;
                        Blockentity.MarkDirty(true);
                    }
                }

                if (BlockBehaviorSlidingWindow.HasCombinableRightWindow(Api.World, RotateYRad, Pos, windowBh.width, out otherWindow, out offset))
                {
                    if (otherWindow.LeftWindow == null && otherWindow.RightWindow == null && otherWindow.windowFacing == windowFacing)
                    {
                        if (Api.Side == EnumAppSide.Server)
                        {
                            if (!otherWindow.invertHandles)
                            {
                                if (otherWindow.windowBh.width > 1)
                                {
                                    Api.World.BlockAccessor.SetBlock(0, otherWindow.Pos);
                                    BlockPos rightWindowPos = Pos.AddCopy(windowFacing.GetCCW(), (otherWindow.windowBh.width + windowBh.width - 1));
                                    Api.World.BlockAccessor.SetBlock(otherWindow.Block.Id, rightWindowPos);
                                    otherWindow = Block.GetBEBehavior<BEBehaviorSlidingWindow>(rightWindowPos);
                                    otherWindow.RotateYRad = RotateYRad;
                                    otherWindow.invertHandles = true;
                                    otherWindow.windowBh.placeMultiblockParts(Api.World, rightWindowPos);
                                    RightWindow = otherWindow;
                                    RightWindow.LeftWindow = this;
                                    otherWindow.SetupMeshAndBoxes(true);
                                }
                                else
                                {
                                    otherWindow.invertHandles = true;
                                    RightWindow = otherWindow;
                                    RightWindow.LeftWindow = this;
                                    RightWindow.Blockentity.MarkDirty(true);
                                    RightWindow.SetupMeshAndBoxes(false);
                                }
                            }
                            else
                            {
                                RightWindow = otherWindow;
                                RightWindow.LeftWindow = this;
                            }
                        }
                    }
                }
            }
            else if (!windowBh.pairable && initialSetup)
            {
                //  you are a lonely window
                LeftWindow = null;
                RightWindow = null;
                invertHandles = false;
                Blockentity.MarkDirty(true);
            }

            if (Api.Side == EnumAppSide.Client)
            {
                if (windowBh.animatableOrigMesh == null)
                {
                    string animkey = Block.Shape.ToString();

                    Api.Logger.Debug($">>> creating animatableOrgMesh with animkey={animkey}");
                    windowBh.animatableOrigMesh = animUtil.CreateMesh(animkey, null, out Shape shape, null);

                    if (Block.Attributes["staticShapeForOpened"] != null)
                    {
                        var shapeCode = Block.Attributes["staticShapeForOpened"].AsString();
                        var shapeLoc = AssetLocation.Create(shapeCode);
                        Shape openShape;
                        
                        try
                        {
                            openShape = Shape.TryGet(Api, shapeLoc);
                            Api.Logger.Debug($">>> creating openMesh with animkey={animkey}-open");
                            // openShape = Api.Assets.TryGet(shapeLoc)?.ToObject<Shape>();
                            windowBh.openMesh = animUtil.CreateMesh(animkey + "-open", openShape, out openShape, null);

                        }
                        catch (Exception e)
                        {
                            Api.Logger.Error($"[SlidingWindows] Failed loading shape '{shapeLoc}': {e}");
                        }
                    }

                    windowBh.animatableShape = shape;
                    windowBh.animatableDictKey = animkey;
                }

                if (windowBh.animatableOrigMesh != null && windowBh.animatableShape != null)
                {            
                    var capi = Api as ICoreClientAPI;
                    var texSource = capi?.Tesselator?.GetTextureSource(Block);
                    animUtil.InitializeAnimator(windowBh.animatableDictKey ?? "slidingwindows:fallback", windowBh.animatableShape, texSource);
                    Api.Logger.Debug($">>> Initialized the animator for dictKey {windowBh.animatableDictKey}");
                    EnsureRendererRegistered();
                    UpdateMeshAndAnimations();
                }
            }

            UpdateHitBoxes();
        }

        private bool _rendererRegistered; 
        private void EnsureRendererRegistered()
        {
            if (Api?.Side != EnumAppSide.Client) return;
            if (animUtil?.renderer == null || _rendererRegistered) return;

            var capi = (ICoreClientAPI)Api;
            capi.Event.RegisterRenderer(animUtil.renderer, EnumRenderStage.OIT, "slidingwindow");
            _rendererRegistered = true;
            Api.Logger.Debug($">>> Registered renderer for block at {Pos}");
            animUtil.renderer.backfaceCulling = false;
            // animUtil.renderer.ShouldRender = false;
        }


        protected virtual void UpdateMeshAndAnimations()
        {
            closedMesh = windowBh.animatableOrigMesh.Clone();
            if (windowBh.openMesh != null)
            {
                openedMesh = windowBh.openMesh.Clone();
            }
            // openedMesh = (windowBh.openMesh ?? windowBh.animatableOrigMesh).Clone(); maybe a fallback? 

            float rot = RotateYRad;

            if (invertHandles) rot = -rot;

            if (rot != 0f)
            {
                closedMesh = closedMesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0, rot, 0);
                openedMesh = openedMesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0, rot, 0);
            }

            if (invertHandles)
            {
                Matrixf matf = new Matrixf();
                matf.Translate(0.5f, 0.5f, 0.5f)
                    .Scale(-1f, 1f, 1f)
                    .Translate(-0.5f, -0.5f, -0.5f);

                closedMesh.MatrixTransform(matf.Values);
                openedMesh.MatrixTransform(matf.Values);

                if (Api.Side == EnumAppSide.Client && animUtil?.renderer != null)
                {
                    animUtil.renderer.backfaceCulling = false;
                    animUtil.renderer.ScaleX = -1f;
                }
            }
            else
            {
                if (Api.Side == EnumAppSide.Client && animUtil?.renderer != null)
                {
                    animUtil.renderer.backfaceCulling = true;
                    animUtil.renderer.ScaleX = 1f;
                }
            }

            // Make animator follow the same yaw so the "opening" animation always slides
            // along the local X axis, regardless of world orientation.
            if (Api.Side == EnumAppSide.Client && animUtil?.renderer != null)
            {
                animUtil.renderer.rotationDeg.Y = rot * GameMath.RAD2DEG;
            }
        }
        protected virtual void UpdateHitBoxes()
        {
            // Expecting Block.CollisionBoxes to contain:
            // [0] = stationary frame
            // [1] = moving sash (closed)
            // [2] = moving sash (open)
            var all = Block.CollisionBoxes ?? Array.Empty<Cuboidf>();

            Cuboidf stationary   = all.Length > 0 ? all[0].Clone() : new Cuboidf(0, 0, 0, 0, 0, 0);
            Cuboidf movingClosed = all.Length > 1 ? all[1].Clone() : stationary.Clone();
            Cuboidf movingOpen   = all.Length > 2 ? all[2].Clone() : movingClosed.Clone();

            var localClosed = new[] { stationary.Clone(), movingClosed.Clone() };
            var localOpened = new[] { stationary.Clone(), movingOpen.Clone() };

            // X center stays at the controller block center (0.5) so the mesh and boxes
            // always pivot around the controller column. This expects the JSON shapes
            // and collision to also start from the controller block as origin.
            float mirrorCenterX = 0.5f;

            if (invertHandles)
            {
                MirrorAroundCenterX(localClosed, mirrorCenterX);
                MirrorAroundCenterX(localOpened, mirrorCenterX);
            }

            // For yaw rotation, only X/Z of the origin matter. We keep X=0.5 so
            // the shape stays anchored on the controller column. Y is set to the
            // vertical center for symmetry; it does not affect yaw.
            double centerX = 0.5;
            double centerY = (windowBh?.height > 0 ? windowBh.height : 1) / 2.0;

            var origin = new Vec3d(centerX, centerY, 0.5);
            float degY = RotateYRad * GameMath.RAD2DEG;

            boxesClosed = new Cuboidf[localClosed.Length];
            boxesOpened = new Cuboidf[localOpened.Length];

            for (int i = 0; i < localClosed.Length; i++)
            {
                boxesClosed[i] = localClosed[i].RotatedCopy(0, degY, 0, origin);
                boxesOpened[i] = localOpened[i].RotatedCopy(0, degY, 0, origin);
            }

            // - Vanilla implicitly stacks its 1×2 column per block; we do not.
            // - Instead, JSON collisionboxes already describe the full multi-block
            //   footprint (0..2 in the case of a height=2 block), and here we only
            //   mirror + rotate that union into world space.
        }

        private static void MirrorAroundCenterX(Cuboidf[] boxes, float centerX)
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                var b = boxes[i];
                float x1 = b.X1;
                float x2 = b.X2;

                b.X1 = 2f * centerX - x2;
                b.X2 = 2f * centerX - x1;

                boxes[i] = b;
            }
        }

        public virtual void OnBlockPlaced(ItemStack byItemStack, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (byItemStack == null) return; // Placed by worldgen

            RotateYRad = getRotateYRad(byPlayer, blockSel);
            SetupMeshAndBoxes(true);
        }

        public static float getRotateYRad(IPlayer byPlayer, BlockSelection blockSel)
        {
            BlockPos targetPos = blockSel.DidOffset ? blockSel.Position.AddCopy(blockSel.Face.Opposite) : blockSel.Position;
            double dx = byPlayer.Entity.Pos.X - (targetPos.X + blockSel.HitPosition.X);
            double dz = byPlayer.Entity.Pos.Z - (targetPos.Z + blockSel.HitPosition.Z);
            float angleHor = (float)Math.Atan2(dx, dz);

            float deg90 = GameMath.PIHALF;
            return ((int)Math.Round(angleHor / deg90)) * deg90;
        }

        public bool IsSideSolid(BlockFacing facing)
        {
            // Facing never changes, just make sure that face isn't open
            return !isOpen && facing == windowFacing;
        }

        #region IInteractable

        public bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
        {
            if (!windowBh.handopenable && byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                (Api as ICoreClientAPI).TriggerIngameError(this, "nothandopenable", Lang.Get("This window cannot be opened by hand."));
                return true;
            }
            if (LastEventTime.IsRunning && LastEventTime.ElapsedMilliseconds < 150) {
                return true; // swallow
            }
            LastEventTime.Restart();

            ToggleWindowSashState(byPlayer, !isOpen);
            handling = EnumHandling.PreventDefault;
            return true;
        }

        #endregion

        internal void ToggleWindowSashFromPartner(IPlayer byPlayer, bool targetOpen)
        {
            // Same behavior as a normal toggle, but mark it as coming from a neighbor
            ToggleWindowSashState(byPlayer, targetOpen, true);
        }


        public void ToggleWindowSashState(IPlayer byPlayer, bool targetOpen, bool fromPartner = false)
        {
            // if (this.isOpen == targetOpen) return;

            ToggleWindowSash(targetOpen);  // movement/animation only

            // Sync state to paired neighbors, but only from the "source" leaf so no recursion
            if (!fromPartner)
            {
                if (LeftWindow != null && invertHandles)
                {
                    LeftWindow.ToggleWindowSashState(byPlayer, targetOpen, true);
                }
                if (RightWindow != null)
                {
                    RightWindow.ToggleWindowSashState(byPlayer, targetOpen, true);
                }
            }

            // ---- Sounds (always per-leaf) ----
            float pitch = isOpen ? 0.8f : 0.7f;
            var sound = isOpen ? windowBh?.OpenSound : windowBh?.CloseSound;

            // Secondary sounds from attributes
            var customSoundKey = Block.Attributes?["secondarySounds"]?[isOpen ? "open" : "close"]?.AsString(null);
            if (customSoundKey != null)
            {
                var customSound = new AssetLocation(customSoundKey);
                float customSoundPitch = isOpen ? 0.8f : 1f;

                Api.World.PlaySoundAt(
                    customSound,
                    Pos.X + 0.5f,
                    Pos.InternalY + 0.5f,
                    Pos.Z + 0.5f,
                    byPlayer,
                    EnumSoundType.Sound,
                    customSoundPitch,
                    32f,
                    2f
                );
            }

            if (sound != null)
            {
                Api.World.PlaySoundAt(
                    sound,
                    Pos.X + 0.5f,
                    Pos.InternalY + 0.5f,
                    Pos.Z + 0.5f,
                    byPlayer,
                    EnumSoundType.Sound,
                    pitch,
                    32f,
                    1f
                );
            }
        }

        // Updates movement and hitboxes, but no sounds


        private void ToggleWindowSash(bool targetOpen)
        {
            var animator = animUtil?.animator;
            if (animator == null) return;

            EnsureRendererRegistered();
            if (animUtil?.renderer != null) animUtil.renderer.ShouldRender = true;  // latch ON at start

            var stOpen  = animator.GetAnimationState("opening");
            var stClose = animator.GetAnimationState("closing");
            if (stOpen  != null) animUtil.StopAnimation("opening");
            if (stClose != null) animUtil.StopAnimation("closing");

            float openSpeed = Block.Attributes?["openingSpeed"].AsFloat(10) ?? 10f;
            float closeSpeed = Block.Attributes?["closingSpeed"].AsFloat(10) ?? 10f;

            string target = targetOpen ? "opening" : "closing";

            // Guard: if target already running, don't restart (avoids reentrancy & flicker)
            var cur = animUtil.animator?.GetAnimationState(target);
            if (lastStartedAnimCode == target && (cur?.Running == true)) {
                Api.Logger.Debug($">>>[TICK #{_tickId}] Skip restart of \"{target}\" (already running) for block at {Pos}");
                return;
            }


            // now start fresh
            Api.Logger.Debug($">>> [TICK #{_tickId}] Requesting animUtil start the animation \"{target}\" for block at {Pos}");
            animUtil.StartAnimation(new AnimationMetaData
            {
                Animation = target,
                Code = target,
                Weight = 1f,
                AnimationSpeed = 1f,
                EaseInSpeed = targetOpen ? openSpeed : closeSpeed,
                EaseOutSpeed = targetOpen ? closeSpeed : openSpeed
            });
            
            var st = animUtil.animator?.GetAnimationState(target);
            Api.Logger.Debug($">>> [TICK #{_tickId}] Started animation \"{target}\" with state of null={st==null} active={st?.Active} running={st?.Running} elems={st?.ElementWeights?.Length}");

            // SAFETY: If this clip has no targets, don’t suppress static; fail open immediately.
            if (st == null || (st.ElementWeights != null && st.ElementWeights.Length == 0))
            {
                if (animUtil?.renderer != null) animUtil.renderer.ShouldRender = false;
                lastStartedAnimCode = null;
                isOpen = targetOpen;               // commit state so static mesh matches intent
                Blockentity.MarkDirty(true);
                return;
            }
            
            lastStartedAnimCode = target;
            if (animUtil?.renderer != null) animUtil.renderer.ShouldRender = true;
            Blockentity.MarkDirty(true);

            // triggers chunk mesh rebuild and server sync
            // Api?.World?.BlockAccessor.MarkBlockDirty(Pos);

            // triggers client-side tesselation update (the mesh refresh)
            // Blockentity.MarkDirty(true);
        }


        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            Api.Logger.Debug($">>> [TICK #{_tickId}] Tesselating while isOpen={isOpen}, ElapsedMs={LastEventTime.Elapsed.TotalMilliseconds} for the block at {Pos}");

            bool latched = animUtil?.renderer?.ShouldRender == true;

            // If entity is latched, entity "owns" this frame. Never add static.
            if (latched) { Api.Logger.Debug($">>> [TICK #{_tickId} ] Skipped static rendering during tesselation because the block is (latched) at {Pos}"); return true; }


            // Draw static for the persisted state
            var mesh = isOpen ? openedMesh : closedMesh;
            if (mesh == null) mesh = closedMesh ?? openedMesh;
            if (mesh != null) mesher.AddMeshData(mesh);
            return true;

        }


        // public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        // {
        //     var capi = Api as ICoreClientAPI;
        //     if (capi != null)
        //     {

        //     }

        //     Api.Logger.Debug($"!!! OnTesselation: pos={Pos} isOpen=${isOpen}, ts={LastEventTime.Elapsed.TotalMilliseconds}");

        //     // If an animation is currently running, let the entity renderer draw it.
        //     var anim = animUtil?.animator;
        //     if (anim?.ActiveAnimationCount > 0)
        //     {
        //         Api.Logger.Debug($"!!! Animator is active!");
        //         string target = isOpen ? "opening" : "closing";
        //         var st = anim.GetAnimationState(target);
        //         if (st != null && (st.Active || st.Running))
        //         {
        //             Api.Logger.Debug($"!!! Animator is active, but don't draw this one at {Pos}");
        //             return true; // draw nothing here (avoid double-draw/flash)
        //         }
        //     }

        //     // If the animated renderer is set to render, skip static draw to avoid flash
        //     if (animUtil?.renderer?.ShouldRender == true) return true;

        //     mesher.AddMeshData(isOpen ? openedMesh : closedMesh);
        //     return true;
        // }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);

            if (Api != null)
            {
                Api.Logger.Debug($">>> [TICK #{_tickId}]FromTreeAttributes pos={Pos} side={Api.Side}!!!");
            }
            
            bool beforeOpened = isOpen;

            RotateYRad = tree.GetFloat("rotateYRad");
            isOpen = tree.GetBool("opened");
            invertHandles = tree.GetBool("invertHandles");
            leftWindowOffset = tree.GetVec3i("leftWindowPos");
            rightWindowOffset = tree.GetVec3i("rightWindowPos");

            if (isOpen != beforeOpened && animUtil != null) ToggleWindowSash(isOpen);

            if (Api != null && Api.Side is EnumAppSide.Client)
            {
                if (animUtil?.renderer != null)
                {
                    animUtil.renderer.rotationDeg.Y = RotateYRad * GameMath.RAD2DEG;
                }

                UpdateMeshAndAnimations();

                if (isOpen && !beforeOpened && animUtil != null && !animUtil.activeAnimationsByAnimCode.ContainsKey("opening"))
                {
                    ToggleWindowSash(true);
                }

                UpdateHitBoxes();
                Api.World.BlockAccessor.MarkBlockDirty(Pos);
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("rotateYRad", RotateYRad);
            tree.SetBool("opened", isOpen);
            tree.SetBool("invertHandles", invertHandles);
            if (leftWindowOffset != null) tree.SetVec3i("leftWindowPos", leftWindowOffset);
            if (rightWindowOffset != null) tree.SetVec3i("rightWindowPos", rightWindowOffset);
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            if (Api is ICoreClientAPI capi)
            {
                if (capi.Settings.Bool["extendedDebugInfo"] == true)
                {
                    dsc.AppendLine("" + windowFacing + " " + (isOpen ? "is open" : "is closed"));
                    dsc.AppendLine("" + windowBh.height + "x" + windowBh.width);
                    EnumHandling h = EnumHandling.PassThrough;
                    if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.NORTH, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: North");
                    if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.EAST, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: East");
                    if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.SOUTH, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: South");
                    if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.WEST, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: West");
                }
            }
        }

        public void OnTransformed(IWorldAccessor worldAccessor, ITreeAttribute tree, int degreeRotation, Dictionary<int, AssetLocation> oldBlockIdMapping, Dictionary<int, AssetLocation> oldItemIdMapping, EnumAxis? flipAxis)
        {
            RotateYRad = tree.GetFloat("rotateYRad");
            RotateYRad = (RotateYRad - degreeRotation * GameMath.DEG2RAD) % GameMath.TWOPI;
            tree.SetFloat("rotateYRad", RotateYRad);
        }
    }
}
