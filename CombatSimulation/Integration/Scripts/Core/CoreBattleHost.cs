using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;
using CombatView;
using GameDatabase.DataModel;
using GameDatabase.Enums;
using GameDatabase.Extensions;
using GameDatabase.Model;
using Services.Audio;
using Services.Localization;
using Services.Resources;
using UnityEngine;
using Zenject;
using CoreEntityId = CombatSimulation.Core.EntityId;
using CoreUnitId = CombatSimulation.Core.UnitId;
using PlayerInputSignal = Combat.Ai.PlayerInputSignal;

namespace Combat.Integration.Core
{
    // The mode-neutral part of a scene on the shared combat core (Main's exploration plan, E1): the frame loop, the
    // renderer, entity IDs, ship views, controls, sounds, broadcasts, camera and asteroids. A mode adapter on top of it
    // owns the units and the rules: it builds the battle, then calls Initialize.
    // Phase 9, step 2 (S4): moved from Main, so both games share it. Four seams carry the game's own types: the mapper's
    // looks (ICoreLookSource), the player pilots (IPlayerPilots), the snapshots (IBattleSource) and the camera rule
    // (ICameraFraming). Main keeps the planet and the effect preview, through RenderMode and DisposeMode.
    // A local battle (LocalBattleSource) steps here, and the BattleRunner is the authority: commands go to it, and the
    // projection only copies its snapshots. A remote battle (Main's online duel) hands over the copies of another thread
    // through the same handling, oldest first.
    public abstract class CoreBattleHost : IShipViews, IShipControlSource, ITickable, IDisposable, IBattleSink
    {
        // exit: fired once, when the exit delay after the result runs out.
        protected CoreBattleHost(
            IResourceLocator resourceLocator,
            Action exit,
            ILocalization localization,
            PlayerInputSignal playerInputSignal,
            ISoundPlayer soundPlayer,
            UnityEngine.SceneManagement.Scene scene)
        {
            _resourceLocator = resourceLocator;
            _exit = exit;
            _localization = localization;
            _playerInputSignal = playerInputSignal;
            _soundPlayer = soundPlayer;
            _scene = scene;
        }

        // Called once by the mode adapter's constructor, after it has mapped its ships: the mapper's looks are read here.
        // asteroids: the battle has asteroids to draw.
        protected void Initialize(ICoreLookSource mapper, IPlayerPilots pilots, IBattleSource source, ICameraFraming framing,
            Torus torus, bool asteroids, CombatViewSettings viewSettings)
        {
            _mapper = mapper;
            _pilots = pilots;
            _source = source;
            _framing = framing;
            LocalSide = source.LocalSide;

            var resourceLocator = _resourceLocator;
            var bulletSprite = resourceLocator.GetSprite(new SpriteId(BulletSpriteName, SpriteId.Type.Ammunition));
            // Only the font of the legacy text effect prefab is used; the text view is built in code (step 7f).
            var textFont = Resources.Load<TextMesh>(TextEffectPrefab)?.font;
            var sounds = new CoreSoundOutput(_soundPlayer, resourceLocator);
            Sounds = sounds;
            Renderer = new BattleRenderer(torus, bulletSprite, textFont, _scene, viewSettings, sounds)
            {
                ViewerSide = source.LocalSide,
                SmoothCorrections = source.IsRemote,
                // Phase 5b, step 4: the legacy combat Settings clips (a loop, as legacy SoundLoopEffect plays it) and the
                // satellite's hard-coded explosion. Step 8: the asteroid's.
                RetreatSound = new SoundClip(resourceLocator.GetAudioClip(new AudioClipId(RetreatSoundName)), true),
                WarpSound = sounds.Resolve(new AudioClipId(WarpSoundName)),
                DroneExplosionSound = sounds.Resolve(new AudioClipId(DroneExplosionSoundName)),
                AsteroidExplosionSound = sounds.Resolve(new AudioClipId(AsteroidExplosionSoundName)),
            };
            // Legacy DamageIndicator colours: the shield, then the LegacyDamageModel types.
            Renderer.SetDamageTextColors(new Color(0.5f, 0.5f, 0.5f), new Color(0.5f, 1f, 0.5f), new Color(0f, 1f, 1f),
                new Color(1f, 1f, 0f), new Color(1f, 0.5f, 1f));
            VisualEffects = new CoreVisualEffects(resourceLocator);
            RegisterMappedLooks();
            _deviceLooks = new CoreDeviceLooks(VisualEffects, sounds);
            _bulletVisuals = new CoreBulletVisuals(_mapper.BulletSources, _mapper.SpawnTriggers, resourceLocator, VisualEffects,
                sounds);
            _anchor = new Vector2(0.5f * torus.Width, 0.5f * torus.Height);
            if (asteroids)
                _asteroids = CoreAsteroids.Create(torus, _scene);

            _playerInputSignal.Event += OnPlayerInput;
        }

        #region IShipViews

        // Exploration plan, E2: each new ship view, in the tick's event handling. A view that vanishes and returns is a
        // new view with a new ID.
        public event Action<EntityId, CombatSide> ShipAdded;

        public bool TryGetShip(EntityId id, out IShipView ship)
        {
            var found = _views.TryGetValue(id, out var view);
            ship = view;
            return found;
        }

        // Camera framing by the game's rule (ICameraFraming). Drawn positions are continuous across the wrap, so the
        // camera never jumps at the map edge. Phase 5b, step 3: the effects' camera shake moves the rect, as legacy
        // Scene.ViewRect.
        public Rect CameraRect
        {
            get
            {
                var rect = _framing.Rect;
                rect.position += Renderer.CameraOffset;
                return rect;
            }
        }

        public Vector2 ViewPoint => _framing.Center;

        #endregion

        #region IShipControlSource

        // Only player ships have controls. A redeployed ship has a new pilot and a new description.
        public bool TryGetControls(EntityId ship, out IShipControlDescription description, out IShipCommandSink commands)
        {
            description = null;
            commands = null;
            if (!IsStepping || !_ids.TryGetCore(ship, out var core) || !_viewsByCore.TryGetValue(core, out var view))
                return false;
            if (!_pilots.TryGetPlayerPilot(core, out var pilot))
                return false;

            description = view.Controls;
            commands = pilot;
            return true;
        }

        #endregion

        #region IBattleSink

        void IBattleSink.BeforeStep() => BeforeStep();

        // One tick's snapshot: the runner's after its step, or a copy from another thread.
        void IBattleSink.Process(BattleSnapshot snapshot)
        {
            _lastTick = snapshot.Tick;
            BeginSnapshot(snapshot);
            HandleEvents(snapshot);
            Renderer.Capture(snapshot);
            _asteroids?.Capture(snapshot);
            ShowBroadcasts(snapshot);
            UpdateProjection(snapshot);
            EndSnapshot(snapshot);

            if (snapshot.IsCompleted)
                OnCompleted(snapshot.Result);
        }

        // Phase 7, step 8: the battle freezes, then exits as after a result.
        void IBattleSink.EndWithoutResult()
        {
            if (_exitDelay < 0f)
                _exitDelay = ExitDelaySeconds;
        }

        #endregion

        public void Tick()
        {
            if (_source == null)
                return;

            _source.Run(this, _playerInput);
            _playerInput = false;
            Render();
            UpdateExit();
        }

        // Scene unload. Clears the battle without events: the outcome is already final or abandoned. Also before Initialize.
        public void Dispose()
        {
            _playerInputSignal.Event -= OnPlayerInput;
            // A remote source belongs to the state that started it, which stops it.
            _source = null;
            DisposeMode();
            Renderer?.Dispose();
            _asteroids?.Dispose();
            _views.Clear();
            _viewsByCore.Clear();
            _ids.Clear();
            _pilots?.Clear();
            _broadcasts.Clear();
        }

        // The mode's view of the player ship and the camera: the controlled ship, the framed target (None for no target),
        // and whether the player stays in the middle.
        protected abstract EntityId PlayerShip { get; }
        protected abstract EntityId CameraTarget { get; }
        protected abstract bool CameraCentersPlayer { get; }

        // Before each local step while the battle runs: the mode's own commands.
        protected virtual void BeforeStep() { }

        // Each tick's snapshot, before its events.
        protected virtual void BeginSnapshot(BattleSnapshot snapshot) { }

        // A ship spawn: the mode names its side and specification; false draws no view.
        protected abstract bool TryDescribeShip(CoreUnitId unit, out CombatSide side,
            out Constructor.IShipSpecification specification);

        // A ship's view went inactive; the host releases its pilot and removes it from the renderer next.
        protected virtual void OnShipRemoved(EntityId ship) { }

        protected virtual void OnUnitTransition(BattleSnapshot snapshot, in BattleEvent item) { }

        // Each tick's snapshot, after its events and the views' update.
        protected virtual void EndSnapshot(BattleSnapshot snapshot) { }

        // An event that the battle mode published (BattleEventKind.Mode), in the tick's event order.
        protected virtual void OnModeEvent(BattleSnapshot snapshot, in ModeEvent item) { }

        // Once per frame after the renderer and the asteroids: the mode's own presentation (Main's planet and exploration
        // objects). Anchor: the drawn controlled ship, or its last position.
        protected virtual void RenderMode(Vector2 anchor, float deltaTime) { }

        // In Dispose, before the host's views: the mode's own presentation.
        protected virtual void DisposeMode() { }

        // The first completed snapshot, and every one after it while the battle steps.
        protected abstract void OnCompleted(BattleResult result);

        // Null after disposal and with a remote source.
        protected BattleRunner Runner => (_source as LocalBattleSource)?.Runner;

        protected int LocalSide { get; private set; } = PlayerSide;

        // The last handled snapshot's tick, local or remote.
        protected long LastTick => _lastTick;

        // False after the result and after disposal: no deployment, mode command or timer.
        protected bool IsRunning => _source != null && _source.IsRunning;

        // The battle steps and the player flies. A local battle also steps after its result (phase 2b, D12); a remote one
        // stops at its result.
        protected bool IsStepping => _source != null && _source.IsStepping;

        // The tick that the next command targets.
        protected long CommandTick => _source?.CommandTick ?? _lastTick + 1;

        protected bool IsExitScheduled => _exitDelay >= 0f;

        // The presentation that Main's effect preview plays on. Null before Initialize.
        protected BattleRenderer Renderer { get; private set; }
        protected CoreVisualEffects VisualEffects { get; private set; }
        protected CoreSoundOutput Sounds { get; private set; }

        // A local battle counts game time, as legacy CombatManager: a pause stops the battle and the delay together. A
        // remote one counts real time; its thread does not pause. A delay of 0 exits in this frame, paused or not.
        protected void ScheduleExit(float seconds) => _exitDelay = seconds;

        protected void ZoomCamera() => _framing.Zoom();

        protected bool TryGetCore(EntityId ship, out CoreEntityId core) => _ids.TryGetCore(ship, out core);
        protected bool TryGetEntity(CoreEntityId core, out EntityId ship) => _ids.TryGet(core, out ship);

        // One local tick out of the frame loop (a surrender's result in the same frame). Nothing with a remote source.
        protected void Step() => (_source as LocalBattleSource)?.Step(this, _playerInput);

        // M1 check: the console lists the inert devices (D1) once per battle, by mapped ship or drone.
        protected void LogInertDevices(ICoreLookSource mapper)
        {
            var report = new System.Text.StringBuilder();
            var count = 0;
            foreach (var mapping in mapper.Mappings.Values)
            {
                for (var i = 0; i < mapping.Devices.Count; ++i)
                {
                    var device = mapping.Devices[i];
                    if (!device.IsInert) continue;
                    report.AppendLine($"  {mapping.Name}, device {i}: {device.Name}: {device.InertReason}");
                    ++count;
                }
            }

            if (count > 0)
                Debug.Log($"{GetType().Name}: {count} inert devices (phase 5 or step 4).\n{report}");
        }

        // Also after the stepping stops (a remote result, disposal), so the last frame stays on screen.
        private void Render()
        {
            var controlled = default(CoreEntityId);
            var player = ActiveView(PlayerShip);
            if (player != null)
                _ids.TryGetCore(player.Id, out controlled);

            var alpha = IsStepping ? _source.Alpha : 1f;
            Renderer.ViewPoint = _framing.Center;
            Renderer.Render(alpha, controlled, ref _anchor, Time.deltaTime);
            _asteroids?.Render(alpha, _anchor, Time.deltaTime);
            RenderMode(_anchor, Time.deltaTime);

            foreach (var pair in _viewsByCore)
                if (Renderer.TryGetShipPose(pair.Key, out var position, out var degrees))
                    pair.Value.SetVisualPose(position, degrees);

            _framing.Update(player, ActiveView(CameraTarget), CameraCentersPlayer);
        }

        private CoreShipView ActiveView(EntityId id) => _views.TryGetValue(id, out var view) && view.IsActive ? view : null;

        private void HandleEvents(BattleSnapshot snapshot)
        {
            var events = snapshot.Events;
            for (var i = 0; i < events.Length; ++i)
            {
                var item = events[i];
                switch (item.Kind)
                {
                    case BattleEventKind.Spawn:
                        if (item.IsBullet)
                            OnBulletSpawned(item);
                        else if (item.IsDrone)
                            OnDroneSpawned(item);
                        else if (item.IsDecoy)
                            OnDecoySpawned(item);
                        else
                            OnShipSpawned(snapshot, item);
                        break;
                    case BattleEventKind.Removal:
                        if (item.IsBullet)
                            Renderer.RemoveBullet(item.EntityId, item.RemovalConditions);
                        else if (item.IsDrone || item.IsDecoy)
                            Renderer.RemoveShip(item.EntityId, item.RemovalReason);
                        else
                            RemoveShip(item, ViewReason(events, i));
                        break;
                    case BattleEventKind.UnitTransition:
                        OnUnitTransition(snapshot, item);
                        break;
                    case BattleEventKind.Rename:
                        Renderer.RenameBullet(item.PreviousId, item.EntityId);
                        break;
                    case BattleEventKind.Hit:
                        Renderer.AddHit(item);
                        break;
                    case BattleEventKind.Disarm:
                        Renderer.AddDisarm(item);
                        break;
                    case BattleEventKind.Teleport:
                        Renderer.AddTeleport(item);
                        break;
                    case BattleEventKind.GravityWave:
                        Renderer.AddGravityWave(item);
                        break;
                    case BattleEventKind.AsteroidRemoval:
                        OnAsteroidRemoved(item);
                        break;
                    case BattleEventKind.Damage:
                        Renderer.AddDamage(item);
                        break;
                    case BattleEventKind.AuxiliaryRemoval:
                        Renderer.RemoveAuxiliary(item.EntityId, item.Point, item.RemovalReason);
                        break;
                    case BattleEventKind.SatelliteChange:
                        Renderer.ChangeSatellite(item);
                        break;
                    case BattleEventKind.Mode:
                        OnModeEvent(snapshot, item.ModeEvent);
                        break;
                }

                Renderer.OnEvent(snapshot, item);
            }
        }

        private void OnShipSpawned(BattleSnapshot snapshot, in BattleEvent item)
        {
            if (!TryDescribeShip(item.UnitId, out var side, out var specification) ||
                !snapshot.TryGetShip(item.EntityId, out var state))
                return;

            var id = _ids.Assign(item.EntityId);
            var view = new CoreShipView(id, side, specification, state, new CoreShipControls(_mapper.Mappings[state.Definition]));
            _views.Add(id, view);
            _viewsByCore.Add(item.EntityId, view);

            RegisterMappedLooks();
            var mapping = _mapper.Mappings[state.Definition];
            Renderer.AddShip(item.EntityId, GetLook(mapping), item.Side);
            AddTurrets(item.EntityId, mapping);
            ShipAdded?.Invoke(id, side);
        }

        // The looks of the mapper's worm tails and repair bots. Main's exploration plan, step 8: the exploration content
        // maps a ship when it first spawns, so each ship spawn registers the looks that the mapper added since. The
        // mapper's dictionaries only grow.
        private void RegisterMappedLooks()
        {
            if (_mapper.SegmentSources.Count != _segmentLookCount)
            {
                _segmentLookCount = _mapper.SegmentSources.Count;
                foreach (var source in _mapper.SegmentSources)
                    Renderer.AddSegmentLook(source.Key, SegmentLook(source.Value, _resourceLocator));
            }

            if (_mapper.RepairBotColors.Count != _repairBotLookCount)
            {
                _repairBotLookCount = _mapper.RepairBotColors.Count;
                foreach (var look in _mapper.RepairBotColors)
                    Renderer.AddRepairBotLook(look.Key, new RepairBotLook(look.Value.ToUnity(),
                        RepairBeam(_mapper.RepairBotEffects.TryGetValue(look.Key, out var effect) ? effect : null),
                        _mapper.RepairBotSounds.TryGetValue(look.Key, out var sound) ? Sounds.Resolve(sound) : default));
            }
        }

        // The view of every ship of one definition (phase 5b, step 4), built on first use.
        private ShipLook GetLook(CoreShipMapping mapping)
        {
            if (_looks.TryGetValue(mapping.Definition, out var look))
                return look;

            look = new ShipLook(_resourceLocator.GetSprite(mapping.ModelImage), mapping.ModelScale)
                { Tint = mapping.TintColor?.ToUnity() };
            var source = mapping.Look;
            if (source != null)
            {
                var engines = new EngineLook[source.Engines.Count];
                for (var i = 0; i < engines.Length; ++i)
                    engines[i] = new EngineLook(source.Engines[i].Position.ToUnity() * 0.5f, source.Engines[i].Size);

                look.Color = source.ShipColor.Color.ToUnity();
                look.IsHsv = source.ShipColor.IsHsv;
                look.Hue = source.ShipColor.Hue;
                look.Saturation = source.ShipColor.Saturation;
                look.IsDrone = source.IsDrone;
                look.EngineColor = source.EngineColor.ToUnity();
                look.Engines = engines;
                look.HasShield = source.HasShield;
                look.Explosion = VisualEffects.Get(source.Explosion);
                look.ExplosionSound = Sounds.Resolve(source.ExplosionSound);
                look.LeavesWreck = source.LeavesWreck;
                look.HasShadow = source.HasShadow;
            }

            // Phase 5b, step 7a: the sounds and effects of the weapons, devices and drone bays.
            look.Devices = _deviceLooks.Create(mapping);

            _looks.Add(mapping.Definition, look);
            return look;
        }

        // Phase 5b, step 7a (owner question 8 (b)): a bullet of a weapon's own definition is its shot. A charged copy has
        // the weapon's definition as its uncharged one; child bullets have definitions of their own.
        private void OnBulletSpawned(in BattleEvent item)
        {
            Renderer.AddBullet(item, _bulletVisuals.Get(item));
            object key = item.IsBeam ? item.BeamDefinition.Uncharged : (object)item.BulletDefinition.Uncharged;
            if (_mapper.WeaponDevices.TryGetValue(key, out var device))
                Renderer.AddShot(item.ParentId, device, item.Tick);
        }

        // Legacy Decoy prefab at the body scale, in the device colour (phase 5, step 10c; phase 5b, step 7b). Step 7a:
        // legacy CreateDecoy's flash at 4 x the decoy size, and the device sound.
        private void OnDecoySpawned(in BattleEvent item)
        {
            var color = _mapper.DecoyColors.TryGetValue(item.Definition, out var data) ? data.ToUnity() : Color.white;
            var sound = _mapper.DecoySounds.TryGetValue(item.Definition, out var clip) ? Sounds.Resolve(clip) : default;
            Renderer.AddDecoy(item.EntityId, DecoyDefinition.Size, color, sound, 4f * DecoyDefinition.Size, item.Tick);
        }

        // Phase 5b, step 8: legacy AsteroidExplosionAction.
        private void OnAsteroidRemoved(in BattleEvent item)
        {
            if (_asteroids != null && _asteroids.Remove(item.EntityId, out var size))
                Renderer.AddAsteroidExplosion(item.Point, size);
        }

        // Phase 5b, step 7a: the launch of a drone bay or a clonning center.
        private void OnDroneSpawned(in BattleEvent item)
        {
            var mapping = _mapper.Mappings[item.Definition];
            Renderer.AddShip(item.EntityId, GetLook(mapping), item.Side);
            AddTurrets(item.EntityId, mapping);
            if (item.BayKey.HasValue)
                Renderer.AddLaunch(item.ParentId, item.BayKey.Value.DeviceIndex, item.EntityId);
        }

        // Satellites first: a turret on a satellite rides its view (phase 5, step 12c).
        private void AddTurrets(CoreEntityId id, CoreShipMapping mapping)
        {
            foreach (var satellite in mapping.Satellites)
                Renderer.AddSatellite(id, _resourceLocator.GetSprite(satellite.Image), satellite.Scale);
            // Legacy ShipFactory.CreatePlatform applies the turret scheme's HSV to every turret.
            var turretColor = mapping.Look?.TurretColor ?? default;
            foreach (var turret in mapping.Turrets)
                Renderer.AddTurret(id, turret.Barrel, mapping.Definition.Barrels[turret.Barrel],
                    _resourceLocator.GetSprite(turret.Image), turret.Size, turret.Color.ToUnity(), turretColor.Hue,
                    turretColor.Saturation);

            // Energy and frontal shields (phase 5, step 10d).
            var devices = mapping.Definition.Devices;
            for (var i = 0; i < devices.Count; ++i)
                if (devices[i] is ShieldDefinition shield)
                    Renderer.AddShield(id, i, GetShieldLook(shield));
        }

        // Phase 5b, step 7b, legacy CustomPrefabLoader: a CircularSpriteObject record sets the EnergyShield prefab's sprite,
        // a CircularOutlineObject record builds the EnergyShieldOutline ring. Step 7c: a ColliderSpriteObject record sets
        // the FrontalShield prefab's sprite.
        private ShieldLook GetShieldLook(ShieldDefinition shield)
        {
            if (_shieldLooks.TryGetValue(shield, out var look) || !_mapper.ShieldLooks.TryGetValue(shield, out var source))
                return look;

            look = new ShieldLook
            {
                Kind = source.IsFrontal ? ShieldViewKind.Frontal : ShieldViewKind.Energy,
                Offset = new Vector2(source.OffsetX, source.OffsetY),
                Size = source.Size,
                Color = source.Color.ToUnity(),
                IdleOpacity = source.IdleOpacity,
            };
            switch (source.Prefab)
            {
                case GameObjectPrefab_CircularSpriteObject sprite:
                    look.Image = _resourceLocator.GetSprite(sprite.Image);
                    break;
                case GameObjectPrefab_CircularOutlineObject outline:
                    look.Kind = ShieldViewKind.EnergyOutline;
                    look.Image = _resourceLocator.GetSprite(outline.Image);
                    look.ImageScale = outline.ImageScale;
                    look.Thickness = outline.Thickness;
                    look.AspectRatio = outline.AspectRatio;
                    break;
                case GameObjectPrefab_ColliderSpriteObject frontal:
                    look.Image = _resourceLocator.GetSprite(frontal.Image);
                    break;
            }

            _shieldLooks.Add(shield, look);
            return look;
        }

        // Phase 5b, step 7b, legacy WormTailSegmentInitializer: a database record sets the WormSegment prefab's sprites and
        // its joint.
        private static SegmentLook SegmentLook(in CoreSegmentSource source, IResourceLocator resourceLocator)
        {
            var record = source.Record;
            return new SegmentLook
            {
                IsSegment2 = source.IsSegment2,
                HasRecord = record != null,
                Body = record != null ? resourceLocator.GetSprite(record.BodyImage) : null,
                Joint = record != null ? resourceLocator.GetSprite(record.JointImage) : null,
                JointScale = record?.JointImageScale ?? 0f,
                JointOffset = record?.JointImageOffset ?? 0f,
            };
        }

        // Phase 5b, step 7c: the first Laser element of the repair bot's record. A device without a record (a mod) gets
        // the legacy Laser effect prefab: its texture, 1 wide, in the device colour; a record without a Laser element
        // draws no beam.
        private CombatView.Effects.EffectElement RepairBeam(VisualEffect effect)
        {
            if (effect == null)
                return new CombatView.Effects.EffectElement(CombatView.Effects.EffectKind.Laser,
                    _resourceLocator.GetSprite(new SpriteId(RepairBeamImage, SpriteId.Type.Effect)),
                    CombatView.Effects.ColorMode.TakeFromOwner, Color.white, 1, 1f, 0f, 0f, 0f, 1f, 1f, Vector2.zero, 0f,
                    loop: false, inverse: false, useRealTime: false);

            foreach (var element in effect.Elements)
                if (element.Type == VisualEffectType.Laser)
                    return VisualEffects.ToElement(element, CombatView.Effects.EffectKind.Laser);
            return null;
        }

        // The unit transition of the same removal comes later in the batch, so the ID stays mapped until the tick ends.
        // Legacy ShipInfo.Destroy: surrender and kill-all make the ship vanish, without an explosion or a sound. The core
        // removes it with Death and names the ForcedDestruction in its unit's transition later in the same batch.
        private static RemovalReason ViewReason(ReadOnlySpan<BattleEvent> events, int index)
        {
            var removal = events[index];
            if (removal.RemovalReason != RemovalReason.Death)
                return removal.RemovalReason;

            for (var i = index + 1; i < events.Length; ++i)
                if (events[i].Kind == BattleEventKind.UnitTransition && events[i].EntityId == removal.EntityId)
                    return events[i].TransitionReason == UnitTransitionReason.ForcedDestruction
                        ? RemovalReason.Vanish : removal.RemovalReason;
            return removal.RemovalReason;
        }

        private void RemoveShip(in BattleEvent item, RemovalReason viewReason)
        {
            if (_viewsByCore.TryGetValue(item.EntityId, out var view))
            {
                view.Deactivate();
                OnShipRemoved(view.Id);
            }

            _pilots.Release(item.EntityId);
            Renderer.RemoveShip(item.EntityId, viewReason);
            _removed.Add(item.EntityId);
        }

        // Copies what the views need from the snapshot; spans are valid only until the next step.
        private void UpdateProjection(BattleSnapshot snapshot)
        {
            foreach (var ship in snapshot.Ships)
                if (_viewsByCore.TryGetValue(ship.Id, out var view))
                    view.Update(ship, snapshot.GetDevices(ship));

            foreach (var core in _removed)
            {
                if (_viewsByCore.TryGetValue(core, out var view))
                {
                    _viewsByCore.Remove(core);
                    _views.Remove(view.Id);
                }

                _ids.Release(core);
            }

            _removed.Clear();
        }

        private void UpdateExit()
        {
            if (_exitDelay < 0f || _exited)
                return;

            _exitDelay -= _source != null && _source.IsRemote ? Time.unscaledDeltaTime : Time.deltaTime;
            if (_exitDelay > 0f)
                return;

            _exited = true;
            _exit();
        }

        // Legacy PlayerInputSignal: the keyboard driver and the HUD fire it in every frame with player input (step 7f).
        // The flag holds until the end of the frame's steps.
        private void OnPlayerInput() => _playerInput = true;

        // ShowMessage (step 7e): called inside the step, while the pilots think. Local only (D6).
        protected void OnBroadcast(CoreEntityId ship, string text, ColorData color) =>
            _broadcasts.Add(new Broadcast(ship, text, color));

        // Legacy RadioTransmitter: the localized text at the ship, drifting with it. Legacy NodeBuilder localizes at
        // build time; the text is the same.
        private void ShowBroadcasts(BattleSnapshot snapshot)
        {
            _source?.TakeBroadcasts(snapshot.Tick, OnBroadcast);
            foreach (var item in _broadcasts)
                if (snapshot.TryGetShip(item.Ship, out var state))
                    Renderer.AddText(_localization.Localize(item.Text), item.Color.ToUnity(), state.Position, state.LinearVelocity);

            _broadcasts.Clear();
        }

        private readonly struct Broadcast
        {
            public Broadcast(CoreEntityId ship, string text, ColorData color)
            {
                Ship = ship;
                Text = text;
                Color = color;
            }

            public readonly CoreEntityId Ship;
            public readonly string Text;
            public readonly ColorData Color;
        }

        protected const int PlayerSide = 0;
        protected const int EnemySide = 1;
        protected const int TicksPerSecond = 50;
        protected const float ExitDelaySeconds = 3f;
        private const string BulletSpriteName = "ball";
        // Legacy ShowMessage effect (RadioTransmitter).
        private const string TextEffectPrefab = "Combat/Effects/MessageText";
        // Legacy combat Settings component (CombatScene): ShipRetreatSound and ShipWarpSound. Legacy
        // DroneExplosionActionObsolete of a satellite, a decoy, a repair bot or a tail segment; AsteroidExplosionAction.
        private const string RetreatSoundName = "flux_02";
        private const string WarpSoundName = "shot_06";
        private const string DroneExplosionSoundName = "explosion_10";
        private const string AsteroidExplosionSoundName = "explosion_08";
        // The texture of the legacy Laser effect prefab (Textures/Bullets/laser; the same image is Textures/Effects/laser).
        private const string RepairBeamImage = "laser";
        private readonly Dictionary<ShieldDefinition, ShieldLook> _shieldLooks = new Dictionary<ShieldDefinition, ShieldLook>();

        private IBattleSource _source;
        // The last handled snapshot's tick, local or remote.
        private long _lastTick = -1;
        private IPlayerPilots _pilots;
        private readonly List<Broadcast> _broadcasts = new List<Broadcast>();
        private bool _playerInput;
        private ICoreLookSource _mapper;
        private ICameraFraming _framing;
        // The drawn controlled ship, or its last position: every other entity is drawn at its image nearest to it.
        private Vector2 _anchor;
        // Seconds left until the exit after the result; negative while the battle runs.
        private float _exitDelay = -1f;
        private bool _exited;

        private readonly Dictionary<EntityId, CoreShipView> _views = new Dictionary<EntityId, CoreShipView>();
        private readonly Dictionary<CoreEntityId, CoreShipView> _viewsByCore = new Dictionary<CoreEntityId, CoreShipView>();
        private readonly List<CoreEntityId> _removed = new List<CoreEntityId>();
        private readonly EntityIdMap _ids = new EntityIdMap();
        private CoreBulletVisuals _bulletVisuals;
        private CoreDeviceLooks _deviceLooks;
        private readonly Dictionary<ShipDefinition, ShipLook> _looks = new Dictionary<ShipDefinition, ShipLook>();
        private CoreAsteroids _asteroids;
        private int _segmentLookCount;
        private int _repairBotLookCount;

        private readonly IResourceLocator _resourceLocator;
        private readonly Action _exit;
        private readonly ILocalization _localization;
        private readonly PlayerInputSignal _playerInputSignal;
        private readonly ISoundPlayer _soundPlayer;
        private readonly UnityEngine.SceneManagement.Scene _scene;
    }
}
