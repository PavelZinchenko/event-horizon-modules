using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;

namespace CombatSimulation.Modes
{
    // Docking (F10): circle areas that a ship docks at by proximity. Legacy PlayerDockingAction docks while the ship's
    // collider touches the object's trigger circle. Here the ship docks when its hull circle touches an area, and undocks
    // when the gap between them is larger than the release margin, so a ship at the edge does not flicker. It reads only
    // the snapshot, so it needs no core change.
    //
    // The mode calls Update once a tick, in BeginTick or EndTick. Each change is a ModeEvent with the area's tag (Value0)
    // and the ship's entity ID (Value1). Areas are checked in the order they were added. Each area docks on its own, so a
    // ship can be docked at two areas that overlap.
    public sealed class Docking
    {
        public Docking(BattleSetup setup, int dockedCode, int undockedCode, float releaseMargin)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            if (dockedCode == undockedCode) throw new ArgumentException("The event codes must differ.", nameof(undockedCode));
            if (!(releaseMargin >= 0f)) throw new ArgumentOutOfRangeException(nameof(releaseMargin));
            _torus = new Torus(setup.Width, setup.Height);
            _dockedCode = dockedCode;
            _undockedCode = undockedCode;
            _releaseMargin = releaseMargin;
        }

        public int Count => _areas.Count;

        public void Add(long tag, Vec2 center, float radius)
        {
            if (!(radius > 0f)) throw new ArgumentOutOfRangeException(nameof(radius));
            if (IndexOf(tag) >= 0) throw new ArgumentException($"Duplicate docking tag {tag}.", nameof(tag));
            _areas.Add(new Area(tag, _torus.Wrap(center), radius));
        }

        // For an area on a moving object: the mode gives the object's position before Update.
        public bool Move(long tag, Vec2 center)
        {
            var index = IndexOf(tag);
            if (index < 0) return false;
            _areas[index].Center = _torus.Wrap(center);
            return true;
        }

        // A docked ship undocks from the removed area at once.
        public bool Remove(ModeContext context, long tag)
        {
            var index = IndexOf(tag);
            if (index < 0) return false;
            if (_areas[index].IsDocked) Publish(context, _undockedCode, tag);
            _areas.RemoveAt(index);
            return true;
        }

        public bool IsDocked(long tag)
        {
            var index = IndexOf(tag);
            return index >= 0 && _areas[index].IsDocked;
        }

        // Checks the ship of the last completed tick. A null or absent ship undocks from every area. A new ship first
        // undocks the previous one.
        public void Update(ModeContext context, EntityId ship)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var state = default(ShipState);
            var found = !ship.IsNull && context.Snapshot.TryGetShip(ship, out state);
            if (ship != _ship || !found) UndockAll(context);
            _ship = found ? ship : default;
            if (!found) return;

            foreach (var area in _areas)
            {
                var gap = _torus.Delta(area.Center, state.Position).Length - area.Radius - state.Definition.HullRadius;
                if (!area.IsDocked && gap <= 0f)
                {
                    area.IsDocked = true;
                    Publish(context, _dockedCode, area.Tag);
                }
                else if (area.IsDocked && gap > _releaseMargin)
                {
                    area.IsDocked = false;
                    Publish(context, _undockedCode, area.Tag);
                }
            }
        }

        private void UndockAll(ModeContext context)
        {
            foreach (var area in _areas)
            {
                if (!area.IsDocked) continue;
                area.IsDocked = false;
                Publish(context, _undockedCode, area.Tag);
            }
        }

        private void Publish(ModeContext context, int code, long tag) =>
            context.Publish(new ModeEvent(code, tag, unchecked((long)_ship.Value)));

        private int IndexOf(long tag)
        {
            for (var i = 0; i < _areas.Count; ++i)
                if (_areas[i].Tag == tag) return i;
            return -1;
        }

        private sealed class Area
        {
            public readonly long Tag;
            public readonly float Radius;
            public Vec2 Center;
            public bool IsDocked;

            public Area(long tag, Vec2 center, float radius)
            {
                Tag = tag;
                Center = center;
                Radius = radius;
            }
        }

        private readonly Torus _torus;
        private readonly int _dockedCode;
        private readonly int _undockedCode;
        private readonly float _releaseMargin;
        private readonly List<Area> _areas = new List<Area>();
        private EntityId _ship;
    }
}
