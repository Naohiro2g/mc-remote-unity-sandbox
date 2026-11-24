using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Code2Create.UnitySandbox.World;

namespace Code2Create.UnitySandbox.Server.Sync
{
    public sealed class ActiveStreamInfo
    {
        public string StreamId { get; }
        public string Dimension { get; }
        public Int3 Origin { get; }

        public ActiveStreamInfo(string streamId, string dimension, Int3 origin)
        {
            StreamId = streamId;
            Dimension = dimension;
            Origin = origin;
        }
    }

    /// <summary>
    /// Persistent association between browser viewerId, player UUID, and paired build stream.
    /// </summary>
    public interface IViewerStreamBindingStore
    {
        StreamBuildContext GetOrCreateStreamContext(string streamId);
        void BindViewerToStream(string viewerId, string streamId);
        string GetOrCreatePlayerUuid(string viewerId);
        ActiveStreamInfo? GetActiveStreamForViewer(string viewerId);
        IReadOnlyList<string> GetViewersForStream(string streamId);
        void UpdateStreamOrigin(string streamId, string dimension, Int3 origin);
    }

    public sealed class InMemoryViewerStreamBindingStore : IViewerStreamBindingStore
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, StreamBuildContext> streams = new Dictionary<string, StreamBuildContext>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> viewerToStream = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> viewerToPlayerUuid = new Dictionary<string, string>(StringComparer.Ordinal);

        public StreamBuildContext GetOrCreateStreamContext(string streamId)
        {
            if (string.IsNullOrEmpty(streamId)) throw new ArgumentException("streamId is required.", nameof(streamId));
            lock (gate)
            {
                if (!streams.TryGetValue(streamId, out var ctx))
                {
                    ctx = new StreamBuildContext(streamId, WorldConstants.DefaultDimension, Int3.Zero);
                    streams[streamId] = ctx;
                }
                return ctx;
            }
        }

        public void BindViewerToStream(string viewerId, string streamId)
        {
            if (string.IsNullOrEmpty(viewerId)) throw new ArgumentException("viewerId is required.", nameof(viewerId));
            if (string.IsNullOrEmpty(streamId)) throw new ArgumentException("streamId is required.", nameof(streamId));

            lock (gate)
            {
                if (!streams.ContainsKey(streamId))
                {
                    streams[streamId] = new StreamBuildContext(streamId, WorldConstants.DefaultDimension, Int3.Zero);
                }
                if (!viewerToPlayerUuid.ContainsKey(viewerId))
                {
                    viewerToPlayerUuid[viewerId] = Guid.NewGuid().ToString("D");
                }
                viewerToStream[viewerId] = streamId;
            }
        }

        public string GetOrCreatePlayerUuid(string viewerId)
        {
            if (string.IsNullOrEmpty(viewerId)) throw new ArgumentException("viewerId is required.", nameof(viewerId));
            lock (gate)
            {
                if (!viewerToPlayerUuid.TryGetValue(viewerId, out var uuid))
                {
                    uuid = Guid.NewGuid().ToString("D");
                    viewerToPlayerUuid[viewerId] = uuid;
                }
                return uuid;
            }
        }

        public ActiveStreamInfo? GetActiveStreamForViewer(string viewerId)
        {
            if (string.IsNullOrEmpty(viewerId)) return null;
            lock (gate)
            {
                if (!viewerToStream.TryGetValue(viewerId, out var streamId)) return null;
                if (!streams.TryGetValue(streamId, out var ctx)) return null;
                return new ActiveStreamInfo(ctx.StreamId, ctx.Dimension, ctx.Origin);
            }
        }

        public IReadOnlyList<string> GetViewersForStream(string streamId)
        {
            lock (gate)
            {
                return viewerToStream
                    .Where(kv => string.Equals(kv.Value, streamId, StringComparison.Ordinal))
                    .Select(kv => kv.Key)
                    .ToArray();
            }
        }

        public void UpdateStreamOrigin(string streamId, string dimension, Int3 origin)
        {
            lock (gate)
            {
                if (!streams.TryGetValue(streamId, out var ctx))
                {
                    ctx = new StreamBuildContext(streamId, dimension, origin);
                    streams[streamId] = ctx;
                }
                else
                {
                    ctx.Dimension = dimension;
                    ctx.Origin = origin;
                }
            }
        }
    }

    public sealed class SandboxTokenRecord
    {
        public string Token { get; }
        public string TokenType { get; }
        public string ViewerId { get; }
        public string StreamId { get; }
        public string PlayerUuid { get; }
        public DateTimeOffset IssuedAt { get; }
        public DateTimeOffset? ExpiresAt { get; }

        public SandboxTokenRecord(
            string token,
            string tokenType,
            string viewerId,
            string streamId,
            string playerUuid,
            DateTimeOffset issuedAt,
            DateTimeOffset? expiresAt)
        {
            Token = token;
            TokenType = tokenType;
            ViewerId = viewerId;
            StreamId = streamId;
            PlayerUuid = playerUuid;
            IssuedAt = issuedAt;
            ExpiresAt = expiresAt;
        }
    }

    public enum TokenResolutionStatus
    {
        Active,
        NotFound,
        Expired,
        Invalid
    }

    public sealed class TokenResolutionResult
    {
        public TokenResolutionStatus Status { get; }
        public SandboxTokenRecord? Record { get; }

        private TokenResolutionResult(TokenResolutionStatus status, SandboxTokenRecord? record)
        {
            Status = status;
            Record = record;
        }

        public static TokenResolutionResult Active(SandboxTokenRecord record) => new TokenResolutionResult(TokenResolutionStatus.Active, record);
        public static TokenResolutionResult NotFound() => new TokenResolutionResult(TokenResolutionStatus.NotFound, null);
        public static TokenResolutionResult Expired() => new TokenResolutionResult(TokenResolutionStatus.Expired, null);
        public static TokenResolutionResult Invalid() => new TokenResolutionResult(TokenResolutionStatus.Invalid, null);
    }

    /// <summary>
    /// Stores issued bearer tokens bound to Unity Web viewerId / playerUuid / streamId.
    /// Intentionally independent from SandboxWorld resets (2026-11-14-01).
    /// </summary>
    public interface ISandboxTokenStore
    {
        SandboxTokenRecord IssueToken(string tokenType, string viewerId, string streamId, string playerUuid, DateTimeOffset now);
        TokenResolutionResult ResolveToken(string token, DateTimeOffset now);
    }

    public sealed class InMemorySandboxTokenStore : ISandboxTokenStore
    {
        private static readonly Regex WellFormedTokenRegex = new Regex(@"^(mcrs|mcrl)_[A-Za-z0-9_-]{8,}$", RegexOptions.Compiled);
        private readonly ConcurrentDictionary<string, SandboxTokenRecord> records = new ConcurrentDictionary<string, SandboxTokenRecord>(StringComparer.Ordinal);

        public SandboxTokenRecord IssueToken(string tokenType, string viewerId, string streamId, string playerUuid, DateTimeOffset now)
        {
            if (tokenType != "session" && tokenType != "long_lived")
            {
                throw new ArgumentException($"Unsupported tokenType: '{tokenType}'.", nameof(tokenType));
            }
            if (string.IsNullOrEmpty(viewerId)) throw new ArgumentException("viewerId is required.", nameof(viewerId));
            if (string.IsNullOrEmpty(streamId)) throw new ArgumentException("streamId is required.", nameof(streamId));
            if (string.IsNullOrEmpty(playerUuid)) throw new ArgumentException("playerUuid is required.", nameof(playerUuid));

            string prefix = tokenType == "long_lived" ? "mcrl_" : "mcrs_";
            DateTimeOffset? expiresAt = tokenType == "long_lived"
                ? null
                : now.AddSeconds(WorldConstants.DefaultSessionTokenTtlSeconds);

            Span<byte> randomBytes = stackalloc byte[24];
            RandomNumberGenerator.Fill(randomBytes);
            string hex = Convert.ToHexString(randomBytes).ToLowerInvariant();
            string token = prefix + hex;

            var record = new SandboxTokenRecord(token, tokenType, viewerId, streamId, playerUuid, now, expiresAt);
            records[token] = record;
            return record;
        }

        public TokenResolutionResult ResolveToken(string token, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(token))
            {
                return TokenResolutionResult.Invalid();
            }

            if (token.StartsWith("mcrp_", StringComparison.Ordinal))
            {
                return TokenResolutionResult.NotFound();
            }

            if (!WellFormedTokenRegex.IsMatch(token))
            {
                return TokenResolutionResult.Invalid();
            }

            if (!records.TryGetValue(token, out var record))
            {
                return TokenResolutionResult.NotFound();
            }

            if (record.ExpiresAt.HasValue && record.ExpiresAt.Value <= now)
            {
                records.TryRemove(token, out _);
                return TokenResolutionResult.Expired();
            }

            return TokenResolutionResult.Active(record);
        }
    }

    public sealed class PairingBeginResult
    {
        public string PairingId { get; }
        public string PairCode { get; }
        public int ExpiresIn { get; }

        public PairingBeginResult(string pairingId, string pairCode, int expiresIn)
        {
            PairingId = pairingId;
            PairCode = pairCode;
            ExpiresIn = expiresIn;
        }
    }

    public enum PairingPollStatus
    {
        Pending,
        Approved,
        Expired,
        NotFound
    }

    public sealed class PairingPollResult
    {
        public PairingPollStatus Status { get; }
        public string? Token { get; }

        private PairingPollResult(PairingPollStatus status, string? token)
        {
            Status = status;
            Token = token;
        }

        public static PairingPollResult Pending() => new PairingPollResult(PairingPollStatus.Pending, null);
        public static PairingPollResult Approved(string token) => new PairingPollResult(PairingPollStatus.Approved, token);
        public static PairingPollResult Expired() => new PairingPollResult(PairingPollStatus.Expired, null);
        public static PairingPollResult NotFound() => new PairingPollResult(PairingPollStatus.NotFound, null);
    }

    public sealed class PairingApprovalOutcome
    {
        public bool Approved { get; }
        public string? StreamId { get; }
        public string? Reason { get; }

        private PairingApprovalOutcome(bool approved, string? streamId, string? reason)
        {
            Approved = approved;
            StreamId = streamId;
            Reason = reason;
        }

        public static PairingApprovalOutcome Success(string streamId) => new PairingApprovalOutcome(true, streamId, null);
        public static PairingApprovalOutcome Failure(string reason) => new PairingApprovalOutcome(false, null, reason);
    }

    /// <summary>
    /// Connection-independent pending pairing store keyed by pairing_id (for auth.pairPoll) and 6-digit pair_code (for /mcremote pair).
    /// </summary>
    public interface IPairingApprovalStore
    {
        PairingBeginResult BeginPairing(string tokenType, string? device, DateTimeOffset now);
        PairingBeginResult? TryBeginPairing(string tokenType, string? device, DateTimeOffset now);
        PairingPollResult PollPairing(string pairingId, DateTimeOffset now);
        PairingApprovalOutcome TryApproveCode(
            string pairCode,
            string viewerId,
            IViewerStreamBindingStore bindingStore,
            ISandboxTokenStore tokenStore,
            DateTimeOffset now);
        void RegisterPendingCode(string pairCode, string streamId, DateTimeOffset expiresAt);
        PairingApprovalOutcome TryConsumeCode(string pairCode, DateTimeOffset now);
    }

    public sealed class InMemoryPairingApprovalStore : IPairingApprovalStore
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, PendingPairingEntry> byId = new Dictionary<string, PendingPairingEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingPairingEntry> byDigits = new Dictionary<string, PendingPairingEntry>(StringComparer.Ordinal);
        private readonly int maxPendingPairCodes;
        private readonly TimeSpan expiredRetentionGrace;
        private readonly int maxTotalEntries;
        private long streamCounter;
        private long entryOrderCounter;

        public InMemoryPairingApprovalStore(
            int maxPendingPairCodes = 128,
            TimeSpan? expiredRetentionGrace = null,
            int maxTotalEntries = 256)
        {
            if (maxPendingPairCodes <= 0) throw new ArgumentOutOfRangeException(nameof(maxPendingPairCodes));
            if (maxTotalEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxTotalEntries));

            this.maxPendingPairCodes = maxPendingPairCodes;
            this.expiredRetentionGrace = expiredRetentionGrace ?? TimeSpan.FromSeconds(60);
            if (this.expiredRetentionGrace < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(expiredRetentionGrace));
            }
            this.maxTotalEntries = maxTotalEntries;
        }

        public int PendingPairCodeCount
        {
            get
            {
                lock (gate) return byDigits.Count;
            }
        }

        public int TrackedEntryCount
        {
            get
            {
                lock (gate) return byId.Count;
            }
        }

        public PairingBeginResult BeginPairing(string tokenType, string? device, DateTimeOffset now)
        {
            return TryBeginPairing(tokenType, device, now)
                ?? throw new InvalidOperationException("Pending pair code capacity reached.");
        }

        public PairingBeginResult? TryBeginPairing(string tokenType, string? device, DateTimeOffset now)
        {
            if (tokenType != "session" && tokenType != "long_lived")
            {
                throw new ArgumentException($"Unsupported tokenType: '{tokenType}'.", nameof(tokenType));
            }

            lock (gate)
            {
                PurgeLocked(now);
                if (byDigits.Count >= maxPendingPairCodes)
                {
                    return null;
                }

                // Evict oldest already-expired (tombstone in grace) entries before rejecting on hard entry cap.
                while (byId.Count >= maxTotalEntries)
                {
                    PendingPairingEntry? oldestExpired = null;
                    foreach (var candidate in byId.Values)
                    {
                        if (candidate.ExpiresAt <= now &&
                            (oldestExpired == null || candidate.CreatedOrder < oldestExpired.CreatedOrder))
                        {
                            oldestExpired = candidate;
                        }
                    }

                    if (oldestExpired == null)
                    {
                        return null;
                    }

                    RemoveEntryByIdLocked(oldestExpired.PairingId);
                }

                string pairingId = "pair_" + Guid.NewGuid().ToString("N");
                string digits = AllocateUniquePairDigitsLocked();
                DateTimeOffset expiresAt = now.AddSeconds(WorldConstants.DefaultPairCodeTtlSeconds);
                long order = Interlocked.Increment(ref entryOrderCounter);

                var entry = new PendingPairingEntry(pairingId, digits, null, tokenType, device, expiresAt, order);
                byId[pairingId] = entry;
                byDigits[digits] = entry;
                return new PairingBeginResult(pairingId, digits, WorldConstants.DefaultPairCodeTtlSeconds);
            }
        }

        public PairingPollResult PollPairing(string pairingId, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(pairingId))
            {
                return PairingPollResult.NotFound();
            }

            lock (gate)
            {
                PurgeLocked(now);
                if (!byId.TryGetValue(pairingId, out var entry))
                {
                    return PairingPollResult.NotFound();
                }

                if (entry.ApprovedToken != null)
                {
                    byId.Remove(pairingId);
                    return PairingPollResult.Approved(entry.ApprovedToken);
                }

                if (entry.ExpiresAt <= now)
                {
                    RemoveEntryByIdLocked(pairingId);
                    return PairingPollResult.Expired();
                }

                return PairingPollResult.Pending();
            }
        }

        public PairingApprovalOutcome TryApproveCode(
            string pairCode,
            string viewerId,
            IViewerStreamBindingStore bindingStore,
            ISandboxTokenStore tokenStore,
            DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(viewerId)) throw new ArgumentException("viewerId is required.", nameof(viewerId));
            if (bindingStore == null) throw new ArgumentNullException(nameof(bindingStore));
            if (tokenStore == null) throw new ArgumentNullException(nameof(tokenStore));

            string digits = ExtractAsciiDigits(pairCode);
            if (digits.Length != 6)
            {
                return PairingApprovalOutcome.Failure("invalid_pair_code_format");
            }

            lock (gate)
            {
                PurgeLocked(now);
                if (!byDigits.TryGetValue(digits, out var entry))
                {
                    return PairingApprovalOutcome.Failure("invalid_pair_code");
                }

                if (entry.ExpiresAt <= now)
                {
                    byDigits.Remove(digits);
                    return PairingApprovalOutcome.Failure("pair_code_expired");
                }

                byDigits.Remove(digits);

                string streamId = entry.ExplicitStreamId
                    ?? bindingStore.GetActiveStreamForViewer(viewerId)?.StreamId
                    ?? ("stream-" + Interlocked.Increment(ref streamCounter));

                bindingStore.BindViewerToStream(viewerId, streamId);
                string playerUuid = bindingStore.GetOrCreatePlayerUuid(viewerId);
                var tokenRecord = tokenStore.IssueToken(entry.TokenType, viewerId, streamId, playerUuid, now);
                entry.ApprovedToken = tokenRecord.Token;

                return PairingApprovalOutcome.Success(streamId);
            }
        }

        public void RegisterPendingCode(string pairCode, string streamId, DateTimeOffset expiresAt)
        {
            if (string.IsNullOrEmpty(pairCode)) throw new ArgumentException("pairCode is required.", nameof(pairCode));
            if (string.IsNullOrEmpty(streamId)) throw new ArgumentException("streamId is required.", nameof(streamId));

            string digits = ExtractAsciiDigits(pairCode);
            if (digits.Length != 6)
            {
                throw new ArgumentException("pairCode must contain 6 ASCII digits.", nameof(pairCode));
            }

            lock (gate)
            {
                string pairingId = "pair_" + Guid.NewGuid().ToString("N");
                long order = Interlocked.Increment(ref entryOrderCounter);
                var entry = new PendingPairingEntry(pairingId, digits, streamId, "session", null, expiresAt, order);
                byId[pairingId] = entry;
                byDigits[digits] = entry;
            }
        }

        public PairingApprovalOutcome TryConsumeCode(string pairCode, DateTimeOffset now)
        {
            string digits = ExtractAsciiDigits(pairCode);
            if (digits.Length != 6)
            {
                return PairingApprovalOutcome.Failure("invalid_pair_code");
            }

            lock (gate)
            {
                PurgeLocked(now);
                if (!byDigits.TryGetValue(digits, out var entry))
                {
                    return PairingApprovalOutcome.Failure("invalid_pair_code");
                }

                byDigits.Remove(digits);
                if (entry.ExpiresAt <= now)
                {
                    return PairingApprovalOutcome.Failure("pair_code_expired");
                }

                return PairingApprovalOutcome.Success(entry.ExplicitStreamId ?? ("stream-" + Interlocked.Increment(ref streamCounter)));
            }
        }

        private string AllocateUniquePairDigitsLocked()
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                string candidate = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                if (!byDigits.ContainsKey(candidate))
                {
                    return candidate;
                }
            }
            throw new InvalidOperationException("Failed to allocate unique pair_code.");
        }

        private void RemoveEntryByIdLocked(string pairingId)
        {
            if (byId.Remove(pairingId, out var entry) &&
                byDigits.TryGetValue(entry.PairDigits, out var current) &&
                ReferenceEquals(current, entry))
            {
                byDigits.Remove(entry.PairDigits);
            }
        }

        private void PurgeLocked(DateTimeOffset now)
        {
            if (byDigits.Count > 0)
            {
                var expiredKeys = byDigits.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToArray();
                foreach (var key in expiredKeys)
                {
                    byDigits.Remove(key);
                }
            }

            if (byId.Count > 0)
            {
                var purgedIds = byId
                    .Where(kv => kv.Value.ExpiresAt + expiredRetentionGrace <= now)
                    .Select(kv => kv.Key)
                    .ToArray();
                foreach (var pairingId in purgedIds)
                {
                    RemoveEntryByIdLocked(pairingId);
                }
            }
        }

        private static string ExtractAsciiDigits(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var chars = new char[6];
            int count = 0;
            foreach (char c in raw)
            {
                if (c >= '0' && c <= '9')
                {
                    if (count >= 6) return string.Empty;
                    chars[count++] = c;
                }
            }
            return count == 6 ? new string(chars, 0, count) : string.Empty;
        }

        private sealed class PendingPairingEntry
        {
            public string PairingId { get; }
            public string PairDigits { get; }
            public string? ExplicitStreamId { get; }
            public string TokenType { get; }
            public string? Device { get; }
            public DateTimeOffset ExpiresAt { get; }
            public long CreatedOrder { get; }
            public string? ApprovedToken { get; set; }

            public PendingPairingEntry(
                string pairingId,
                string pairDigits,
                string? explicitStreamId,
                string tokenType,
                string? device,
                DateTimeOffset expiresAt,
                long createdOrder)
            {
                PairingId = pairingId;
                PairDigits = pairDigits;
                ExplicitStreamId = explicitStreamId;
                TokenType = tokenType;
                Device = device;
                ExpiresAt = expiresAt;
                CreatedOrder = createdOrder;
            }
        }
    }

    public sealed class RateLimitDecision
    {
        public bool Allowed { get; }
        public int RemainingAttempts { get; }

        public RateLimitDecision(bool allowed, int remainingAttempts)
        {
            Allowed = allowed;
            RemainingAttempts = remainingAttempts;
        }
    }

    /// <summary>
    /// Prevents brute-force attempts on 6-digit NNN-NNN pairing codes per viewerId and per WebSocket connection.
    /// </summary>
    public sealed class PairingRateLimiter
    {
        private readonly object gate = new object();
        private readonly int maxAttempts;
        private readonly TimeSpan window;
        private readonly Dictionary<string, Queue<DateTimeOffset>> failuresByKey = new Dictionary<string, Queue<DateTimeOffset>>(StringComparer.Ordinal);

        public int MaxAttempts => maxAttempts;

        public PairingRateLimiter(int maxAttempts = 5, TimeSpan? window = null)
        {
            if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            this.maxAttempts = maxAttempts;
            this.window = window ?? TimeSpan.FromMinutes(1);
        }

        public RateLimitDecision CheckAllowed(string connectionKey, string? viewerId, DateTimeOffset now)
        {
            lock (gate)
            {
                int connUsed = CountRecentFailuresLocked("conn:" + connectionKey, now);
                int viewerUsed = string.IsNullOrEmpty(viewerId) ? 0 : CountRecentFailuresLocked("viewer:" + viewerId, now);
                int used = Math.Max(connUsed, viewerUsed);
                int remaining = Math.Max(0, maxAttempts - used);
                return new RateLimitDecision(remaining > 0, remaining);
            }
        }

        public int RecordFailure(string connectionKey, string? viewerId, DateTimeOffset now)
        {
            lock (gate)
            {
                RecordLocked("conn:" + connectionKey, now);
                if (!string.IsNullOrEmpty(viewerId))
                {
                    RecordLocked("viewer:" + viewerId, now);
                }

                int connUsed = CountRecentFailuresLocked("conn:" + connectionKey, now);
                int viewerUsed = string.IsNullOrEmpty(viewerId) ? 0 : CountRecentFailuresLocked("viewer:" + viewerId, now);
                return Math.Max(0, maxAttempts - Math.Max(connUsed, viewerUsed));
            }
        }

        private int CountRecentFailuresLocked(string key, DateTimeOffset now)
        {
            if (!failuresByKey.TryGetValue(key, out var queue)) return 0;
            PruneLocked(queue, now);
            return queue.Count;
        }

        private void RecordLocked(string key, DateTimeOffset now)
        {
            if (!failuresByKey.TryGetValue(key, out var queue))
            {
                queue = new Queue<DateTimeOffset>();
                failuresByKey[key] = queue;
            }
            PruneLocked(queue, now);
            queue.Enqueue(now);
        }

        private void PruneLocked(Queue<DateTimeOffset> queue, DateTimeOffset now)
        {
            DateTimeOffset cutoff = now - window;
            while (queue.Count > 0 && queue.Peek() <= cutoff)
            {
                queue.Dequeue();
            }
        }
    }

    /// <summary>
    /// Sliding-window rate limiter for pre-hello auth.pairBegin calls (per source IP and global).
    /// </summary>
    public sealed class PairBeginRateLimiter
    {
        private static readonly TimeSpan SecondWindow = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MinuteWindow = TimeSpan.FromMinutes(1);

        private readonly object gate = new object();
        private readonly int maxPerIpPerMinute;
        private readonly int maxGlobalPerSecond;
        private readonly int maxGlobalPerMinute;
        private readonly Queue<DateTimeOffset> globalSecond = new Queue<DateTimeOffset>();
        private readonly Queue<DateTimeOffset> globalMinute = new Queue<DateTimeOffset>();
        private readonly Dictionary<string, Queue<DateTimeOffset>> perIp = new Dictionary<string, Queue<DateTimeOffset>>(StringComparer.Ordinal);

        public PairBeginRateLimiter(
            int maxPerIpPerMinute = 20,
            int maxGlobalPerSecond = 20,
            int maxGlobalPerMinute = 120)
        {
            if (maxPerIpPerMinute <= 0) throw new ArgumentOutOfRangeException(nameof(maxPerIpPerMinute));
            if (maxGlobalPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(maxGlobalPerSecond));
            if (maxGlobalPerMinute <= 0) throw new ArgumentOutOfRangeException(nameof(maxGlobalPerMinute));

            this.maxPerIpPerMinute = maxPerIpPerMinute;
            this.maxGlobalPerSecond = maxGlobalPerSecond;
            this.maxGlobalPerMinute = maxGlobalPerMinute;
        }

        public int TrackedIpCount
        {
            get
            {
                lock (gate) return perIp.Count;
            }
        }

        public bool TryAcquire(string? remoteIpKey, DateTimeOffset now)
        {
            string ipKey = string.IsNullOrEmpty(remoteIpKey) ? "unknown" : remoteIpKey;

            lock (gate)
            {
                PruneQueueLocked(globalSecond, now - SecondWindow);
                PruneQueueLocked(globalMinute, now - MinuteWindow);

                Queue<DateTimeOffset>? ipQueue = null;
                if (perIp.TryGetValue(ipKey, out var existing))
                {
                    PruneQueueLocked(existing, now - MinuteWindow);
                    if (existing.Count == 0)
                    {
                        perIp.Remove(ipKey);
                    }
                    else
                    {
                        ipQueue = existing;
                    }
                }

                if (perIp.Count > 512)
                {
                    DateTimeOffset minuteCutoff = now - MinuteWindow;
                    var emptyKeys = new List<string>();
                    foreach (var kv in perIp)
                    {
                        PruneQueueLocked(kv.Value, minuteCutoff);
                        if (kv.Value.Count == 0) emptyKeys.Add(kv.Key);
                    }
                    foreach (var k in emptyKeys)
                    {
                        perIp.Remove(k);
                    }
                }

                if (globalSecond.Count >= maxGlobalPerSecond ||
                    globalMinute.Count >= maxGlobalPerMinute ||
                    (ipQueue != null && ipQueue.Count >= maxPerIpPerMinute))
                {
                    return false;
                }

                if (ipQueue == null)
                {
                    ipQueue = new Queue<DateTimeOffset>();
                    perIp[ipKey] = ipQueue;
                }

                globalSecond.Enqueue(now);
                globalMinute.Enqueue(now);
                ipQueue.Enqueue(now);
                return true;
            }
        }

        private static void PruneQueueLocked(Queue<DateTimeOffset> queue, DateTimeOffset cutoff)
        {
            while (queue.Count > 0 && queue.Peek() <= cutoff)
            {
                queue.Dequeue();
            }
        }
    }
}
