// MovingFloorManager.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Tracks moving floors for protocol v5 moving-floor-local avatar poses.
    /// A moving floor is any scene-stable Transform known by the same 32-bit id on each client.
    /// </summary>
    internal class MovingFloorManager
    {
        public const string ClientVariableName = NetSyncManager.PrefixForSystem + "movingFloor";

        public const uint UnassignedFloorId = 0u;

        private readonly Dictionary<uint, Transform> _floors = new Dictionary<uint, Transform>();
        private readonly Dictionary<int, uint> _clientFloorIds = new Dictionary<int, uint>();

        // --- Unresolved-floor warning episodes -------------------------------------------
        // A remote avatar that is in moving-floor-local mode queries its floor every frame.
        // While the floor cannot be resolved the avatar holds its last pose, which is only a
        // problem worth a warning when it lasts long enough for a visitor to notice AND the
        // avatar is actually relevant (visible) to this viewer. Short gaps around boarding /
        // leaving and floors that are intentionally inactive on this device are by design.

        /// <summary>Continuous unresolved time before an episode may warn.</summary>
        internal const double WarningGraceSeconds = 1.0;

        /// <summary>An episode ends when its avatar has not queried for longer than this.</summary>
        internal const double EpisodeIdleTimeoutSeconds = 0.5;

        /// <summary>Interval at which a not-yet-warned episode re-asks the relevance predicate.</summary>
        internal const double RelevanceRecheckIntervalSeconds = 1.0;

        /// <summary>Minimum interval between suppressed-episode summary logs.</summary>
        internal const double SummaryIntervalSeconds = 60.0;

        /// <summary>
        /// Upper bound for how far one <see cref="Tick"/> advances episode time. A frame hitch
        /// or app pause must neither age an episode past the grace period nor idle it out.
        /// </summary>
        internal const double MaxTickDeltaSeconds = 0.25;

        private enum UnresolvedReason : byte
        {
            /// <summary>The client's floor id variable has not been received (or is empty).</summary>
            MissingFloorId,
            /// <summary>No Transform is registered for the client's floor id on this device.</summary>
            FloorNotRegistered,
            /// <summary>The floor id is registered but its Transform has been destroyed.</summary>
            FloorDestroyed,
        }

        /// <summary>
        /// One continuous freeze of one remote avatar. Keyed by client number only: the grace
        /// period and the warning are about how long the avatar has been frozen, so a change
        /// of floor id or reason in the middle neither restarts the grace period nor warns again.
        /// </summary>
        private sealed class UnresolvedEpisode
        {
            // Floor id and reason as of the last query; used for messages only.
            public uint FloorId;
            public UnresolvedReason Reason;
            public double StartTime;
            public double LastQueryTime;
            public double NextRelevanceCheckTime;
            public bool Warned;
            public bool Suppressed;
        }

        private readonly Dictionary<int, UnresolvedEpisode> _episodes = new Dictionary<int, UnresolvedEpisode>();
        private readonly List<int> _episodeClientScratch = new List<int>();
        private bool _isTicking;

        private readonly Func<double> _clock;
        private readonly Action<LogType, string> _log;
        private double _lastClockValue;
        private bool _hasClockValue;
        private double _episodeTime;
        private double _nextSummaryTime = SummaryIntervalSeconds;
        private int _warnedEpisodeCount;
        private int _suppressedEpisodeCount;
        private int _summarizedSuppressedEpisodeCount;
        private bool _relevancePredicateExceptionLogged;

        private uint _localFloorId;

        /// <summary>
        /// Optional predicate: is a held (frozen) remote avatar of this client relevant to the
        /// local viewer? Null means every avatar is relevant.
        /// </summary>
        public Func<int, bool> RelevancePredicate { get; set; }

        /// <summary>Total episodes that produced a warning.</summary>
        public int WarnedEpisodeCount => _warnedEpisodeCount;

        /// <summary>
        /// Total episodes that outlasted the grace period but were judged not relevant. An
        /// episode that later becomes relevant and warns is counted in both totals.
        /// </summary>
        public int SuppressedEpisodeCount => _suppressedEpisodeCount;

        /// <summary>Episodes currently tracked (unresolved and not yet ended).</summary>
        public int ActiveEpisodeCount => _episodes.Count;

        public MovingFloorManager()
            : this(null, null)
        {
        }

        /// <param name="clock">Monotonic seconds source; defaults to unscaled realtime.</param>
        /// <param name="log">Log sink; defaults to the Unity console.</param>
        internal MovingFloorManager(Func<double> clock, Action<LogType, string> log)
        {
            _clock = clock ?? DefaultClock;
            _log = log ?? DefaultLog;
            // The clock is first read in Tick: this constructor runs from a MonoBehaviour
            // field initializer, where Unity time APIs must not be called.
        }

        private static double DefaultClock()
        {
            return Time.realtimeSinceStartupAsDouble;
        }

        private static void DefaultLog(LogType type, string message)
        {
            Debug.unityLogger.Log(type, (object)message);
        }

        public uint LocalFloorId => _localFloorId;
        public bool HasLocalFloor => _localFloorId != UnassignedFloorId;

        public bool RegisterFloor(uint floorId, Transform floor)
        {
            if (floorId == UnassignedFloorId || floor == null)
            {
                return false;
            }

            // Reject duplicate registration with a different Transform so the
            // second component's OnDisable cannot unregister the still-needed
            // first one. Re-registration with the same Transform is idempotent.
            if (_floors.TryGetValue(floorId, out var existing) && existing != null && existing != floor)
            {
                Debug.LogWarning(
                    $"[NetSync] Moving floor id '{FormatFloorId(floorId)}' is already registered to a different Transform. Ignoring duplicate registration.");
                return false;
            }

            // Episodes waiting on this floor close on their avatar's next query.
            _floors[floorId] = floor;
            return true;
        }

        public void UnregisterFloor(uint floorId)
        {
            if (floorId == UnassignedFloorId)
            {
                return;
            }

            _floors.Remove(floorId);
        }

        public bool BoardLocal(uint floorId)
        {
            if (floorId == UnassignedFloorId)
            {
                return false;
            }

            if (!_floors.TryGetValue(floorId, out var floor) || floor == null)
            {
                return false;
            }

            _localFloorId = floorId;
            return true;
        }

        public void LeaveLocal()
        {
            _localFloorId = UnassignedFloorId;
        }

        public bool TryGetLocalFloor(out Transform floor)
        {
            floor = null;
            if (_localFloorId == UnassignedFloorId)
            {
                return false;
            }

            return _floors.TryGetValue(_localFloorId, out floor) && floor != null;
        }

        public void SetClientFloorId(int clientNo, string encodedFloorId)
        {
            if (clientNo <= 0)
            {
                return;
            }

            // Episodes are deliberately left alone here: riders bounce between board and
            // leave, and a flip of the variable must not restart the grace period or produce
            // a fresh warning. Episodes end by resolving, idling out, or client removal.
            if (string.IsNullOrEmpty(encodedFloorId))
            {
                _clientFloorIds.Remove(clientNo);
                return;
            }

            if (!TryParseFloorId(encodedFloorId, out var floorId))
            {
                _clientFloorIds.Remove(clientNo);
                Debug.LogWarning(
                    $"[NetSync] Ignoring invalid moving floor id '{encodedFloorId}' for client#{clientNo}.");
                return;
            }

            _clientFloorIds[clientNo] = floorId;
        }

        /// <summary>
        /// Seed a client's floor id from the client-variable cache. The id normally arrives
        /// through variable-changed events, which fire only when the value changes, while
        /// <see cref="RemoveClient"/> drops it whenever the client disconnects. An avatar
        /// that respawns with an unchanged floor id is restored through this method.
        /// </summary>
        public void SeedClientFloorId(int clientNo, NetworkVariableManager variables)
        {
            if (variables != null &&
                variables.TryGetCachedClientVariable(ClientVariableName, clientNo, out var encodedFloorId))
            {
                SetClientFloorId(clientNo, encodedFloorId);
            }
        }

        public void RemoveClient(int clientNo)
        {
            _clientFloorIds.Remove(clientNo);
            EndEpisode(clientNo, "client removed");
        }

        /// <summary>
        /// Resolve the moving floor a remote client is riding.
        /// </summary>
        /// <param name="trackUnresolved">
        /// True only for the once-per-frame query that drives the avatar pose
        /// (<c>NetSyncAvatar.Update</c>). That query feeds the unresolved-episode tracking
        /// behind the warnings. Secondary lookups (late re-apply, hand visibility) pass false
        /// and are pure: they neither start, extend, nor end an episode.
        /// </param>
        public bool TryGetFloorForClient(int clientNo, bool trackUnresolved, out Transform floor)
        {
            floor = null;
            if (!_clientFloorIds.TryGetValue(clientNo, out var floorId))
            {
                floorId = UnassignedFloorId;
            }

            UnresolvedReason reason;
            if (floorId == UnassignedFloorId)
            {
                reason = UnresolvedReason.MissingFloorId;
            }
            else if (!_floors.TryGetValue(floorId, out floor))
            {
                reason = UnresolvedReason.FloorNotRegistered;
            }
            else if (floor == null)
            {
                reason = UnresolvedReason.FloorDestroyed;
            }
            else
            {
                if (trackUnresolved && _episodes.Count > 0)
                {
                    ResolveEpisode(clientNo, floorId);
                }
                return true;
            }

            floor = null;
            if (trackUnresolved)
            {
                TouchEpisode(clientNo, floorId, reason);
            }
            return false;
        }

        /// <summary>
        /// Advance episode time and evaluate episodes. Call once per frame. In production this
        /// runs from <c>NetSyncManager.Update</c>, which executes before the avatars query.
        /// </summary>
        public void Tick()
        {
            // The relevance predicate and the log sink are caller code and may call back in.
            if (_isTicking)
            {
                return;
            }

            _isTicking = true;
            try
            {
                TickCore();
            }
            finally
            {
                _isTicking = false;
            }
        }

        private void TickCore()
        {
            double clockValue = _clock();
            double delta = _hasClockValue ? clockValue - _lastClockValue : 0d;
            _lastClockValue = clockValue;
            _hasClockValue = true;
            if (delta > 0d)
            {
                _episodeTime += delta < MaxTickDeltaSeconds ? delta : MaxTickDeltaSeconds;
            }

            if (_episodes.Count > 0)
            {
                EvaluateEpisodes();
            }

            if (_episodeTime >= _nextSummaryTime)
            {
                _nextSummaryTime = _episodeTime + SummaryIntervalSeconds;
                int newlySuppressed = _suppressedEpisodeCount - _summarizedSuppressedEpisodeCount;
                if (newlySuppressed > 0)
                {
                    _summarizedSuppressedEpisodeCount = _suppressedEpisodeCount;
                    _log(
                        LogType.Log,
                        $"[NetSync] Moving floor: {newlySuppressed} unresolved episode(s) suppressed as not relevant to this viewer since the last summary (totals: {_suppressedEpisodeCount} suppressed, {_warnedEpisodeCount} warned).");
                }
            }
        }

        public void ClearClientStates(bool clearLocal)
        {
            _clientFloorIds.Clear();
            if (clearLocal)
            {
                _localFloorId = UnassignedFloorId;
            }

            // Ending an episode can log, and a log handler may call back in. Take one key at
            // a time instead of iterating, and leave the shared scratch list to EvaluateEpisodes.
            while (_episodes.Count > 0)
            {
                int clientNo = 0;
                foreach (var pair in _episodes)
                {
                    clientNo = pair.Key;
                    break;
                }

                EndEpisode(clientNo, "client states cleared");
            }
        }

        public static string FormatFloorId(uint floorId)
        {
            return floorId == UnassignedFloorId ? "(unassigned)" : $"0x{floorId:X8}";
        }

        public static string EncodeFloorId(uint floorId)
        {
            return floorId == UnassignedFloorId ? string.Empty : $"0x{floorId:X8}";
        }

        /// <summary>
        /// Yaw of a rotation around world up, computed from the projected forward vector.
        /// </summary>
        public static float ExtractYawDegrees(Quaternion rotation)
        {
            var fwd = rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-8f)
            {
                return 0f;
            }

            fwd.Normalize();
            return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        }

        private static bool TryParseFloorId(string encodedFloorId, out uint floorId)
        {
            floorId = UnassignedFloorId;
            if (string.IsNullOrWhiteSpace(encodedFloorId))
            {
                return false;
            }

            string s = encodedFloorId.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("#", StringComparison.Ordinal))
            {
                s = s.Substring(s[0] == '#' ? 1 : 2);
                return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out floorId) &&
                    floorId != UnassignedFloorId;
            }

            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out floorId) &&
                floorId != UnassignedFloorId;
        }

        private void TouchEpisode(int clientNo, uint floorId, UnresolvedReason reason)
        {
            if (!_episodes.TryGetValue(clientNo, out var episode))
            {
                episode = new UnresolvedEpisode
                {
                    StartTime = _episodeTime,
                    NextRelevanceCheckTime = _episodeTime + WarningGraceSeconds,
                };
                _episodes[clientNo] = episode;
            }

            episode.LastQueryTime = _episodeTime;
            episode.FloorId = floorId;
            episode.Reason = reason;
        }

        private void EvaluateEpisodes()
        {
            _episodeClientScratch.Clear();
            foreach (var pair in _episodes)
            {
                _episodeClientScratch.Add(pair.Key);
            }

            for (int i = 0; i < _episodeClientScratch.Count; i++)
            {
                int clientNo = _episodeClientScratch[i];

                // An earlier iteration's predicate or log handler may have removed episodes
                // (for example by switching rooms).
                if (!_episodes.TryGetValue(clientNo, out var episode))
                {
                    continue;
                }

                // The avatar queries every frame while it is floor-local, whether or not
                // packets arrive. Silence therefore means it left floor-local mode or was
                // destroyed, not that the stream stalled.
                if (_episodeTime - episode.LastQueryTime > EpisodeIdleTimeoutSeconds)
                {
                    EndEpisode(clientNo, "avatar no longer in moving-floor-local mode");
                    continue;
                }

                // Judge by the last query rather than the current time, so an avatar that
                // stopped querying inside the grace period cannot warn while it idles out.
                if (episode.Warned || episode.LastQueryTime < episode.NextRelevanceCheckTime)
                {
                    continue;
                }

                episode.NextRelevanceCheckTime = episode.LastQueryTime + RelevanceRecheckIntervalSeconds;
                bool relevant = IsRelevant(clientNo);

                // The predicate ran caller code: skip if it ended this episode.
                if (!_episodes.TryGetValue(clientNo, out var current) || !ReferenceEquals(current, episode))
                {
                    continue;
                }

                if (relevant)
                {
                    episode.Warned = true;
                    _warnedEpisodeCount++;
                    _log(LogType.Warning, BuildWarningMessage(clientNo, episode));
                }
                else if (!episode.Suppressed)
                {
                    episode.Suppressed = true;
                    _suppressedEpisodeCount++;
                }
            }
        }

        private bool IsRelevant(int clientNo)
        {
            var predicate = RelevancePredicate;
            if (predicate == null)
            {
                return true;
            }

            try
            {
                return predicate(clientNo);
            }
            catch (Exception ex)
            {
                // A broken predicate must never hide a real problem.
                if (!_relevancePredicateExceptionLogged)
                {
                    _relevancePredicateExceptionLogged = true;
                    _log(
                        LogType.Error,
                        $"[NetSync] Moving-floor warning relevance predicate threw; treating avatars as relevant. This is logged once. {ex}");
                }
                return true;
            }
        }

        /// <summary>Seconds the avatar is known to have been frozen: first to last unresolved query.</summary>
        private static string FormatFrozenSeconds(UnresolvedEpisode episode)
        {
            return (episode.LastQueryTime - episode.StartTime).ToString("F1", CultureInfo.InvariantCulture);
        }

        private static string BuildWarningMessage(int clientNo, UnresolvedEpisode episode)
        {
            string prefix = $"[NetSync] Avatar of client#{clientNo} has been frozen for {FormatFrozenSeconds(episode)} s: ";
            switch (episode.Reason)
            {
                case UnresolvedReason.MissingFloorId:
                    return prefix + $"it sends moving-floor-local poses but {ClientVariableName} is missing. Holding the last applied pose until the floor id is received.";
                case UnresolvedReason.FloorDestroyed:
                    return prefix + $"moving floor '{FormatFloorId(episode.FloorId)}' is registered on this device but its Transform has been destroyed. Holding the last applied pose until a live Transform is registered for it.";
                default:
                    return prefix + $"moving floor '{FormatFloorId(episode.FloorId)}' is not registered on this device. Holding the last applied pose until it is registered.";
            }
        }

        private static string DescribeUnresolvedState(UnresolvedEpisode episode)
        {
            switch (episode.Reason)
            {
                case UnresolvedReason.MissingFloorId:
                    return "missing floor id";
                case UnresolvedReason.FloorDestroyed:
                    return $"floor '{FormatFloorId(episode.FloorId)}' Transform destroyed";
                default:
                    return $"floor '{FormatFloorId(episode.FloorId)}' not registered";
            }
        }

        // Only an episode that warned reports its end, so every warning in a log is paired
        // with exactly one closing line. Both methods remove the episode before logging.

        /// <summary>The client's floor resolved: close its episode, if any.</summary>
        private void ResolveEpisode(int clientNo, uint resolvedFloorId)
        {
            if (!_episodes.Remove(clientNo, out var episode) || !episode.Warned)
            {
                return;
            }

            _log(
                LogType.Log,
                $"[NetSync] Moving floor for client#{clientNo} resolved after {FormatFrozenSeconds(episode)} s: now on floor '{FormatFloorId(resolvedFloorId)}' (last unresolved state: {DescribeUnresolvedState(episode)}).");
        }

        /// <summary>Close a client's episode, if any, that ended without resolving.</summary>
        private void EndEpisode(int clientNo, string endReason)
        {
            if (!_episodes.Remove(clientNo, out var episode) || !episode.Warned)
            {
                return;
            }

            _log(
                LogType.Log,
                $"[NetSync] Unresolved moving floor for client#{clientNo} ended after {FormatFrozenSeconds(episode)} s without resolving ({DescribeUnresolvedState(episode)}): {endReason}.");
        }
    }
}
