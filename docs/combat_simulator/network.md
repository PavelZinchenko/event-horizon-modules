# Network library

Part of [Combat simulator](index.md).

Migration phase 3b, step 1 is implemented. Network has no reference to Core, Physics, Runtime or Unity.
Runtime uses it through `NetworkSession` (step 6). Core remains independent of Network.

## Peer and transport contracts

1. Create one `PeerConnection` per remote peer and match. Both endpoints use the same maximum datagram size, default 1,200 bytes.
2. Supply an `ITransport` that sends and receives whole datagrams without a delivery guarantee. The caller owns its lifetime.
3. `TrySend` consumes or copies the supplied bytes before returning. A false return means local backpressure, not packet loss.
4. `TryReceive` consumes one datagram and reports its original length, even when the receive buffer is too small. The peer rejects oversized packets.
5. Call `Update(now)` regularly with finite, nondecreasing monotonic seconds. It receives packets, then sends one datagram with everything queued since the last call:
   input and unreliable sections, acknowledgements, clock probes, reliable retries and new reliable data.
6. All state belongs to the instance. Use one thread at a time; receiver callbacks must not reenter the peer or throw.
7. Queue methods copy their payloads. `IPeerReceiver` callbacks expose borrowed spans that expire when the callback returns.
8. Use a fresh transport and peer for each match. Session negotiation, authentication, reconnects and disconnect policy remain phase 7 work.

## Channels and encoding

`ByteWriter` and `ByteReader` encode integers and floating-point bits explicitly in little-endian order.
Failed reads and writes leave their position unchanged. The codec uses no streams or per-operation heap objects.
`BitWriter` and `BitReader` pack fixed-width fields and `VarUInt` values, least significant bit first.
`VarUInt` is the Utilites `EliasGammaEncoder` code with section size 4: 0 takes 1 bit, 1–15 take 6, 16–255 take 11.
The reader rejects non-minimal codes. `Finish` zeroes the padding of the last byte.
The byte codec also has a byte-aligned `VarUInt`: unsigned LEB128, 7 bits per byte. 0–127 take 1 byte; non-minimal codes are rejected.

**One datagram per update (G3, packet format version 3).** A datagram is the version byte, then one or more sections to its end.
A section is a LEB128 header (payload length × 8 + kind), then its payload: 1 byte for payloads under 16 bytes, 2 bytes under 2,048.
Kinds are input (1), reliable data (2), acknowledgement (3), clock probe (4), clock reply (5), and plain unreliable data (6).
The receiver checks the framing of the whole datagram first, so a malformed datagram delivers none of its sections.
Then it dispatches the sections in order. `InvalidPackets` counts malformed datagrams and sections with invalid contents.
Payload contents remain opaque to Network.

1. Input and unreliable sections are queued until the next `Update`. A section that does not fit sends the datagram before it.
2. `Update` adds the clock reply, the probe and the acknowledgement, then reliable retries, then new reliable data in the space left.
   More data than one datagram holds continues in more datagrams.
3. A datagram of only acknowledgements, probes and replies waits up to 1.5 ticks (30 ms) for data to join it. Runtime sends data
   about every tick, so these sections rarely travel alone. A reply carries its own receive and send times, so the wait does not bias
   the clock estimate. Cost: an acknowledgement can arrive up to one tick later.
4. A lost datagram loses all its sections. Input copies, reliable resends and periodic corrections cover the loss.
5. A refused send (local backpressure) drops the datagram. Its reliable packets keep no sequence and wait for the next update.
6. Measured, dogfight sender A, Perfect (`Results/N-bandwidth-2026-10-25-g3.md`): 137 → 49 datagrams/s; payload 5.95 → 5.39 KB/s;
   7.2 KB/s with the 38-byte Relay header. At cap: 53 datagrams/s, 7.4 KB/s with the Relay header.

Channels:

1. **Inputs:** `TrySendInput(payload)` queues one input section; `OnInput(payload)` receives it whole. `MaxInputPayload` gives its size limit.
   Network adds no history, redundancy or record framing. Runtime owns the input encoding, change detection and copies ([Controls](runtime_session.md#controls)).
2. **Reliable messages:** the reliable channel is one ordered byte stream. Each message is a LEB128 length, then its bytes.
   The stream is cut into packets; each reliable section carries a 32-bit sequence number, then stream bytes. A message can span packets.
   A packet is cut at its first send, to the space its datagram has left, and resent unchanged. It takes all unsent bytes, or at
   least 64, or it starts a datagram. Messages queued between two `Update` calls share a packet. The unsent stream keeps growing
   while the window is full. So the window (64 packets) limits packets in flight, not messages.
   The receiver holds gaps, appends data in sequence order and delivers each complete message once, in submission order.
   Empty messages are valid. Sequence arithmetic wraps at 32 bits; fewer than half the sequence space may remain outstanding.
   Measured reason (step 9a): one datagram per message filled the window at At cap; each loss at the window base stopped sending.
3. **Acknowledgements:** the next contiguous receive sequence acknowledges all earlier packets; a mask acknowledges buffered packets after it.
   The mask travels as a `VarUInt` without its always-clear first bit: usually 1 byte, so an acknowledgement section is 6 bytes.
   The sender retries only missing packets. Duplicate packets trigger another acknowledgement, including during receive-only traffic.
   The initial retry timeout is 200 ms. Later it is twice the smoothed round-trip time, clamped to 100–1,000 ms.
   A packet sent before one the peer has acknowledged is probably lost. It is retried after 1.25 round trips instead,
   at least 50 ms and at most the normal timeout (`TickClock.FastResendSeconds`). Reordering can cause a harmless duplicate.
4. **Plain unreliable data:** `TrySendUnreliable` queues one opaque payload without history or fragmentation. `MaxUnreliablePayload` gives its size limit.
   Runtime uses it for corrections. Input and plain unreliable delivery do not wait for reliable gaps.

Packet buffers and reliable reassembly storage retain their capacities for reuse.
Capacity growth may allocate; warmed steady traffic, including retries and reassembly, allocates zero bytes in the tests.
Reliable queues survive blackouts without dropping messages. `PendingReliableBytes` gives the queued and unacknowledged
stream; the session thread sets its limit (phase 7, step 8). `LastReceiveTime` is the update time of the last well-formed
datagram (before the first, of the first update); disconnect timeouts measure the silence from it.
An acknowledged stream with a malformed length prefix faults the connection (`IsFaulted`); it never silently discards an application message.
A reliable section without data, or with more data than a datagram can carry, is invalid and consumes no sequence.

## Tick clock

One peer is the reference clock, selected at construction. Both peers exchange probes every 250 ms.
Four timestamps measure round-trip time excluding remote processing time and estimate the reference clock's offset.
The offset uses the lowest-round-trip sample among the latest 16 samples. Round-trip smoothing uses a weight of 1/8 for each new sample.
Duplicate or expired probe replies cannot refresh the estimate.

1. Wait for the follower's `Clock.HasEstimate`. Match setup supplies both peers with the same start epoch in reference-clock seconds.
2. Call `Clock.Start(localTime, referenceStartTime)` once. A future epoch is allowed; tick zero becomes due at that epoch.
3. After start, `PeerConnection.Update` advances the clock. `TickPosition` and `IsTickDue(nextTick)` tell Runtime which ticks to execute.
4. Runtime must execute every due tick in order with the fixed 0.02-second simulation step. Network never calls the core.
5. The reference runs at rate 1. The follower adjusts its rate toward the estimated reference time over a one-second horizon.
   The rate stays between 0.98 and 1.02. New estimates never snap or rewind the running tick position.

**Asymmetric delay:** round-trip synchronization cannot distinguish clock offset from directional latency imbalance.
The 40/120 ms test exposes a 40 ms bias, or two actual ticks, despite a stable estimate.
The owner kept the one-tick gate limit unchanged (2026-10-22). The gate must measure actual peer tick differences and report failures.

Validation (2026-10-22): 45 Network tests and all 617 engine-free tests pass.
Tests cover loopback, seeded loss, duplication, reordering, delay, a 500 ms blackout, sequence wraparound, fragmentation and zero warmed allocations.
Clock tests cover distinct clock origins, jitter, oscillator drift, pacing limits and asymmetric bias.
Network and Network.Tests compile separately with Unity 6000.0.75f1's compiler, .NET Standard 2.1 references and warnings treated as errors.
The scripted battle still ends at tick 3506; its four existing acceptance tests pass. Unity metadata is supplied; Editor import remains a separate check.
