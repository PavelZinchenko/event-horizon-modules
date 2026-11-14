using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Network;
using NUnit.Framework;

namespace CombatSimulation.Runtime.Tests
{
    // Phase 7, step 4: the match handshake and the setup hash.
    [TestFixture]
    public class MatchHandshakeTests
    {
        private const string Version = "1.0.0";
        private static readonly byte[] Database = { 1, 2, 3, 4 };

        [TestCase(0.0, 0)] [TestCase(0.04, 1)] [TestCase(0.041, 2)] [TestCase(0.1, 3)] [TestCase(0.2, 5)] [TestCase(2.0, 5)]
        [TestCase(double.NaN, 0)]
        public void InputDelayIsTheOneWayLatencyRoundedUpToTicksAndCapped(double roundTrip, int expected) =>
            Assert.AreEqual(expected, MatchHandshake.InputDelay(roundTrip));

        [Test]
        public void EqualPlayersReachReadyWithOneSetupHash()
        {
            var pair = new Pair();
            pair.RunUntil(() => pair.Host.State == HandshakeState.Ready && pair.Guest.State == HandshakeState.Ready);
            Assert.AreNotEqual(0UL, pair.Host.SetupHashValue);
            Assert.AreEqual(pair.Host.SetupHashValue, pair.Guest.SetupHashValue);
            Assert.AreEqual(pair.Host.Setup.BattleId, pair.Guest.Setup.BattleId);
            Assert.AreEqual(77UL, pair.Guest.Setup.MatchSeed);
            Assert.AreEqual(pair.Host.LocalDelay, pair.Guest.Plan.HostDelay);
            Assert.AreEqual(pair.Guest.LocalDelay, pair.Host.Plan.GuestDelay);
            Assert.IsTrue(pair.Host.Setup.IsLocalOwner(MatchPlan.HostOwner));
            Assert.IsTrue(pair.Guest.Setup.IsLocalOwner(MatchPlan.GuestOwner));
            CollectionAssert.AreEqual(new byte[] { 9 }, pair.Guest.Plan.HostFleet);
            CollectionAssert.AreEqual(new byte[] { 8 }, pair.Host.Plan.GuestFleet);
        }

        [Test]
        public void HelloWaitsForClockSamples()
        {
            var pair = new Pair();
            pair.Step();
            Assert.AreEqual(HandshakeState.Measuring, pair.Host.State);
            pair.RunUntil(() => pair.Host.State != HandshakeState.Measuring);
            Assert.GreaterOrEqual(pair.Host.Clock.Samples, MatchHandshake.ClockSamples);
        }

        [Test]
        public void GameVersionMismatchRejectsOnBothPlayers()
        {
            var pair = new Pair(guestVersion: "1.0.1");
            pair.RunUntil(() => pair.Host.State == HandshakeState.Rejected && pair.Guest.State == HandshakeState.Rejected);
            Assert.AreEqual(HandshakeRejection.GameVersion, pair.Host.Rejection);
            Assert.AreEqual(HandshakeRejection.GameVersion, pair.Guest.Rejection);
            Assert.IsNull(pair.Host.Setup);
        }

        [Test]
        public void DatabaseMismatchRejectsOnBothPlayers()
        {
            var pair = new Pair(guestDatabase: new byte[] { 1, 2, 3, 5 });
            pair.RunUntil(() => pair.Host.State == HandshakeState.Rejected && pair.Guest.State == HandshakeState.Rejected);
            Assert.AreEqual(HandshakeRejection.Database, pair.Host.Rejection);
            Assert.AreEqual(HandshakeRejection.Database, pair.Guest.Rejection);
        }

        [Test]
        public void SetupMismatchRejectsOnTheGuestAndReachesTheHost()
        {
            var pair = new Pair(guestArmor: 999);
            pair.RunUntil(() => pair.Host.State == HandshakeState.Rejected && pair.Guest.State == HandshakeState.Rejected);
            Assert.AreEqual(HandshakeRejection.SetupMismatch, pair.Guest.Rejection);
            Assert.IsFalse(pair.Guest.RejectedByPeer);
            Assert.AreEqual(HandshakeRejection.SetupMismatch, pair.Host.Rejection);
            Assert.IsTrue(pair.Host.RejectedByPeer);
        }

        [Test]
        public void FailedSetupBuildRejectsWithTheError()
        {
            var pair = new Pair(guestFails: true);
            pair.RunUntil(() => pair.Host.State == HandshakeState.Rejected && pair.Guest.State == HandshakeState.Rejected);
            Assert.AreEqual(HandshakeRejection.SetupFailed, pair.Guest.Rejection);
            Assert.IsInstanceOf<InvalidOperationException>(pair.Guest.SetupError);
            Assert.AreEqual(HandshakeRejection.SetupFailed, pair.Host.Rejection);
            Assert.IsTrue(pair.Host.RejectedByPeer);
        }

        // The protocol version is read before anything else, so another layout of Hello still gets a clear reason.
        [Test]
        public void OtherProtocolIsRejectedBeforeTheRestOfHello()
        {
            var host = new SessionFixtures.Transport();
            var guest = new SessionFixtures.Transport();
            host.Other = guest; guest.Other = host;
            var receiver = new Receiver();
            var foreign = new PeerConnection(host, receiver, true);
            var handshake = MatchHandshake.Guest(guest, Identity(Version, Database, 8), plan => Build(plan, 100));
            foreign.QueueReliable(new byte[] { 1, (byte)(MatchHandshake.ProtocolVersion + 1), 0, 0xFF });
            var now = 0.0;
            for (var i = 0; i < 50; ++i)
            {
                now += 0.02;
                foreign.Update(now);
                handshake.Update(now);
            }
            Assert.AreEqual(HandshakeState.Rejected, handshake.State);
            Assert.AreEqual(HandshakeRejection.Protocol, handshake.Rejection);
            CollectionAssert.AreEqual(new byte[] { 5, (byte)HandshakeRejection.Protocol }, receiver.Reliable[0]);
        }

        // The host's session runs ticks before the guest creates its own; the guest gets them from the buffer.
        [Test]
        public void SessionsTakeOverTheConnectionAndAgree()
        {
            var pair = new Pair();
            pair.RunUntil(() => pair.Host.State == HandshakeState.Ready);
            var host = pair.Host.CreateSession(new SessionFixtures.Pilots(), pair.Now);
            Assert.Throws<InvalidOperationException>(() => pair.Host.Update(pair.Now));
            NetworkSession guest = null;
            while (host.Tick < 20)
            {
                pair.Now += 0.02;
                if (host.Tick == 1) host.Deploy(new RosterId(1), host.Tick);
                host.Update(pair.Now);
                if (guest != null) guest.Update(pair.Now);
                else if (pair.Guest.State == HandshakeState.Ready && host.Tick >= 5) guest = pair.Guest.CreateSession(new SessionFixtures.Pilots(), pair.Now);
                else pair.Guest.Update(pair.Now);
                Assert.Less(pair.Now, 30, "The match did not start.");
            }
            Assert.IsNotNull(guest);
            while (guest.Tick < host.Tick)
            {
                pair.Now += 0.02;
                host.Update(pair.Now);
                guest.Update(pair.Now);
            }
            Assert.AreEqual(0, guest.InvalidMessages);
            Assert.AreEqual(1, Count(host.Snapshot, MatchPlan.HostOwner));
            Assert.AreEqual(1, Count(guest.Snapshot, MatchPlan.HostOwner));
        }

        // Phase 7, step 8: a peer that never answers ends the handshake after LinkTimeoutSeconds.
        [Test]
        public void SilentPeerTimesOutWithoutAReject()
        {
            var pair = new Pair();
            while (pair.Now < MatchHandshake.LinkTimeoutSeconds - 0.1)
            {
                pair.Now += 0.02;
                pair.Guest.Update(pair.Now);
                Assert.AreEqual(HandshakeState.Measuring, pair.Guest.State);
            }
            pair.Now += 0.2;
            pair.Guest.Update(pair.Now);
            Assert.AreEqual(HandshakeState.Rejected, pair.Guest.State);
            Assert.AreEqual(HandshakeRejection.Timeout, pair.Guest.Rejection);
            Assert.IsFalse(pair.Guest.RejectedByPeer);
            Assert.AreEqual(0, pair.Guest.PendingReliablePackets, "A silent peer gets no Reject.");
        }

        [Test]
        public void SilenceUnderTheLimitIsNoTimeout()
        {
            var pair = new Pair();
            while (pair.Now < MatchHandshake.LinkTimeoutSeconds - 0.5)
            {
                pair.Now += 0.02;
                pair.Guest.Update(pair.Now);
            }
            pair.RunUntil(() => pair.Host.State == HandshakeState.Ready && pair.Guest.State == HandshakeState.Ready);
        }

        [Test]
        public void SetupHashIgnoresLocalOwnersAndCountsDelaysAndValues()
        {
            var host = Build(Plan(true, 2, 3), 100);
            var guest = Build(Plan(false, 2, 3), 100);
            Assert.AreEqual(SetupHash.Compute(host), SetupHash.Compute(guest));
            Assert.AreEqual(SetupHash.Compute(host), SetupHash.Compute(Build(Plan(true, 2, 3), 100)));
            Assert.AreNotEqual(SetupHash.Compute(host), SetupHash.Compute(Build(Plan(true, 2, 4), 100)));
            Assert.AreNotEqual(SetupHash.Compute(host), SetupHash.Compute(Build(Plan(true, 2, 3), 100.0001f)));
        }

        private static int Count(BattleSnapshot snapshot, int owner)
        {
            var count = 0;
            foreach (var ship in snapshot.Ships) if (ship.Owner == owner && !ship.IsDrone) ++count;
            return count;
        }

        private static MatchPlan Plan(bool isHost, int hostDelay, int guestDelay) =>
            new MatchPlan(isHost, new BattleId(5), 77, new byte[] { 9 }, new byte[] { 8 }, hostDelay, guestDelay);

        private static MatchIdentity Identity(string version, byte[] database, byte fleet) =>
            new MatchIdentity(version, database, new[] { fleet });

        private static BattleSetup Build(MatchPlan plan, float armor)
        {
            var ship = new ShipDefinition(1, 1, new EngineDefinition(20, 10, 5, 2), armor,
                new ResourceDefinition(500, 10, 1), new ResourceDefinition(300, 20, 0.5f));
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), MatchPlan.HostSide, ship, MatchPlan.HostOwner),
                new RosterSlotSetup(new RosterId(2), MatchPlan.GuestSide, ship, MatchPlan.GuestOwner),
            };
            return new BattleSetup(plan.BattleId, plan.Seed, roster, localOwners: plan.LocalOwners, ownerDelays: plan.OwnerDelays);
        }

        private sealed class Pair
        {
            internal readonly MatchHandshake Host, Guest;
            internal double Now;

            internal Pair(string guestVersion = Version, byte[] guestDatabase = null, float guestArmor = 100, bool guestFails = false)
            {
                var host = new SessionFixtures.Transport();
                var guest = new SessionFixtures.Transport();
                host.Other = guest; guest.Other = host;
                Host = MatchHandshake.Host(host, Identity(Version, Database, 9), plan => Build(plan, 100), new BattleId(5), 77);
                Guest = MatchHandshake.Guest(guest, Identity(guestVersion, guestDatabase ?? Database, 8),
                    plan => guestFails ? throw new InvalidOperationException("No ships") : Build(plan, guestArmor));
            }

            internal void Step()
            {
                Now += 0.02;
                Host.Update(Now);
                Guest.Update(Now);
            }

            internal void RunUntil(Func<bool> done)
            {
                while (!done())
                {
                    Step();
                    Assert.Less(Now, 30, "The handshake did not finish.");
                }
            }
        }

        private sealed class Receiver : IPeerReceiver
        {
            internal readonly List<byte[]> Reliable = new List<byte[]>();
            public void OnInput(ReadOnlySpan<byte> payload) { }
            public void OnReliable(ReadOnlySpan<byte> payload) => Reliable.Add(payload.ToArray());
            public void OnUnreliable(ReadOnlySpan<byte> payload) { }
        }
    }
}
