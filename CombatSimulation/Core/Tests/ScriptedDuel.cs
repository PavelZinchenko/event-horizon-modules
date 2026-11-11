using System;
using System.Collections.Generic;
using CombatSimulation.Runtime;

namespace CombatSimulation.Core.Tests
{
    // The phase 5 scripted battle on two cores in one process. Core A is owner 20 (side 1), core B is owner 10
    // (side 0). After both cores step, each sends its operations, hits and a watermark to the other, as NetPair does;
    // every CorrectionPeriod ticks each also sends corrections. Pilots run on their owner's core and send each frame
    // to both cores, as the network session sends remote inputs to the copy.
    internal sealed class ScriptedDuel
    {
        public const int OwnerA = 20;
        public const int OwnerB = 10;
        public const int CorrectionPeriod = 10;

        // Setup: owner of side 0, owner of side 1, local owners. Barrel digest: the step 12a log adds barrel states.
        public ScriptedDuel(Func<int, int, IReadOnlyCollection<int>, BattleSetup> setup = null, bool barrelDigest = false)
        {
            setup = setup ?? ((owner0, owner1, local) => ScriptedBattle.Phase5Setup(owner0, owner1, local));
            A = new Core(OwnerA, 1UL << 40, setup, barrelDigest);
            B = new Core(OwnerB, 2UL << 40, setup, barrelDigest);
            A.Command();
            B.Command();
        }

        public Core A { get; }
        public Core B { get; }
        public bool IsCompleted => A.Battle.IsCompleted && B.Battle.IsCompleted;

        public static ScriptedDuel RunToResult(Func<int, int, IReadOnlyCollection<int>, BattleSetup> setup = null,
            bool barrelDigest = false)
        {
            var duel = new ScriptedDuel(setup, barrelDigest);
            while (!duel.IsCompleted && duel.A.Battle.Tick < ScriptedBattle.MaxTicks) duel.Step();
            return duel;
        }

        public void Step()
        {
            if (!A.Battle.IsCompleted) A.Battle.Step();
            if (!B.Battle.IsCompleted) B.Battle.Step();
            A.Record();
            B.Record();
            Relay(A, B);
            Relay(B, A);
            if (A.Battle.Snapshot.Tick % CorrectionPeriod == 0)
            {
                Correct(A, B);
                Correct(B, A);
            }
            A.Think(B);
            B.Think(A);
            A.Command();
            B.Command();
        }

        private static void Relay(Core from, Core to)
        {
            if (to.Battle.IsCompleted) return;
            var snapshot = from.Battle.Snapshot;
            foreach (var operation in snapshot.OutgoingOperations)
            {
                if (!to.Battle.Submit(from.Owner, operation)) throw new InvalidOperationException($"Rejected record {operation.Kind}.");
                from.LastSequence = operation.Sequence;
                ++from.SentOperations[(int)operation.Kind];
            }
            if (!to.Battle.Submit(from.Owner, new OwnerWatermark(to.Battle.BattleId, from.Owner, snapshot.Tick, from.LastSequence)))
                throw new InvalidOperationException("Rejected watermark.");
            foreach (var hit in snapshot.OutgoingHits)
            {
                if (!to.Battle.Submit(new InboundHit(from.Owner, hit))) throw new InvalidOperationException("Rejected hit.");
                ++from.SentHits;
            }
        }

        private void Correct(Core from, Core to)
        {
            if (from.Battle.IsCompleted || to.Battle.IsCompleted) return;
            from.Battle.WriteCorrections(from.Owner, to.Owner, _batch);
            to.HeldCorrections += to.Battle.Submit(from.Owner, _batch);
        }

        private readonly CorrectionBatch _batch = new CorrectionBatch();

        internal sealed class Core
        {
            public Core(int owner, ulong firstCommandId, Func<int, int, IReadOnlyCollection<int>, BattleSetup> setup,
                bool barrelDigest)
            {
                Owner = owner;
                Battle = new Battle(setup(OwnerB, OwnerA, new[] { owner })) { ThrowOnDefect = true };
                _nextCommandId = firstCommandId;
                _barrelDigest = barrelDigest;
            }

            public int Owner { get; }
            public Battle Battle { get; }
            public ulong LastSequence;

            // Records this core sent, per OwnerOperationKind; hits it sent; body corrections it held.
            public readonly int[] SentOperations = new int[Enum.GetValues(typeof(OwnerOperationKind)).Length];
            public int SentHits, HeldCorrections;

            // One line per tick with its full state digest, then one line per event.
            public readonly List<string> Log = new List<string>();

            public void Record()
            {
                var snapshot = Battle.Snapshot;
                if (Log.Count > 0 && snapshot.Tick == _lastRecordedTick) return;
                _lastRecordedTick = snapshot.Tick;
                Log.Add($"{snapshot.Tick} {ScriptedBattle.Digest(snapshot, true, _barrelDigest):X16}");
                foreach (var item in snapshot.Events)
                    if (!ScriptedBattle.IsViewOnly(item.Kind)) Log.Add(item.ToString());
                UpdateRegistrations(snapshot);
            }

            // BattleRunner.Step: pilots on their schedule; each frame goes to this core and to the peer.
            public void Think(Core peer)
            {
                var snapshot = Battle.Snapshot;
                if (snapshot.IsCompleted) return;
                foreach (var registration in _pilots)
                {
                    if (snapshot.Tick % registration.Schedule.Period != registration.Offset) continue;
                    if (!snapshot.TryGetShip(registration.EntityId, out var ship)) continue;
                    if (!registration.Pilot.Think(snapshot, ship, out var controls)) continue;

                    var target = snapshot.Tick + registration.Schedule.Delay;
                    // Barrel requests matter only on the owner; the copy ignores them and takes the owner's records.
                    var frame = ControlFrame.Create(Battle.BattleId, _nextCommandId++, target, registration.EntityId,
                        controls.HasCourse ? controls.Course : (float?)null, controls.Throttle, controls.Systems,
                        controls.TurretTarget, controls.TurretTargetSystems, controls.SearchTurretSystems);
                    Battle.Submit(frame);
                    if (!peer.Battle.IsCompleted) peer.Battle.Submit(frame);
                    if (controls.SelfDestruct)
                        Battle.Submit(new SelfDestructCommand(Battle.BattleId, _nextCommandId++, target, registration.EntityId));
                }
            }

            // ScriptedBattle.Command for the slots of this core's owner.
            public void Command()
            {
                if (Battle.IsCompleted) return;
                var snapshot = Battle.Snapshot;
                var roster = snapshot.Roster;
                var max = Battle.Setup.MaxActiveShipsPerSide;
                for (var side = 0; side < BattleSetup.SideCount; ++side)
                {
                    var active = 0;
                    for (var i = 0; i < roster.Length; ++i)
                        if (roster[i].Side == side && roster[i].Status == RosterStatus.Active) ++active;

                    for (var i = 0; i < roster.Length && active < max; ++i)
                    {
                        if (roster[i].Side != side || roster[i].Owner != Owner || roster[i].Status != RosterStatus.Ready) continue;
                        Battle.Submit(RosterCommand.Deploy(Battle.BattleId, _nextCommandId++, Battle.Tick, roster[i].RosterId));
                        ++active;
                    }
                }

                for (var i = 0; i < roster.Length; ++i)
                {
                    var slot = roster[i];
                    if (slot.Owner != Owner || slot.RosterId.Value % 100 != 1 || slot.Status != RosterStatus.Active ||
                        slot.DeploymentCount != 1) continue;
                    if (!snapshot.TryGetShip(slot.ActiveEntity, out var ship) || ship.IsRetreating) continue;
                    if (ship.Armor.Value < ScriptedBattle.RetreatArmorFraction * ship.Armor.Max)
                        Battle.Submit(RosterCommand.Retreat(Battle.BattleId, _nextCommandId++, Battle.Tick, ship.Id));
                }
            }

            private void UpdateRegistrations(BattleSnapshot snapshot)
            {
                foreach (var item in snapshot.Events)
                {
                    if (item.IsBullet) continue;
                    if (item.Kind == BattleEventKind.Removal)
                        _pilots.RemoveAll(registration => registration.EntityId == item.EntityId);
                    else if (item.Kind == BattleEventKind.Spawn && snapshot.TryGetShip(item.EntityId, out var ship) &&
                        Battle.Setup.IsLocalOwner(ship.Owner))
                    {
                        var pilot = _provider.CreatePilot(snapshot, ship, out var schedule);
                        _pilots.Add(new Registration(item.EntityId, pilot, schedule));
                    }
                }
            }

            private readonly ScriptedBattle.PilotProvider _provider = new ScriptedBattle.PilotProvider();
            private readonly List<Registration> _pilots = new List<Registration>();
            private readonly bool _barrelDigest;
            private ulong _nextCommandId;
            private long _lastRecordedTick;

            private readonly struct Registration
            {
                public Registration(EntityId entityId, IPilot pilot, PilotSchedule schedule)
                {
                    EntityId = entityId;
                    Pilot = pilot;
                    Schedule = schedule;
                    Offset = schedule.OffsetOf(entityId);
                }

                public readonly EntityId EntityId;
                public readonly IPilot Pilot;
                public readonly PilotSchedule Schedule;
                public readonly int Offset;
            }
        }
    }
}
