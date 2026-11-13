// MovingFloorManagerTests.cs
using System;
using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace Styly.NetSync.Tests
{
    /// <summary>
    /// Every test runs in both per-frame orders. In production NetSyncManager is
    /// [DefaultExecutionOrder(-1000)], so <see cref="MovingFloorManager.Tick"/> runs before the
    /// avatars query (tickFirst = true). The reverse order is kept to prove that nothing
    /// depends on it.
    /// </summary>
    [TestFixture(true)]
    [TestFixture(false)]
    public class MovingFloorManagerTests
    {
        private const int Client = 7;
        private const uint FloorA = 0x0000A001u;
        private const uint FloorB = 0x0000B002u;
        private const double Frame = 0.05;

        private double _now;
        private List<(LogType type, string message)> _logs;
        private List<GameObject> _gameObjects;
        private MovingFloorManager _manager;
        private readonly bool _tickFirst;

        public MovingFloorManagerTests(bool tickFirst)
        {
            _tickFirst = tickFirst;
        }

        [SetUp]
        public void SetUp()
        {
            _now = 100.0;
            _logs = new List<(LogType type, string message)>();
            _gameObjects = new List<GameObject>();
            _manager = new MovingFloorManager(() => _now, (type, message) => _logs.Add((type, message)));
            _manager.Tick();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _gameObjects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
        }

        private Transform NewFloor()
        {
            var go = new GameObject("TestMovingFloor");
            _gameObjects.Add(go);
            return go.transform;
        }

        private static string Encode(uint floorId)
        {
            return MovingFloorManager.EncodeFloorId(floorId);
        }

        /// <summary>
        /// Simulate one frame of the given wall-clock length: the manager ticks and the listed
        /// floor-local avatars query, in the fixture's order.
        /// </summary>
        private void Step(double frameSeconds, int[] queryingClients)
        {
            _now += frameSeconds;
            if (_tickFirst)
            {
                _manager.Tick();
            }

            for (int i = 0; i < queryingClients.Length; i++)
            {
                _manager.TryGetFloorForClient(queryingClients[i], true, out _);
            }

            if (!_tickFirst)
            {
                _manager.Tick();
            }
        }

        private void Run(double seconds, params int[] queryingClients)
        {
            int frames = (int)Math.Round(seconds / Frame);
            for (int i = 0; i < frames; i++)
            {
                Step(Frame, queryingClients);
            }
        }

        private int Count(LogType type)
        {
            int n = 0;
            foreach (var log in _logs)
            {
                if (log.type == type) { n++; }
            }
            return n;
        }

        private string Single(LogType type)
        {
            Assert.AreEqual(1, Count(type), $"Expected exactly one {type} log. Logs: {DumpLogs()}");
            foreach (var log in _logs)
            {
                if (log.type == type) { return log.message; }
            }
            return null;
        }

        private string DumpLogs()
        {
            var lines = new List<string>();
            foreach (var log in _logs)
            {
                lines.Add($"[{log.type}] {log.message}");
            }
            return string.Join(" | ", lines);
        }

        [Test]
        public void Unresolved_WithinGrace_DoesNotWarn()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));

            Run(0.9, Client);
            Assert.AreEqual(1, _manager.ActiveEpisodeCount);

            // The avatar leaves floor-local mode: the episode idles out silently.
            Run(1.0);

            Assert.IsEmpty(_logs, DumpLogs());
            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
            Assert.AreEqual(0, _manager.WarnedEpisodeCount);
        }

        [Test]
        public void Unresolved_ResolvingWithinGrace_EndsSilently()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(0.5, Client);

            _manager.RegisterFloor(FloorA, NewFloor());
            Run(2.0, Client);

            Assert.IsEmpty(_logs, DumpLogs());
            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
        }

        [Test]
        public void Unresolved_BeyondGrace_WarnsExactlyOnce()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));

            Run(10.0, Client);

            string warning = Single(LogType.Warning);
            StringAssert.Contains("client#7", warning);
            StringAssert.Contains("0x0000A001", warning);
            StringAssert.Contains("not registered", warning);
            StringAssert.IsMatch(@"frozen for 1\.[01] s", warning);
            Assert.AreEqual(1, _logs.Count, DumpLogs());
            Assert.AreEqual(1, _manager.WarnedEpisodeCount);
            Assert.AreEqual(0, _manager.SuppressedEpisodeCount);
        }

        [Test]
        public void MissingFloorId_BeyondGrace_WarnsWithMissingReason()
        {
            Run(3.0, Client);

            string warning = Single(LogType.Warning);
            StringAssert.Contains("client#7", warning);
            StringAssert.Contains(MovingFloorManager.ClientVariableName + " is missing", warning);
        }

        [Test]
        public void WarnedEpisode_ThatResolves_LogsRecoveryOnce()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(2.0, Client);
            Assert.AreEqual(1, Count(LogType.Warning));

            var floor = NewFloor();
            _manager.RegisterFloor(FloorA, floor);
            Run(3.0, Client);

            Assert.IsTrue(_manager.TryGetFloorForClient(Client, true, out var resolved));
            Assert.AreSame(floor, resolved);
            string info = Single(LogType.Log);
            // Frozen time runs from the first to the last unresolved query.
            StringAssert.IsMatch(@"resolved after (1\.9|2\.0) s", info);
            StringAssert.Contains("client#7", info);
            StringAssert.Contains("now on floor '0x0000A001'", info);
            StringAssert.Contains("last unresolved state: floor '0x0000A001' not registered", info);
            Assert.AreEqual(1, Count(LogType.Warning));
            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
        }

        [Test]
        public void BoardLeaveBounce_WithinWindow_KeepsSingleWarning()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(1.5, Client);
            Assert.AreEqual(1, Count(LogType.Warning));

            for (int i = 0; i < 10; i++)
            {
                // Leave: the variable clears first; the avatar stays floor-local for a moment,
                // then switches to world space (no queries) and reboards the same floor.
                _manager.SetClientFloorId(Client, "");
                Run(0.2, Client);
                Run(0.2);
                _manager.SetClientFloorId(Client, Encode(FloorA));
                Run(0.6, Client);
            }

            Assert.AreEqual(1, Count(LogType.Warning), DumpLogs());
            Assert.AreEqual(0, Count(LogType.Log), DumpLogs());
            Assert.AreEqual(1, _manager.WarnedEpisodeCount);
        }

        [Test]
        public void ShortRidesOnUnregisteredFloor_NeverWarn()
        {
            for (int i = 0; i < 20; i++)
            {
                // 0.5 s on the floor plus 0.3 s of missing id is one 0.8 s freeze: under grace.
                _manager.SetClientFloorId(Client, Encode(FloorA));
                Run(0.5, Client);
                _manager.SetClientFloorId(Client, "");
                Run(0.3, Client);
                Run(0.7);
            }

            Assert.IsEmpty(_logs, DumpLogs());
            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
        }

        [Test]
        public void SwitchingToRegisteredFloor_ResolvesWarnedEpisode()
        {
            _manager.RegisterFloor(FloorB, NewFloor());
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(2.0, Client);
            Assert.AreEqual(1, Count(LogType.Warning));

            _manager.SetClientFloorId(Client, Encode(FloorB));
            Run(0.1, Client);

            string info = Single(LogType.Log);
            StringAssert.Contains("resolved after", info);
            StringAssert.Contains("now on floor '0x0000B002'", info);
            StringAssert.Contains("last unresolved state: floor '0x0000A001' not registered", info);
            Assert.AreEqual(1, Count(LogType.Warning), DumpLogs());
            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
        }

        [Test]
        public void FloorSwitch_WhileBothUnregistered_IsOneEpisode()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(2.0, Client);
            StringAssert.Contains("0x0000A001", Single(LogType.Warning));

            _manager.SetClientFloorId(Client, Encode(FloorB));
            Run(5.0, Client);

            Assert.AreEqual(1, _logs.Count, DumpLogs());
            Assert.AreEqual(1, _manager.ActiveEpisodeCount);
            Assert.AreEqual(1, _manager.WarnedEpisodeCount);

            // The closing line reports the state the episode ended in.
            _manager.RemoveClient(Client);
            string info = Single(LogType.Log);
            StringAssert.IsMatch(@"ended after (6\.9|7\.0) s without resolving", info);
            StringAssert.Contains("floor '0x0000B002' not registered", info);
        }

        [Test]
        public void FloorSwitch_WithinGrace_DoesNotRestartGrace()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(0.6, Client);
            _manager.SetClientFloorId(Client, Encode(FloorB));
            Run(0.3, Client);
            Assert.IsEmpty(_logs, DumpLogs());

            Run(0.3, Client);

            // One continuous freeze of more than 1 s, reported with the current floor.
            string warning = Single(LogType.Warning);
            StringAssert.Contains("0x0000B002", warning);
            StringAssert.IsMatch(@"frozen for 1\.[01] s", warning);
            Assert.AreEqual(1, _manager.ActiveEpisodeCount);
        }

        [Test]
        public void WarnedEpisode_ThenMissingId_ThenOtherFloor_WarnsOnce()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(2.0, Client);
            StringAssert.Contains("not registered", Single(LogType.Warning));

            _manager.SetClientFloorId(Client, "");
            Run(3.0, Client);
            Assert.AreEqual(1, _logs.Count, DumpLogs());
            Assert.AreEqual(1, _manager.ActiveEpisodeCount);

            _manager.SetClientFloorId(Client, Encode(FloorB));
            Run(3.0, Client);
            Assert.AreEqual(1, _logs.Count, DumpLogs());
            Assert.AreEqual(1, _manager.ActiveEpisodeCount);
            Assert.AreEqual(1, _manager.WarnedEpisodeCount);

            _manager.SetClientFloorId(Client, "");
            Run(1.0, Client);
            Run(1.0);

            string info = Single(LogType.Log);
            StringAssert.IsMatch(@"ended after (8\.9|9\.0) s without resolving \(missing floor id\)", info);
            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
        }

        [Test]
        public void MissingIdWithinGrace_ThenUnregisteredFloor_WarnsOnContinuousFrozenTime()
        {
            Run(0.6, Client);
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(0.6, Client);

            string warning = Single(LogType.Warning);
            StringAssert.Contains("0x0000A001' is not registered", warning);
            StringAssert.IsMatch(@"frozen for 1\.[01] s", warning);
        }

        [Test]
        public void UnregisterFloor_WhileResolved_StartsEpisode()
        {
            _manager.RegisterFloor(FloorA, NewFloor());
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(3.0, Client);
            Assert.IsEmpty(_logs, DumpLogs());
            Assert.AreEqual(0, _manager.ActiveEpisodeCount);

            _manager.UnregisterFloor(FloorA);
            Run(0.9, Client);
            Assert.IsEmpty(_logs, DumpLogs());
            Assert.AreEqual(1, _manager.ActiveEpisodeCount);

            Run(0.3, Client);
            string warning = Single(LogType.Warning);
            StringAssert.Contains("0x0000A001' is not registered", warning);
            StringAssert.IsMatch(@"frozen for 1\.[01] s", warning);

            _manager.RegisterFloor(FloorA, NewFloor());
            Run(0.1, Client);
            StringAssert.Contains("resolved after", Single(LogType.Log));
            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
        }

        [Test]
        public void DestroyedFloorTransform_WarnsWithItsOwnReason()
        {
            var floor = NewFloor();
            _manager.RegisterFloor(FloorA, floor);
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(1.0, Client);
            Assert.IsEmpty(_logs, DumpLogs());

            // The floor object is destroyed without UnregisterFloor.
            UnityEngine.Object.DestroyImmediate(floor.gameObject);
            Assert.IsFalse(_manager.TryGetFloorForClient(Client, false, out var resolved));
            Assert.IsTrue(resolved == null);
            Run(1.5, Client);

            string warning = Single(LogType.Warning);
            StringAssert.Contains("0x0000A001", warning);
            StringAssert.Contains("Transform has been destroyed", warning);
            StringAssert.DoesNotContain("not registered", warning);

            // A destroyed registration can be replaced, which resolves the episode.
            var replacement = NewFloor();
            Assert.IsTrue(_manager.RegisterFloor(FloorA, replacement));
            Run(0.1, Client);
            string info = Single(LogType.Log);
            StringAssert.Contains("resolved after", info);
            StringAssert.Contains("last unresolved state: floor '0x0000A001' Transform destroyed", info);
            Assert.IsTrue(_manager.TryGetFloorForClient(Client, false, out resolved));
            Assert.AreSame(replacement, resolved);
        }

        [Test]
        public void ResolvedDuration_IsMeasuredToTheLastFrozenQuery()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(2.0, Client);
            Assert.AreEqual(1, Count(LogType.Warning));

            // The avatar leaves floor-local mode for a while (no queries, under the idle
            // timeout), then comes back with the floor registered. The avatar was not known
            // to be frozen during that gap, so it must not be counted.
            Run(0.4);
            _manager.RegisterFloor(FloorA, NewFloor());
            Run(0.1, Client);

            StringAssert.IsMatch(@"resolved after (1\.9|2\.0) s", Single(LogType.Log));
        }

        [Test]
        public void IrrelevantAvatar_IsCountedButNotWarned()
        {
            var asked = new List<int>();
            _manager.RelevancePredicate = clientNo =>
            {
                asked.Add(clientNo);
                return false;
            };
            _manager.SetClientFloorId(Client, Encode(FloorA));

            Run(10.0, Client);

            Assert.AreEqual(0, Count(LogType.Warning), DumpLogs());
            Assert.AreEqual(0, Count(LogType.Error), DumpLogs());
            Assert.AreEqual(1, _manager.SuppressedEpisodeCount);
            Assert.AreEqual(0, _manager.WarnedEpisodeCount);
            // Asked at grace expiry and then about once per second, never per frame.
            Assert.That(asked.Count, Is.InRange(8, 10));
            CollectionAssert.AreEqual(new[] { Client }, new HashSet<int>(asked));
        }

        [Test]
        public void IrrelevantAvatar_ThatBecomesRelevant_Warns()
        {
            bool relevant = false;
            _manager.RelevancePredicate = _ => relevant;
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(5.0, Client);
            Assert.AreEqual(0, Count(LogType.Warning));

            relevant = true;
            Run(1.1, Client);

            StringAssert.Contains("client#7", Single(LogType.Warning));
            Assert.AreEqual(1, _manager.WarnedEpisodeCount);
            Assert.AreEqual(1, _manager.SuppressedEpisodeCount);

            Run(5.0, Client);
            Assert.AreEqual(1, Count(LogType.Warning));
        }

        [Test]
        public void RelevancePredicate_IsPerClient()
        {
            _manager.RelevancePredicate = clientNo => clientNo == Client;
            _manager.SetClientFloorId(Client, Encode(FloorA));
            _manager.SetClientFloorId(8, Encode(FloorA));

            Run(3.0, Client, 8);

            StringAssert.Contains("client#7", Single(LogType.Warning));
            Assert.AreEqual(1, _manager.SuppressedEpisodeCount);
        }

        [Test]
        public void ThrowingPredicate_IsTreatedAsRelevant_AndLoggedOnce()
        {
            _manager.RelevancePredicate = _ => throw new InvalidOperationException("boom");
            _manager.SetClientFloorId(Client, Encode(FloorA));
            _manager.SetClientFloorId(8, Encode(FloorA));

            Run(3.0, Client, 8);

            Assert.AreEqual(2, Count(LogType.Warning), DumpLogs());
            StringAssert.Contains("boom", Single(LogType.Error));
            Assert.AreEqual(0, _manager.SuppressedEpisodeCount);
        }

        [Test]
        public void RemoveClient_EndsEpisode()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(2.0, Client);
            Assert.AreEqual(1, Count(LogType.Warning));

            _manager.RemoveClient(Client);

            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
            string info = Single(LogType.Log);
            StringAssert.IsMatch(@"ended after (1\.9|2\.0) s without resolving", info);
            StringAssert.Contains("client removed", info);

            // A respawned avatar starts a fresh episode with a fresh grace period.
            Run(0.9, Client);
            Assert.AreEqual(1, Count(LogType.Warning));
            Run(0.5, Client);
            Assert.AreEqual(2, Count(LogType.Warning));
        }

        [Test]
        public void RemoveClient_BeforeWarning_EndsSilently()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(0.5, Client);

            _manager.RemoveClient(Client);
            Run(3.0);

            Assert.IsEmpty(_logs, DumpLogs());
            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
        }

        [Test]
        public void WarnedEpisode_WhoseAvatarStopsQuerying_EndsAndIsRemoved()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(2.0, Client);

            Run(0.4);
            Assert.AreEqual(1, _manager.ActiveEpisodeCount);
            Run(0.3);

            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
            StringAssert.Contains("without resolving", Single(LogType.Log));
        }

        [Test]
        public void UntrackedLookups_DoNotTouchEpisodes()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));

            for (int i = 0; i < 100; i++)
            {
                _now += Frame;
                Assert.IsFalse(_manager.TryGetFloorForClient(Client, false, out _));
                _manager.Tick();
            }
            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
            Assert.IsEmpty(_logs, DumpLogs());

            // Untracked lookups also do not keep a tracked episode alive or close it.
            Run(0.5, Client);
            for (int i = 0; i < 20; i++)
            {
                _now += Frame;
                _manager.TryGetFloorForClient(Client, false, out _);
                _manager.Tick();
            }
            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
            Assert.IsEmpty(_logs, DumpLogs());
        }

        [Test]
        public void FrameHitch_DoesNotAgeEpisodePastGrace()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(0.3, Client);

            // App pause / long hitch: 30 s of wall time pass in a single frame.
            Step(30.0, new[] { Client });

            Assert.IsEmpty(_logs, DumpLogs());
            Assert.AreEqual(1, _manager.ActiveEpisodeCount);

            Run(1.0, Client);
            Assert.AreEqual(1, Count(LogType.Warning));
        }

        [Test]
        public void SuppressedSummary_IsLoggedAtMostOncePerInterval_AndOnlyWhenNonzero()
        {
            Run(61.0);
            Assert.IsEmpty(_logs, DumpLogs());

            _manager.RelevancePredicate = _ => false;
            _manager.SetClientFloorId(Client, Encode(FloorA));
            _manager.SetClientFloorId(8, Encode(FloorA));
            Run(60.0, Client, 8);

            string summary = Single(LogType.Log);
            StringAssert.Contains("2 unresolved episode(s) suppressed", summary);

            // Nothing new was suppressed: no further summaries.
            Run(130.0, Client, 8);
            Assert.AreEqual(1, _logs.Count, DumpLogs());
        }

        [Test]
        public void ClearClientStates_EndsAllEpisodes()
        {
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Run(2.0, Client, 8);
            Assert.AreEqual(2, _manager.ActiveEpisodeCount);

            _manager.ClearClientStates(true);

            Assert.AreEqual(0, _manager.ActiveEpisodeCount);
            Assert.AreEqual(2, Count(LogType.Log), DumpLogs());
        }

        [Test]
        public void ClearClientStates_ThenSeed_RestoresFloorIdFromCache()
        {
            var floor = NewFloor();
            _manager.RegisterFloor(FloorA, floor);
            var variables = NewVariablesWiredToManager();
            variables.HandleClientVariableSync(ClientVariableSync(Client, MovingFloorManager.ClientVariableName, Encode(FloorA)));
            Assert.IsTrue(_manager.TryGetFloorForClient(Client, false, out _));

            _manager.ClearClientStates(false);
            Assert.IsFalse(_manager.TryGetFloorForClient(Client, false, out _));

            _manager.SeedClientFloorId(Client, variables);

            Assert.IsTrue(_manager.TryGetFloorForClient(Client, true, out var resolved));
            Assert.AreSame(floor, resolved);
            Run(3.0, Client);
            Assert.IsEmpty(_logs, DumpLogs());
            variables.Dispose();
        }

        [Test]
        public void RoomSwitchCacheClear_DropsValuesSilently_AndNextSyncFiresChange()
        {
            _manager.RegisterFloor(FloorA, NewFloor());
            var variables = NewVariablesWiredToManager();
            int changeEvents = 0;
            variables.OnClientVariableChanged += (clientNo, name, oldValue, newValue) => changeEvents++;
            var sync = ClientVariableSync(Client, MovingFloorManager.ClientVariableName, Encode(FloorA));
            variables.HandleClientVariableSync(sync);
            Assert.AreEqual(1, changeEvents);

            // What NetSyncManager.SetRoomId does.
            variables.ClearClientVariableCache();
            _manager.ClearClientStates(true);

            Assert.AreEqual(1, changeEvents, "Clearing the cache must not fire change events.");
            Assert.IsFalse(variables.TryGetCachedClientVariable(MovingFloorManager.ClientVariableName, Client, out _));
            Assert.IsEmpty(variables.GetAllClientVariables(Client));

            // The same client number in the new room is somebody else: nothing to seed.
            _manager.SeedClientFloorId(Client, variables);
            Assert.IsFalse(_manager.TryGetFloorForClient(Client, false, out _));

            // The new room's sync is a change again, even for an identical value.
            variables.HandleClientVariableSync(sync);
            Assert.AreEqual(2, changeEvents);
            Assert.IsTrue(_manager.TryGetFloorForClient(Client, false, out _));
            variables.Dispose();
        }

        [Test]
        public void Predicate_ThatClearsClientStates_DoesNotThrowOrWarn()
        {
            _manager.RelevancePredicate = _ =>
            {
                // e.g. the application switches rooms from inside the predicate.
                _manager.ClearClientStates(true);
                return true;
            };
            _manager.SetClientFloorId(Client, Encode(FloorA));
            _manager.SetClientFloorId(8, Encode(FloorA));

            Assert.DoesNotThrow(() => Run(1.5, Client, 8));

            // The episodes were ended by the predicate itself, so nothing may warn for them.
            Assert.IsEmpty(_logs, DumpLogs());
            Assert.AreEqual(0, _manager.WarnedEpisodeCount);
        }

        [Test]
        public void Predicate_ThatRemovesItsClient_SuppressesTheWarning()
        {
            bool removed = false;
            _manager.RelevancePredicate = clientNo =>
            {
                if (!removed)
                {
                    removed = true;
                    _manager.RemoveClient(clientNo);
                }
                return true;
            };

            Assert.DoesNotThrow(() => Run(1.3, Client));

            Assert.IsTrue(removed);
            Assert.IsEmpty(_logs, DumpLogs());
            Assert.AreEqual(0, _manager.WarnedEpisodeCount);
        }

        [Test]
        public void LogHandler_ThatReentersTheManager_DoesNotThrow()
        {
            MovingFloorManager manager = null;
            var logs = new List<(LogType type, string message)>();
            manager = new MovingFloorManager(
                () => _now,
                (type, message) =>
                {
                    logs.Add((type, message));
                    // e.g. an Application.logMessageReceived handler that reacts to warnings.
                    manager.Tick();
                    manager.ClearClientStates(true);
                });
            _manager = manager;
            _manager.Tick();
            _manager.SetClientFloorId(Client, Encode(FloorA));
            _manager.SetClientFloorId(8, Encode(FloorA));
            _manager.SetClientFloorId(9, Encode(FloorA));

            Assert.DoesNotThrow(() => Run(3.0, Client, 8, 9));

            // Each warning is still closed by exactly one Info line.
            int warnings = 0;
            int infos = 0;
            foreach (var log in logs)
            {
                if (log.type == LogType.Warning) { warnings++; }
                if (log.type == LogType.Log) { infos++; }
            }
            Assert.Greater(warnings, 0);
            Assert.AreEqual(warnings, infos);

            // Re-entering Tick from outside a Tick (closing line of RemoveClient) is fine too.
            _manager.SetClientFloorId(Client, Encode(FloorA));
            Assert.DoesNotThrow(() => Run(0.5, Client));
            Assert.DoesNotThrow(() => _manager.RemoveClient(Client));
        }

        [Test]
        public void SteadyState_WithActiveEpisodes_DoesNotAllocate()
        {
            // One warned episode, one suppressed episode (re-asks the predicate every second)
            // and one resolved client.
            _manager.RegisterFloor(FloorB, NewFloor());
            _manager.RelevancePredicate = clientNo => clientNo != 8;
            _manager.SetClientFloorId(Client, Encode(FloorA));
            _manager.SetClientFloorId(8, Encode(FloorA));
            _manager.SetClientFloorId(9, Encode(FloorB));
            var clients = new[] { Client, 8, 9 };
            for (int i = 0; i < 100; i++)
            {
                Step(Frame, clients);
            }
            Assert.AreEqual(2, _manager.ActiveEpisodeCount);
            Assert.AreEqual(1, _manager.WarnedEpisodeCount);
            Assert.AreEqual(1, _manager.SuppressedEpisodeCount);
            int logsBefore = _logs.Count;

            // 20 s of frames: crosses many relevance rechecks for the suppressed episode.
            Assert.That(
                () =>
                {
                    for (int i = 0; i < 400; i++)
                    {
                        Step(Frame, clients);
                    }
                },
                new NotConstraint(new AllocatingGCMemoryConstraint()));

            Assert.AreEqual(logsBefore, _logs.Count, DumpLogs());
            Assert.AreEqual(2, _manager.ActiveEpisodeCount);
        }

        [Test]
        public void RespawnedAvatar_IsReseededFromVariableCache_WithoutVariableChange()
        {
            var floor = NewFloor();
            _manager.RegisterFloor(FloorA, floor);

            var variables = NewVariablesWiredToManager();
            int changeEvents = 0;
            variables.OnClientVariableChanged += (clientNo, name, oldValue, newValue) => changeEvents++;

            var sync = ClientVariableSync(Client, MovingFloorManager.ClientVariableName, Encode(FloorA));
            variables.HandleClientVariableSync(sync);
            Assert.AreEqual(1, changeEvents);
            Assert.IsTrue(_manager.TryGetFloorForClient(Client, true, out _));

            // The avatar is removed (e.g. the sender app was paused) ...
            _manager.RemoveClient(Client);
            Assert.IsFalse(_manager.TryGetFloorForClient(Client, false, out _));

            // ... and the server repeats the unchanged value, which fires no change event.
            variables.HandleClientVariableSync(sync);
            Assert.AreEqual(1, changeEvents);
            Assert.IsFalse(_manager.TryGetFloorForClient(Client, false, out _));

            // Respawn re-seeds from the cache.
            _manager.SeedClientFloorId(Client, variables);

            Assert.IsTrue(_manager.TryGetFloorForClient(Client, true, out var resolved));
            Assert.AreSame(floor, resolved);
            Run(3.0, Client);
            Assert.IsEmpty(_logs, DumpLogs());
            variables.Dispose();
        }

        [Test]
        public void Seed_WithoutCachedVariable_LeavesClientUnassigned()
        {
            _manager.RegisterFloor(FloorA, NewFloor());
            var variables = new NetworkVariableManager(null, "test-device", null);

            _manager.SeedClientFloorId(Client, variables);
            _manager.SeedClientFloorId(Client, null);

            Assert.IsFalse(_manager.TryGetFloorForClient(Client, false, out _));
            variables.Dispose();
        }

        /// <summary>Wire the variable-changed event the same way NetSyncManager does.</summary>
        private NetworkVariableManager NewVariablesWiredToManager()
        {
            var variables = new NetworkVariableManager(null, "test-device", null);
            variables.OnClientVariableChanged += (clientNo, name, oldValue, newValue) =>
            {
                if (name == MovingFloorManager.ClientVariableName)
                {
                    _manager.SetClientFloorId(clientNo, newValue);
                }
            };
            return variables;
        }

        private static Dictionary<string, object> ClientVariableSync(int clientNo, string name, string value)
        {
            return new Dictionary<string, object>
            {
                {
                    "clientVariables",
                    new Dictionary<string, object>
                    {
                        {
                            clientNo.ToString(),
                            new object[]
                            {
                                new Dictionary<string, object> { { "name", name }, { "value", value } },
                            }
                        },
                    }
                },
            };
        }
    }
}
