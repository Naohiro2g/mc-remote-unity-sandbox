using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Code2Create.UnitySandbox.Server.Sync;
using Code2Create.UnitySandbox.World;
using Code2Create.UnitySandbox.World.JsonRpc;
using Code2Create.UnitySandbox.World.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.Server.Rpc
{
    public readonly struct SandboxRpcStepResult
    {
        public string? ResponseLine { get; }
        public bool ShouldClose { get; }

        public SandboxRpcStepResult(string? responseLine, bool shouldClose)
        {
            ResponseLine = responseLine;
            ShouldClose = shouldClose;
        }
    }

    /// <summary>
    /// Per-connection JSON-RPC 2.0 session handling pre-hello gate (auth.* / hello) and post-hello dispatch.
    /// Pairings correlate via pairing_id so auth.pairBegin, auth.pairPoll, and hello can run on separate TCP connections.
    /// </summary>
    public sealed class SandboxRpcSession : IDisposable
    {
        private static readonly Regex SemverRegex = new Regex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.Compiled);

        private readonly SandboxWorld world;
        private readonly IViewerStreamBindingStore bindingStore;
        private readonly IPairingApprovalStore pairingStore;
        private readonly ISandboxTokenStore tokenStore;
        private readonly DisplaySyncHub displaySyncHub;
        private readonly Func<DateTimeOffset> clock;
        private readonly bool authEnforcement;
        private readonly string serverName;
        private readonly int buildRange;
        private readonly string serverVersion;
        private readonly SandboxEventRing eventRing = new SandboxEventRing();
        private readonly SandboxJsonRpcDispatcher dispatcher;
        private readonly string sessionId = "sess_" + Guid.NewGuid().ToString("N").Substring(0, 12);

        private bool helloComplete;
        private StreamBuildContext? boundContext;
        private string? boundViewerId;
        private int disposed;
        private volatile ContextSnapshot? eventOriginSnapshot;

        public SandboxRpcSession(
            SandboxWorld world,
            IViewerStreamBindingStore bindingStore,
            IPairingApprovalStore pairingStore,
            ISandboxTokenStore tokenStore,
            DisplaySyncHub displaySyncHub,
            Func<DateTimeOffset>? clock = null,
            bool authEnforcement = true,
            string serverName = WorldConstants.DefaultServerName,
            int buildRange = WorldConstants.DefaultBuildRange,
            string serverVersion = WorldConstants.DefaultServerVersion)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.bindingStore = bindingStore ?? throw new ArgumentNullException(nameof(bindingStore));
            this.pairingStore = pairingStore ?? throw new ArgumentNullException(nameof(pairingStore));
            this.tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
            this.displaySyncHub = displaySyncHub ?? throw new ArgumentNullException(nameof(displaySyncHub));
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
            this.authEnforcement = authEnforcement;
            this.serverName = string.IsNullOrEmpty(serverName) ? WorldConstants.DefaultServerName : serverName;
            this.buildRange = buildRange;
            this.serverVersion = string.IsNullOrWhiteSpace(serverVersion) ? WorldConstants.DefaultServerVersion : serverVersion;

            dispatcher = new SandboxJsonRpcDispatcher(
                this.world,
                ctx =>
                {
                    eventOriginSnapshot = new ContextSnapshot(ctx.Dimension, ctx.Origin);
                    this.displaySyncHub.NotifyStreamOriginChanged(ctx.StreamId, ctx.Dimension, ctx.Origin);
                },
                new SessionPlayerBridge(this),
                eventRing);
        }

        public bool IsHelloComplete => helloComplete;

        public async Task RunAsync(IJsonRpcLineTransport transport, CancellationToken cancellationToken)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await transport.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null) break;

                SandboxRpcStepResult step = ProcessLine(line);
                if (step.ResponseLine != null)
                {
                    await transport.WriteLineAsync(step.ResponseLine, cancellationToken).ConfigureAwait(false);
                }

                if (step.ShouldClose)
                {
                    break;
                }
            }
        }

        public SandboxRpcStepResult ProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                string resp = SandboxJsonRpcDispatcher.BuildErrorResponse(
                    JValue.CreateNull(),
                    new SandboxProtocolError(-32700, "parse error", "invalid_params"));
                return new SandboxRpcStepResult(resp, !helloComplete);
            }

            JObject request;
            try
            {
                var token = JToken.Parse(line);
                if (token is not JObject obj)
                {
                    string resp = SandboxJsonRpcDispatcher.BuildErrorResponse(
                        JValue.CreateNull(),
                        SandboxProtocolError.InvalidParams(message: "request must be a JSON object"));
                    return new SandboxRpcStepResult(resp, !helloComplete);
                }
                request = obj;
            }
            catch (JsonException)
            {
                string resp = SandboxJsonRpcDispatcher.BuildErrorResponse(
                    JValue.CreateNull(),
                    new SandboxProtocolError(-32700, "parse error", "invalid_params"));
                return new SandboxRpcStepResult(resp, !helloComplete);
            }

            bool hasId = request.TryGetValue("id", StringComparison.Ordinal, out var idToken);
            if (!request.TryGetValue("jsonrpc", StringComparison.Ordinal, out var versionToken) ||
                versionToken.Type != JTokenType.String ||
                !string.Equals(versionToken.Value<string>(), "2.0", StringComparison.Ordinal))
            {
                string? resp = hasId
                    ? SandboxJsonRpcDispatcher.BuildErrorResponse(
                        idToken!,
                        SandboxProtocolError.InvalidParams(message: "jsonrpc must be '2.0'"))
                    : null;
                return new SandboxRpcStepResult(resp, !helloComplete);
            }

            if (!request.TryGetValue("method", StringComparison.Ordinal, out var methodToken) ||
                methodToken.Type != JTokenType.String)
            {
                string? resp = hasId
                    ? SandboxJsonRpcDispatcher.BuildErrorResponse(
                        idToken!,
                        SandboxProtocolError.InvalidParams(message: "method must be a string"))
                    : null;
                return new SandboxRpcStepResult(resp, !helloComplete);
            }

            string method = methodToken.Value<string>()!;
            request.TryGetValue("params", StringComparison.Ordinal, out var paramsToken);

            if (!helloComplete)
            {
                return HandlePreHello(hasId ? idToken : null, method, paramsToken);
            }

            if (method == "hello" || method == "auth.pairBegin" || method == "auth.pairPoll")
            {
                string? resp = hasId
                    ? SandboxJsonRpcDispatcher.BuildErrorResponse(idToken!, SandboxProtocolError.MethodNotFound(method))
                    : null;
                return new SandboxRpcStepResult(resp, false);
            }

            string? postResp = dispatcher.DispatchLine(boundContext!, line);
            return new SandboxRpcStepResult(postResp, false);
        }

        private SandboxRpcStepResult HandlePreHello(JToken? idToken, string method, JToken? paramsToken)
        {
            switch (method)
            {
                case "auth.pairBegin":
                    return ExecuteWithResponse(idToken, false, () => HandlePairBegin(paramsToken));

                case "auth.pairPoll":
                    return ExecuteWithResponse(idToken, false, () => HandlePairPoll(paramsToken));

                case "hello":
                    return ExecuteWithResponse(idToken, true, () =>
                    {
                        var result = HandleHello(paramsToken);
                        helloComplete = true;
                        return result;
                    });

                default:
                    string? err = idToken != null
                        ? SandboxJsonRpcDispatcher.BuildErrorResponse(idToken, SandboxProtocolError.ExpectedHello())
                        : null;
                    return new SandboxRpcStepResult(err, true);
            }
        }

        private static SandboxRpcStepResult ExecuteWithResponse(
            JToken? idToken,
            bool closeOnError,
            Func<JToken> handler)
        {
            try
            {
                JToken result = handler();
                string? resp = idToken != null
                    ? SandboxJsonRpcDispatcher.BuildSuccessResponse(idToken, result)
                    : null;
                return new SandboxRpcStepResult(resp, false);
            }
            catch (SandboxOperationException ex)
            {
                string? resp = idToken != null
                    ? SandboxJsonRpcDispatcher.BuildErrorResponse(idToken, ex.Error)
                    : null;
                return new SandboxRpcStepResult(resp, closeOnError);
            }
        }

        private JToken HandlePairBegin(JToken? paramsToken)
        {
            if (paramsToken is not JObject paramsObj)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params"));
            }

            string tokenType = "session";
            if (paramsObj.TryGetValue("token_type", StringComparison.Ordinal, out var typeToken))
            {
                if (typeToken.Type != JTokenType.String)
                {
                    throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.token_type"));
                }

                string rawType = typeToken.Value<string>()!;
                if (rawType != "session" && rawType != "long_lived")
                {
                    throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.token_type"));
                }
                tokenType = rawType;
            }

            string? device = null;
            if (paramsObj.TryGetValue("device", StringComparison.Ordinal, out var deviceToken) &&
                deviceToken.Type != JTokenType.Null)
            {
                if (deviceToken.Type != JTokenType.String)
                {
                    throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.device"));
                }
                string trimmed = deviceToken.Value<string>()!.Trim();
                int codePoints = CountCodePoints(trimmed);
                if (codePoints < 1 || codePoints > 64)
                {
                    throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.device"));
                }
                device = trimmed;
            }

            if (paramsObj.TryGetValue("client", StringComparison.Ordinal, out var clientToken) &&
                clientToken.Type != JTokenType.Null &&
                clientToken is not JObject)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.client"));
            }

            var begun = pairingStore.BeginPairing(tokenType, device, clock());
            return new JObject
            {
                ["pairing_id"] = begun.PairingId,
                ["pair_code"] = begun.PairCode,
                ["expires_in"] = begun.ExpiresIn
            };
        }

        private JToken HandlePairPoll(JToken? paramsToken)
        {
            if (paramsToken is not JObject paramsObj ||
                !paramsObj.TryGetValue("pairing_id", StringComparison.Ordinal, out var idToken) ||
                idToken.Type != JTokenType.String)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.pairing_id"));
            }

            string pairingId = idToken.Value<string>()!;
            if (string.IsNullOrWhiteSpace(pairingId))
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.pairing_id"));
            }

            var poll = pairingStore.PollPairing(pairingId, clock());
            return poll.Status switch
            {
                PairingPollStatus.Pending => new JObject { ["status"] = "pending" },
                PairingPollStatus.Approved => new JObject
                {
                    ["status"] = "ok",
                    ["token"] = poll.Token!
                },
                PairingPollStatus.Expired => throw new SandboxOperationException(SandboxProtocolError.PairExpired()),
                _ => throw new SandboxOperationException(SandboxProtocolError.PairNotFound())
            };
        }

        private JToken HandleHello(JToken? paramsToken)
        {
            if (paramsToken is not JObject paramsObj)
            {
                throw new SandboxOperationException(SandboxProtocolError.ProtocolRequired());
            }

            if (!paramsObj.TryGetValue("protocol", StringComparison.Ordinal, out var protoToken) ||
                protoToken.Type != JTokenType.String)
            {
                throw new SandboxOperationException(SandboxProtocolError.ProtocolRequired());
            }

            string clientProtocol = protoToken.Value<string>()!.Trim();
            if (string.IsNullOrEmpty(clientProtocol))
            {
                throw new SandboxOperationException(SandboxProtocolError.ProtocolRequired());
            }

            if (!IsProtocolCompatible(clientProtocol))
            {
                throw new SandboxOperationException(
                    SandboxProtocolError.ProtocolMismatch(WorldConstants.ProtocolVersion, clientProtocol));
            }

            string? token = ExtractHelloToken(paramsObj);
            SandboxTokenRecord? resolvedRecord = null;
            DateTimeOffset now = clock();

            if (string.IsNullOrEmpty(token))
            {
                if (authEnforcement)
                {
                    throw new SandboxOperationException(SandboxProtocolError.AuthRequired());
                }
            }
            else
            {
                var resolution = tokenStore.ResolveToken(token, now);
                if (resolution.Status != TokenResolutionStatus.Active)
                {
                    if (authEnforcement || resolution.Status != TokenResolutionStatus.NotFound)
                    {
                        SandboxProtocolError err = resolution.Status switch
                        {
                            TokenResolutionStatus.Expired => SandboxProtocolError.TokenExpired(),
                            TokenResolutionStatus.NotFound => SandboxProtocolError.TokenNotFound(),
                            _ => SandboxProtocolError.TokenInvalid()
                        };
                        throw new SandboxOperationException(err);
                    }
                }
                else
                {
                    resolvedRecord = resolution.Record;
                }
            }

            (string dimension, Int3 origin) = ResolveHelloBuild(paramsObj);

            string streamId;
            string? playerUuid;
            if (resolvedRecord != null)
            {
                streamId = resolvedRecord.StreamId;
                playerUuid = resolvedRecord.PlayerUuid;
            }
            else
            {
                streamId = "guest-" + sessionId;
                playerUuid = Guid.NewGuid().ToString("D");
            }

            _ = bindingStore.GetOrCreateStreamContext(streamId);
            var ctx = new StreamBuildContext(streamId, dimension, origin);
            boundContext = ctx;
            eventOriginSnapshot = new ContextSnapshot(dimension, origin);
            if (resolvedRecord != null)
            {
                boundViewerId = resolvedRecord.ViewerId;
                displaySyncHub.RegisterViewerChatListener(
                    resolvedRecord.ViewerId,
                    sessionId,
                    message =>
                    {
                        var snap = eventOriginSnapshot;
                        if (snap != null)
                        {
                            eventRing.OfferChatPosted(snap.Dimension, snap.Origin, message);
                        }
                    });
            }

            displaySyncHub.NotifyStreamOriginChanged(streamId, dimension, origin);

            string catalogHash = world.Registry.BuildCatalogResult()["catalogHash"]!.Value<string>()!;

            return new JObject
            {
                ["protocol"] = WorldConstants.ProtocolVersion,
                ["mc_version"] = WorldConstants.McVersion,
                ["supported_mc_versions"] = new JArray(WorldConstants.McVersion),
                ["catalogHash"] = catalogHash,
                ["world_constants"] = new JObject
                {
                    ["y_sea"] = WorldConstants.YSea
                },
                ["session"] = sessionId,
                ["player"] = playerUuid,
                ["dimension"] = dimension,
                ["origin"] = new JArray(origin.ToArray()),
                ["permissions"] = new JObject
                {
                    ["online"] = true,
                    ["offline"] = true,
                    ["buildRange"] = buildRange
                },
                ["server"] = new JObject
                {
                    ["name"] = serverName,
                    ["version"] = serverVersion
                }
            };
        }

        private static string? ExtractHelloToken(JObject paramsObj)
        {
            if (!paramsObj.TryGetValue("auth", StringComparison.Ordinal, out var authToken) ||
                authToken.Type == JTokenType.Null)
            {
                return null;
            }

            if (authToken is not JObject authObj)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.auth"));
            }

            if (!authObj.TryGetValue("token", StringComparison.Ordinal, out var tokenVal) ||
                tokenVal.Type == JTokenType.Null)
            {
                return null;
            }

            if (tokenVal.Type != JTokenType.String)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.auth.token"));
            }

            return tokenVal.Value<string>()!.Trim();
        }

        private static (string Dimension, Int3 Origin) ResolveHelloBuild(JObject paramsObj)
        {
            string dimension = WorldConstants.DefaultDimension;
            Int3 origin = Int3.Zero;

            if (!paramsObj.TryGetValue("build", StringComparison.Ordinal, out var buildToken) ||
                buildToken.Type == JTokenType.Null)
            {
                return (dimension, origin);
            }

            if (buildToken is not JObject buildObj)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.build"));
            }

            foreach (var prop in buildObj.Properties())
            {
                if (prop.Name != "dimension" && prop.Name != "origin")
                {
                    throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.build." + prop.Name));
                }
            }

            if (buildObj.TryGetValue("dimension", StringComparison.Ordinal, out var dimToken))
            {
                if (dimToken.Type != JTokenType.String)
                {
                    throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.build.dimension"));
                }
                dimension = SandboxDimensionResolver.ResolveDimension(dimToken.Value<string>());
            }

            if (buildObj.TryGetValue("origin", StringComparison.Ordinal, out var originToken))
            {
                if (originToken is not JArray originArr || originArr.Count != 3)
                {
                    throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params.build.origin"));
                }
                int x = SandboxJsonRpcDispatcher.RequireStrictInt(originArr[0], "params.build.origin[0]");
                int y = SandboxJsonRpcDispatcher.RequireStrictInt(originArr[1], "params.build.origin[1]");
                int z = SandboxJsonRpcDispatcher.RequireStrictInt(originArr[2], "params.build.origin[2]");
                origin = new Int3(x, y, z);
            }

            return (dimension, origin);
        }

        private static bool IsProtocolCompatible(string clientProtocol)
        {
            var match = SemverRegex.Match(clientProtocol);
            if (!match.Success) return false;

            if (!int.TryParse(match.Groups[1].Value, out int major) ||
                !int.TryParse(match.Groups[2].Value, out int minor))
            {
                return false;
            }

            return major == WorldConstants.ProtocolMajor && minor <= WorldConstants.ProtocolMinor;
        }

        private static int CountCodePoints(string s)
        {
            int count = 0;
            foreach (var _ in s.EnumerateRunes())
            {
                count++;
            }
            return count;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0 && !string.IsNullOrEmpty(boundViewerId))
            {
                displaySyncHub.UnregisterViewerChatListener(boundViewerId, sessionId);
            }
        }

        private sealed class ContextSnapshot
        {
            public string Dimension { get; }
            public Int3 Origin { get; }

            public ContextSnapshot(string dimension, Int3 origin)
            {
                Dimension = dimension;
                Origin = origin;
            }
        }

        private sealed class SessionPlayerBridge : ISandboxPlayerBridge
        {
            private readonly SandboxRpcSession owner;

            public SessionPlayerBridge(SandboxRpcSession owner)
            {
                this.owner = owner;
            }

            public PlayerAccessStatus TryGetPlayerState(out SandboxPlayerState state) =>
                owner.displaySyncHub.TryGetViewerPlayerState(owner.boundViewerId, out state);

            public PlayerAccessStatus TrySetPlayerPosition(string dimension, double absX, double absY, double absZ, out SandboxPlayerState updated) =>
                owner.displaySyncHub.TrySetViewerPosition(owner.boundViewerId, dimension, absX, absY, absZ, out updated);

            public PlayerAccessStatus TrySetPlayerPose(string dimension, double absX, double absY, double absZ, double normalizedYaw, double pitch, out SandboxPlayerState updated) =>
                owner.displaySyncHub.TrySetViewerPose(owner.boundViewerId, dimension, absX, absY, absZ, normalizedYaw, pitch, out updated);

            public PlayerAccessStatus TrySetPlayerRotation(double normalizedYaw, double pitch, out SandboxPlayerState updated) =>
                owner.displaySyncHub.TrySetViewerRotation(owner.boundViewerId, normalizedYaw, pitch, out updated);
        }
    }
}
