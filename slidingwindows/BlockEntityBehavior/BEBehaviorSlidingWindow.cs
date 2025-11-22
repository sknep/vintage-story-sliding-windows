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
        protected bool invertHandles;
    
        protected MeshData currentStaticMesh;
        class ScheduledCallback
        {
            public long Id;
            public Action Callback;
        }
        readonly List<ScheduledCallback> scheduledCallbacks = new List<ScheduledCallback>();
        bool renderCurrentMeshAnimation = false;
        bool tesselateCurrentStaticMesh = true;
        enum CurrentStaticMeshType { Closed, Opened }
        CurrentStaticMeshType currentStaticMeshType;
        bool animationStopsSoon = false;

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
        ICoreClientAPI capi;

        protected Vec3i leftWindowOffset;
        protected Vec3i rightWindowOffset;

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

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);
            capi = api as ICoreClientAPI;   // will be null on server side

            SetupMeshAndBoxes(false);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);

            if (Api.Side == EnumAppSide.Client)
            {
                PrepareStaticAndAnimMeshes(); 
                string primaryTrack = isOpen ? "closing" : "opening";
                EnsureAnimatorTrackReady(primaryTrack);
                UpdateStaticMeshAndRotation(isOpen);
                // no animation on load
                renderCurrentMeshAnimation = false;
                updateShouldRenderFromCurrentAnim();
                Api.World.BlockAccessor.MarkBlockDirty(Pos);
            }
            UpdateHitBoxes();
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
            // This method mirrors vanilla door pairing, but adapts it to optional pairing
            // and the shallower sliding-footprint.  It may run multiple times while a window
            // searches for a counterpart or when multi-block parts are re-placed.
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
                // Lonely unpaired window: ensure stale offsets are cleared so it behaves
                // like a single leaf regardless of what was saved in the chunk data.
                LeftWindow = null;
                RightWindow = null;
                invertHandles = false;
                Blockentity.MarkDirty(true);
            }
            UpdateHitBoxes();
        }
        private void PrepareStaticAndAnimMeshes()
        {
            if (Api.Side != EnumAppSide.Client) return;
            if (windowBh == null) return;
            
            // register default mesh as closed shape with animation
            string closedAnimKey = Block.Shape.ToString();
            if (windowBh.closedDictKey == null) windowBh.closedDictKey = closedAnimKey;

            // Prefer the shape returned by CreateMesh when animUtil is available
            if (animUtil != null && windowBh.closedAnimMesh == null)
            {
                windowBh.closedAnimMesh = animUtil.CreateMesh(closedAnimKey, null, out Shape animShape, null);
                if (windowBh.closedShape == null) windowBh.closedShape = animShape;
            }

            // If we still don't have a shape (e.g. animUtil null on first run), load it directly
            if (windowBh.closedShape == null && Block.Shape != null)
            {
                windowBh.closedShape = Shape.TryGet(Api, Block.Shape.Base);
            }

            if (windowBh.closedStaticMesh == null && windowBh.closedShape != null) 
            {
                MeshData closedStatic;
                capi.Tesselator.TesselateShape(Block, windowBh.closedShape, out closedStatic, null);
                windowBh.closedStaticMesh = closedStatic;
            }

            if (Block.Attributes?["openedShape"].Exists == true)
            {
                var openedShapeCode = Block.Attributes["openedShape"].AsString(null);
                var openedShapeLoc  = AssetLocation.Create(openedShapeCode);
                var openedAnimKey = closedAnimKey + "-alt";
                
                if (windowBh.openedDictKey == null) windowBh.openedDictKey = openedAnimKey;

                // Ensure openedShape
                if (windowBh.openedShape == null) windowBh.openedShape = Shape.TryGet(Api, openedShapeLoc);

                // Again, prefer the shape returned by CreateMesh when possible
                if (animUtil != null && windowBh.openedAnimMesh == null && windowBh.openedShape != null)
                {
                    windowBh.openedAnimMesh = animUtil.CreateMesh(openedAnimKey, windowBh.openedShape, out Shape animShape, null);
                    windowBh.openedShape = animShape;  // keep the animator’s shape as the canonical one
                }

                if (windowBh.openedStaticMesh == null && windowBh.openedShape != null)
                {
                    MeshData openedStatic;
                    capi.Tesselator.TesselateShape(Block, windowBh.openedShape, out openedStatic, null);
                    windowBh.openedStaticMesh = openedStatic;
                }
            }
        }

        private void InitializeAnimatorTracks(string animCode)
        {
            if (Api.Side != EnumAppSide.Client || animUtil == null) return;

            MeshData animMesh;
            Shape animShape;
            string animKey;

            if (animCode == "opening")
            {
                animMesh  = windowBh.closedAnimMesh;
                animShape = windowBh.closedShape;
                animKey   = windowBh.closedDictKey;
            }
            else if (animCode == "closing")
            {
                animMesh  = windowBh.openedAnimMesh ?? windowBh.closedAnimMesh;
                animShape = windowBh.openedShape    ?? windowBh.closedShape;
                animKey   = windowBh.openedDictKey  ?? windowBh.closedDictKey;
            }
            else
            {
                return;
            }
            if (animMesh == null || animShape == null) return;
            animUtil.InitializeAnimator(animKey, animMesh, animShape, null);

        }


        protected virtual void UpdateStaticMeshAndRotation(bool useOpened = false)
        {
            if (Api.Side != EnumAppSide.Client) return;
            MeshData baseStaticMesh;

            if (useOpened && windowBh.openedStaticMesh  != null)
            {
                baseStaticMesh = windowBh.openedStaticMesh ;
                currentStaticMeshType = CurrentStaticMeshType.Opened;
            }
            else
            {
                baseStaticMesh = windowBh.closedStaticMesh ?? windowBh.closedAnimMesh; // fallback
                currentStaticMeshType = CurrentStaticMeshType.Closed;
            }

            // Clone so we can mutate safely for this block instance
            currentStaticMesh = baseStaticMesh?.Clone();

            if (currentStaticMesh == null) return;
            float rot = RotateYRad;
            if (invertHandles) rot = -rot;
            if (rot != 0f)
            {
                currentStaticMesh = currentStaticMesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0, rot, 0);
            }
            if (invertHandles)
            {
                Matrixf matf = new Matrixf();
                matf.Translate(0.5f, 0.5f, 0.5f)
                    .Scale(-1f, 1f, 1f)
                    .Translate(-0.5f, -0.5f, -0.5f);
                currentStaticMesh.MatrixTransform(matf.Values);
            }

            // Orient renderer to match (so anim meshes line up visually)
            ApplyRendererRotation(rot);
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
        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();
            CancelAnimationWork();
        }

        public override void OnBlockUnloaded()
        {
            base.OnBlockUnloaded();
            CancelAnimationWork();
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

        public bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
        {
            if (!windowBh.handopenable && byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                capi?.TriggerIngameError(this, "nothandopenable", Lang.Get("This window cannot be opened by hand."));                handling = EnumHandling.PreventDefault;
                return true;
            }
            ToggleSashState(byPlayer, !isOpen);
            handling = EnumHandling.PreventDefault;
            return true;
        }

        public void ToggleSashState(IPlayer byPlayer, bool toOpenState, bool fromPartner = false)
        {
            // If we're already in that state, bail.
            // if you want to be explicit about the 'opening' animation: (animUtil.activeAnimationsByAnimCode.ContainsKey("opening") == isOpen)
            if (isOpen == toOpenState)  return;
            string animCode = toOpenState ? "opening" : "closing";
            isOpen = toOpenState;

            // 2) Play audio for whichever leaf actually toggled.
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
            // 3) Client visuals: start/stop animation + refresh static mesh/tesselation.
            if (Api.Side == EnumAppSide.Client)
            {
                UpdateStaticMeshAndRotation(isOpen);
                ToggleSashAnimation(animCode);  // movement/animation only
                Api.World.BlockAccessor.MarkBlockDirty(Pos);
            }

            // 1) Propagate state to paired neighbors, but only from the "source" leaf so no recursion.
            bool shouldSync = true;

            if (!fromPartner && shouldSync)
            {
                if (LeftWindow != null && invertHandles)
                {
                    LeftWindow.ToggleSashState(byPlayer, isOpen, true);
                }
                if (RightWindow != null)
                {
                    RightWindow.ToggleSashState(byPlayer, isOpen, true);
                }
            }
            if (Api.Side == EnumAppSide.Server && shouldSync)
            {
                Blockentity.MarkDirty(true);
            }

            if (Api.Side == EnumAppSide.Server)
            {
                UpdateNeighbors();
            }
        }

        private void UpdateNeighbors()
        {
            if (Api.Side != EnumAppSide.Server || windowBh == null) return;
            windowBh.IterateOverEach(Pos, RotateYRad, invertHandles, blockPos =>
            {
                Api.World.BlockAccessor.TriggerNeighbourBlockUpdate(blockPos);
                return true;
            });
        }

        // imprecise way to delay overlaps around animation timing
        public void RunAfterMs(int delayMs, Action callback)
        {
            if (capi == null || callback == null) return;

            var scheduled = new ScheduledCallback { Callback = callback };
            scheduled.Id = capi.Event.RegisterCallback(dt =>
            {
                scheduledCallbacks.Remove(scheduled);
                var cb = scheduled.Callback;
                scheduled.Callback = null;
                cb?.Invoke();
            }, delayMs);

            scheduledCallbacks.Add(scheduled);
        }

        // expect the main thread to handle this immediately after the current frame is rendered, but before the next
        public void RunBeforeNextFrameRender(Action callback)
        {
            if (capi == null || callback == null) return;
            capi.Event.EnqueueMainThreadTask(callback, "SlidingWindowDeferred");
        }

        bool StartAnimationAndCallback(string animCode)
        {
            if (Api.Side != EnumAppSide.Client || animUtil == null) return false;

            // immediately invoke queued callbacks... unless we are ending an interrupted animation with another animation
            CompletePendingAnimationCallbacks(!renderCurrentMeshAnimation);
            if (!EnsureAnimatorTrackReady(animCode)) return false;

            float easeInSpeed = Block.Attributes?["openingSpeed"].AsFloat(10) ?? 10;
            float easeOutSpeed = Block.Attributes?["closingSpeed"].AsFloat(10) ?? 10;
            // note that older windows used "opened" as the animation track name, like doors
            float easeSpeed = animCode == "opening" ? easeInSpeed : easeOutSpeed; 
            var meta = new AnimationMetaData
            {
                Animation = animCode,
                Code = animCode,
                EaseInSpeed = easeSpeed,
                EaseOutSpeed = easeSpeed,
                AnimationSpeed = 1f 
            };

            double baseDurationMs = 1000.0 / easeSpeed; 

            // enable both the tesselator and renderer for the next draw
            tesselateCurrentStaticMesh = true;
            renderCurrentMeshAnimation = true;
            updateShouldRenderFromCurrentAnim();

            animUtil.StartAnimation(meta);
            UpdateStaticMeshAndRotation(isOpen);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
            // Api.Logger.Debug(">>> animationStarted, but there should still be a static mesh in the mesh pool for one more OnTesselation");

            RunBeforeNextFrameRender(() =>
            {
                // disable the tesselator for the next draw, leave the renderer running
                tesselateCurrentStaticMesh = false;
                // don't batch these into the same frame
                RunBeforeNextFrameRender(() => Api.World.BlockAccessor.MarkBlockDirty(Pos));                
            });
            
            string currentAnimCode = animCode;
            string nextAnimCode = animCode == "closing" ? "opening" : "closing";

            RunAfterMs((int)(baseDurationMs * 6), () =>
            {
                UpdateStaticMeshAndRotation(isOpen);
                // enable the tesselator for the next draw, tesselation callback will turn off the renderer after one pass
                tesselateCurrentStaticMesh = true;
                RunBeforeNextFrameRender(() =>
                {
                    animationStopsSoon = true;
                    Api.World.BlockAccessor.MarkBlockDirty(Pos);
                    // prewarm the animator for the next state
                    RunBeforeNextFrameRender(() => EnsureAnimatorTrackReady(nextAnimCode));
                });

            });

            return true;
        }
        bool EnsureAnimatorTrackReady(string animCode)
        {
            if (Api.Side != EnumAppSide.Client || animUtil == null) return false;

            bool missingClosedMeshes = windowBh?.closedStaticMesh == null || windowBh?.closedAnimMesh == null;
            bool missingOpenedMeshes = windowBh?.openedStaticMesh == null && windowBh?.openedAnimMesh == null;

            if (missingClosedMeshes || missingOpenedMeshes) PrepareStaticAndAnimMeshes();
            InitializeAnimatorTracks(animCode);
            return true;
        }

        void CompletePendingAnimationCallbacks(bool invoke = true)
        {
            if (capi == null || scheduledCallbacks.Count == 0) return;

            var snapshot = scheduledCallbacks.ToArray();
            scheduledCallbacks.Clear();

            foreach (var scheduled in snapshot)
            {
                capi.Event.UnregisterCallback(scheduled.Id);

                if (invoke)
                {
                    var cb = scheduled.Callback;
                    scheduled.Callback = null;
                    cb?.Invoke();
                }
                else
                {
                    scheduled.Callback = null;
                }
            }
        }
        void CancelAnimationWork()
        {
            // Only client has callbacks/animators to clean up
            if (Api?.Side != EnumAppSide.Client) return;

            CompletePendingAnimationCallbacks(false);
            animationStopsSoon = false;
            
            // turn tesselator back on
            tesselateCurrentStaticMesh = true;

            renderCurrentMeshAnimation = false;
            updateShouldRenderFromCurrentAnim();
            if (animUtil == null) return;
            animUtil.StopAnimation("opening");
            animUtil.StopAnimation("closing");
        }

        private void ApplyRendererRotation(float rot)
        {
            if (animUtil?.renderer == null) return;

            animUtil.renderer.rotationDeg.Y = rot * GameMath.RAD2DEG;
            animUtil.renderer.ScaleX = invertHandles ? -1f : 1f;
            animUtil.renderer.backfaceCulling = !invertHandles;
        }

        private bool ToggleSashAnimation(string animCode)
        {
            // Server is not invited to the animation party
            if (Api.Side != EnumAppSide.Client || animUtil == null) return false;

            // Need a matching animation mesh ready for each sash
            EnsureAnimatorTrackReady(animCode);

            // update rotation before we start playing
            ApplyRendererRotation(invertHandles ? -RotateYRad : RotateYRad);

            // Actually play the right clip
            bool started = StartAnimationAndCallback(animCode);
            if (!started) return false;
            return true;
        }

        // marking the block dirty will trigger OnTesselation, eventually. No contract for frame or time frame, may be batched
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            if (Api.Side != EnumAppSide.Client) return true; // shhh server you're not invited to the tesselation party
            if (tesselateCurrentStaticMesh == true && currentStaticMesh != null && currentStaticMesh.VerticesCount > 0 )
            {
                // bake this custom model into the chunk mesh as static geometry
                mesher.AddMeshData(currentStaticMesh);

                // queue up the animation stopping after we're sure we've marked dirty and triggered a new onTesselation
                if (renderCurrentMeshAnimation == true && animationStopsSoon == true)
                {
                    animationStopsSoon = false;
                    // Api.Logger.Debug(">>> animationStopsSoon was true for this OnTesselation and is now set to false");
                    // Api.Logger.Debug(">>> Calling RunBeforeNextFrameRender to enqueue a short timeout to stop animations after a mark dirty during onTesselation");
                    RunBeforeNextFrameRender(() =>
                    {
                        Api.World.BlockAccessor.MarkBlockDirty(Pos);
                        // Api.Logger.Debug(">>> MarkDirty one frame after setting animationStopSoon to false, forces queueing of animation stopping callback");
                        RunAfterMs(30, () =>
                        {
                            // Api.Logger.Debug(">>> --- 30ms after animationStopsSoon flag, stopping animations and finally setting shouldrender to false ---");
                            if (animUtil == null) return;
                            tesselateCurrentStaticMesh = true; // should already be true but just in case
                            renderCurrentMeshAnimation = false;
                            updateShouldRenderFromCurrentAnim();
                            animUtil.StopAnimation("opening");
                            animUtil.StopAnimation("closing");
                        });
                    });
                    
                }
            }

            // return true to skip engine's default mesh tesselation; false will also add the default model to the mesher
            return true;
        }

        private void updateShouldRenderFromCurrentAnim()
        {
            if (animUtil?.renderer == null || animUtil?.animator == null) return;
            // allow render while flag is true, but don't rely on ShouldRender because it is affected by external code/conditions too
            animUtil.renderer.ShouldRender = renderCurrentMeshAnimation;
        }


        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);

            RotateYRad = tree.GetFloat("rotateYRad");
            isOpen = tree.GetBool("opened") || tree.GetBool("isOpen"); // old windows used 'opened' like doors
            invertHandles = tree.GetBool("invertHandles");
            leftWindowOffset = tree.GetVec3i("leftWindowPos");
            rightWindowOffset = tree.GetVec3i("rightWindowPos");

            if (Api == null) return;

            // Re-run local transforms before rendering so collision + visuals match the replicated state.
            UpdateHitBoxes();

            if (Api.Side is EnumAppSide.Client)
            {
                bool needAnimPrep =
                    windowBh?.closedStaticMesh == null ||
                    windowBh?.closedAnimMesh == null ||
                    (Block.Attributes?["openedShape"].Exists == true && windowBh?.openedStaticMesh == null);

                if (needAnimPrep)
                {
                    PrepareStaticAndAnimMeshes();
                    string primaryTrack = isOpen ? "closing" : "opening";
                    EnsureAnimatorTrackReady(primaryTrack);
                }

                if (!renderCurrentMeshAnimation)
                 {
                    ApplyRendererRotation(invertHandles ? -RotateYRad : RotateYRad);
                    // Safe to swap static meshes immediately if no animation is currently rendering.
                    if (renderCurrentMeshAnimation == false) UpdateStaticMeshAndRotation(isOpen);
                    Api.World.BlockAccessor.MarkBlockDirty(Pos);
               }
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("rotateYRad", RotateYRad);
            tree.SetBool("isOpen", isOpen);
            tree.SetBool("invertHandles", invertHandles);
            if (leftWindowOffset != null) tree.SetVec3i("leftWindowPos", leftWindowOffset);
            if (rightWindowOffset != null) tree.SetVec3i("rightWindowPos", rightWindowOffset);
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            if (capi != null && capi.Settings.Bool["extendedDebugInfo"] == true)
            {
                dsc.AppendLine("" + windowFacing + " " + (isOpen ? "open" : "closed"));
                dsc.AppendLine("" + windowBh.height + "x" + windowBh.width);
                EnumHandling h = EnumHandling.PassThrough;
                if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.NORTH, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: North");
                if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.EAST, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: East");
                if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.SOUTH, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: South");
                if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.WEST, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: West");
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
