using System;
using System.Threading;
using System.Threading.Tasks;
using Code2Create.UnitySandbox.World.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.World.JsonRpc
{
    /// <summary>
    /// Dispatches authenticated world, catalog, events, build, player, chat, and flush JSON-RPC operations against SandboxWorld.
    /// </summary>
    public sealed class SandboxJsonRpcDispatcher
    {
        private readonly SandboxWorld world;
        private readonly Action<StreamBuildContext>? onBuildContextChanged;
        private readonly ISandboxPlayerBridge? playerBridge;

        public SandboxEventRing EventRing { get; }

        public SandboxJsonRpcDispatcher(
            SandboxWorld world,
            Action<StreamBuildContext>? onBuildContextChanged = null,
            ISandboxPlayerBridge? playerBridge = null,
            SandboxEventRing? eventRing = null)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.onBuildContextChanged = onBuildContextChanged;
            this.playerBridge = playerBridge;
            EventRing = eventRing ?? new SandboxEventRing();
        }

        public string? DispatchLine(StreamBuildContext context, string line)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (string.IsNullOrWhiteSpace(line))
            {
                return BuildErrorResponse(JValue.CreateNull(), new SandboxProtocolError(-32700, "parse error", "invalid_params"));
            }

            JObject request;
            try
            {
                var token = JToken.Parse(line);
                if (token is not JObject obj)
                {
                    return BuildErrorResponse(JValue.CreateNull(), SandboxProtocolError.InvalidParams(message: "request must be a JSON object"));
                }
                request = obj;
            }
            catch (JsonException)
            {
                return BuildErrorResponse(JValue.CreateNull(), new SandboxProtocolError(-32700, "parse error", "invalid_params"));
            }

            bool hasId = request.TryGetValue("id", StringComparison.Ordinal, out var idToken);
            if (!request.TryGetValue("jsonrpc", StringComparison.Ordinal, out var versionToken) ||
                versionToken.Type != JTokenType.String ||
                !string.Equals(versionToken.Value<string>(), "2.0", StringComparison.Ordinal))
            {
                return hasId ? BuildErrorResponse(idToken!, SandboxProtocolError.InvalidParams(message: "jsonrpc must be '2.0'")) : null;
            }

            if (!request.TryGetValue("method", StringComparison.Ordinal, out var methodToken) ||
                methodToken.Type != JTokenType.String)
            {
                return hasId ? BuildErrorResponse(idToken!, SandboxProtocolError.InvalidParams(message: "method must be a string")) : null;
            }

            string method = methodToken.Value<string>()!;
            request.TryGetValue("params", StringComparison.Ordinal, out var paramsToken);

            try
            {
                JToken result = ExecuteMethod(context, method, paramsToken);
                return hasId ? BuildSuccessResponse(idToken!, result) : null;
            }
            catch (SandboxOperationException ex)
            {
                return hasId ? BuildErrorResponse(idToken!, ex.Error) : null;
            }
        }

        public async Task RunLoopAsync(IJsonRpcLineTransport transport, StreamBuildContext context, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await transport.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null) break;
                string? response = DispatchLine(context, line);
                if (response != null)
                {
                    await transport.WriteLineAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private JToken ExecuteMethod(StreamBuildContext context, string method, JToken? paramsToken)
        {
            switch (method)
            {
                case "chat.post":
                {
                    var arr = RequireParamsArray(paramsToken, 1);
                    if (arr[0].Type != JTokenType.String)
                    {
                        throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params[0]"));
                    }
                    world.PostChat(context, arr[0].Value<string>()!);
                    return JValue.CreateNull();
                }

                case "world.setBlock":
                {
                    var arr = RequireParamsArray(paramsToken, 4);
                    int x = RequireStrictInt(arr[0], "params[0]");
                    int y = RequireStrictInt(arr[1], "params[1]");
                    int z = RequireStrictInt(arr[2], "params[2]");
                    world.SetBlock(context, x, y, z, arr[3]);
                    return JValue.CreateNull();
                }

                case "world.getBlock":
                {
                    var arr = RequireParamsArray(paramsToken, 3);
                    int x = RequireStrictInt(arr[0], "params[0]");
                    int y = RequireStrictInt(arr[1], "params[1]");
                    int z = RequireStrictInt(arr[2], "params[2]");
                    return world.GetBlock(context, x, y, z).ToJson();
                }

                case "world.setBlocks":
                {
                    var arr = RequireParamsArray(paramsToken, 7);
                    int x1 = RequireStrictInt(arr[0], "params[0]");
                    int y1 = RequireStrictInt(arr[1], "params[1]");
                    int z1 = RequireStrictInt(arr[2], "params[2]");
                    int x2 = RequireStrictInt(arr[3], "params[3]");
                    int y2 = RequireStrictInt(arr[4], "params[4]");
                    int z2 = RequireStrictInt(arr[5], "params[5]");
                    world.SetBlocks(context, x1, y1, z1, x2, y2, z2, arr[6]);
                    return JValue.CreateNull();
                }

                case "world.getBlocks":
                {
                    var arr = RequireParamsArray(paramsToken, 6);
                    int x1 = RequireStrictInt(arr[0], "params[0]");
                    int y1 = RequireStrictInt(arr[1], "params[1]");
                    int z1 = RequireStrictInt(arr[2], "params[2]");
                    int x2 = RequireStrictInt(arr[3], "params[3]");
                    int y2 = RequireStrictInt(arr[4], "params[4]");
                    int z2 = RequireStrictInt(arr[5], "params[5]");
                    var blocks = world.GetBlocks(context, x1, y1, z1, x2, y2, z2);
                    var result = new JArray();
                    for (int i = 0; i < blocks.Count; i++)
                    {
                        result.Add(blocks[i].ToJson());
                    }
                    return result;
                }

                case "world.getHeight":
                {
                    if (paramsToken is not JArray arr || (arr.Count != 2 && arr.Count != 3))
                    {
                        throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params"));
                    }
                    int x = RequireStrictInt(arr[0], "params[0]");
                    int z = RequireStrictInt(arr[1], "params[1]");
                    int? maxY = arr.Count == 3 ? RequireStrictInt(arr[2], "params[2]") : null;
                    return new JValue(world.GetHeight(context, x, z, maxY));
                }

                case "player.getPos":
                {
                    RequireParamsArray(paramsToken, 0);
                    return RequireOnlinePlayerState().ToPositionResultJson(context.Origin);
                }

                case "player.setPos":
                {
                    var arr = RequireParamsArray(paramsToken, 4);
                    _ = RequireOnlinePlayerState();
                    string canonicalDim = RequireDimension(arr[0], "params[0]");
                    (double absX, double absY, double absZ) = ResolveAbsolutePlayerCoords(context, arr, 1);
                    var status = playerBridge!.TrySetPlayerPosition(canonicalDim, absX, absY, absZ, out var updated);
                    ThrowIfPlayerUnavailable(status);
                    return updated.ToPositionResultJson(context.Origin);
                }

                case "player.getPose":
                {
                    RequireParamsArray(paramsToken, 0);
                    return RequireOnlinePlayerState().ToPoseResultJson(context.Origin);
                }

                case "player.setPose":
                {
                    var arr = RequireParamsArray(paramsToken, 6);
                    _ = RequireOnlinePlayerState();
                    string canonicalDim = RequireDimension(arr[0], "params[0]");
                    double relX = RequireFiniteDouble(arr[1], "params[1]");
                    double relY = RequireFiniteDouble(arr[2], "params[2]");
                    double relZ = RequireFiniteDouble(arr[3], "params[3]");
                    double yaw = RequireFiniteDouble(arr[4], "params[4]");
                    double pitch = RequireFiniteDouble(arr[5], "params[5]");
                    SandboxWireNumbers.RequirePitch(pitch, "params[5]");

                    (double absX, double absY, double absZ) = ToAbsoluteCheckedPlayerBounds(context, relX, relY, relZ);
                    double normalizedYaw = SandboxWireNumbers.NormalizeYaw(yaw, "params[4]");
                    var status = playerBridge!.TrySetPlayerPose(canonicalDim, absX, absY, absZ, normalizedYaw, pitch, out var updated);
                    ThrowIfPlayerUnavailable(status);
                    return updated.ToPoseResultJson(context.Origin);
                }

                case "player.getDirection":
                {
                    RequireParamsArray(paramsToken, 0);
                    SandboxPlayerState state = RequireOnlinePlayerState();
                    return SandboxDirectionUnit.FromYawPitch(state.Yaw, state.Pitch).ToWireJson();
                }

                case "player.setDirection":
                {
                    var arr = RequireParamsArray(paramsToken, 3);
                    double dx = RequireFiniteDouble(arr[0], "params[0]");
                    double dy = RequireFiniteDouble(arr[1], "params[1]");
                    double dz = RequireFiniteDouble(arr[2], "params[2]");
                    var unit = SandboxDirectionUnit.Normalize(dx, dy, dz);

                    if (playerBridge == null)
                    {
                        throw new SandboxOperationException(SandboxProtocolError.AuthRequired());
                    }
                    var status = playerBridge.TrySetPlayerRotation(unit.ToYawDegrees(), unit.ToPitchDegrees(), out var updated);
                    ThrowIfPlayerUnavailable(status);
                    return SandboxDirectionUnit.FromYawPitch(updated.Yaw, updated.Pitch).ToWireJson();
                }

                case "catalog.get":
                {
                    RequireParamsArray(paramsToken, 0);
                    return world.Registry.BuildCatalogResult();
                }

                case "events.poll":
                {
                    if (paramsToken is not JArray arr || arr.Count < 1 || arr.Count > 2)
                    {
                        throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params"));
                    }
                    long afterSequence = RequireStrictLong(arr[0], "params[0]");
                    int effectiveLimit = WorldConstants.DefaultEventPollLimit;
                    if (arr.Count == 2)
                    {
                        if (arr[1] is not JObject opts ||
                            opts.Count != 1 ||
                            !opts.TryGetValue("max_events", StringComparison.Ordinal, out var maxToken))
                        {
                            throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params[1]"));
                        }
                        long requestedMax = RequireStrictLong(maxToken, "params[1].max_events");
                        if (requestedMax < 1)
                        {
                            throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params[1].max_events"));
                        }
                        effectiveLimit = requestedMax >= WorldConstants.MaxEventPollLimit
                            ? WorldConstants.MaxEventPollLimit
                            : (int)requestedMax;
                    }

                    return EventRing.Poll(afterSequence, effectiveLimit);
                }

                case "build.setDimension":
                {
                    var arr = RequireParamsArray(paramsToken, 1);
                    if (arr[0].Type != JTokenType.String)
                    {
                        throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params[0]"));
                    }
                    string canonical = SandboxDimensionResolver.ResolveDimension(arr[0].Value<string>());
                    context.Dimension = canonical;
                    onBuildContextChanged?.Invoke(context);
                    return BuildContextJson(context);
                }

                case "build.setOrigin":
                {
                    var arr = RequireParamsArray(paramsToken, 3);
                    int x = RequireStrictInt(arr[0], "params[0]");
                    int y = RequireStrictInt(arr[1], "params[1]");
                    int z = RequireStrictInt(arr[2], "params[2]");
                    context.Origin = new Int3(x, y, z);
                    onBuildContextChanged?.Invoke(context);
                    return BuildContextJson(context);
                }

                case "connection.flush":
                {
                    RequireParamsArray(paramsToken, 0);
                    return JValue.CreateNull();
                }

                default:
                    throw new SandboxOperationException(SandboxProtocolError.MethodNotFound(method));
            }
        }

        public static JObject BuildContextJson(StreamBuildContext context)
        {
            return new JObject
            {
                ["dimension"] = context.Dimension,
                ["origin"] = new JArray(context.Origin.ToArray())
            };
        }

        public static JArray RequireParamsArray(JToken? paramsToken, int expectedLength)
        {
            if (paramsToken is not JArray arr || arr.Count != expectedLength)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params"));
            }
            return arr;
        }

        public static int RequireStrictInt(JToken token, string path)
        {
            long value = RequireStrictLong(token, path);
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(path));
            }
            return (int)value;
        }

        public static long RequireStrictLong(JToken token, string path)
        {
            if (token.Type != JTokenType.Integer)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(path));
            }

            try
            {
                return token.Value<long>();
            }
            catch (OverflowException)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(path));
            }
        }

        public static double RequireFiniteDouble(JToken token, string path)
        {
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(path));
            }

            double value;
            try
            {
                value = token.Value<double>();
            }
            catch (OverflowException)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(path));
            }

            SandboxWireNumbers.RequireFinite(value, path);
            return value;
        }

        private SandboxPlayerState RequireOnlinePlayerState()
        {
            if (playerBridge == null)
            {
                throw new SandboxOperationException(SandboxProtocolError.AuthRequired());
            }

            var status = playerBridge.TryGetPlayerState(out var state);
            ThrowIfPlayerUnavailable(status);
            return state;
        }

        private static void ThrowIfPlayerUnavailable(PlayerAccessStatus status)
        {
            switch (status)
            {
                case PlayerAccessStatus.Ok:
                    return;
                case PlayerAccessStatus.AuthRequired:
                    throw new SandboxOperationException(SandboxProtocolError.AuthRequired());
                case PlayerAccessStatus.PlayerOffline:
                    throw new SandboxOperationException(SandboxProtocolError.PlayerOffline());
                default:
                    throw new SandboxOperationException(SandboxProtocolError.PlayerOffline());
            }
        }

        private static string RequireDimension(JToken token, string path)
        {
            if (token.Type != JTokenType.String)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(path));
            }
            return SandboxDimensionResolver.ResolveDimension(token.Value<string>());
        }

        private static (double AbsX, double AbsY, double AbsZ) ResolveAbsolutePlayerCoords(
            StreamBuildContext context,
            JArray args,
            int offset)
        {
            double relX = RequireFiniteDouble(args[offset], $"params[{offset}]");
            double relY = RequireFiniteDouble(args[offset + 1], $"params[{offset + 1}]");
            double relZ = RequireFiniteDouble(args[offset + 2], $"params[{offset + 2}]");
            return ToAbsoluteCheckedPlayerBounds(context, relX, relY, relZ);
        }

        private static (double AbsX, double AbsY, double AbsZ) ToAbsoluteCheckedPlayerBounds(
            StreamBuildContext context,
            double relX,
            double relY,
            double relZ)
        {
            double absX = context.Origin.X + relX;
            double absY = context.Origin.Y + relY;
            double absZ = context.Origin.Z + relZ;
            SandboxWireNumbers.RequireFinite(absX, "params");
            SandboxWireNumbers.RequireFinite(absY, "params");
            SandboxWireNumbers.RequireFinite(absZ, "params");

            if (!WorldConstants.IsInPlayerBounds(absX, absY, absZ))
            {
                throw new SandboxOperationException(SandboxProtocolError.OutOfBounds(absX, absY, absZ));
            }

            return (absX, absY, absZ);
        }

        public static string BuildSuccessResponse(JToken idToken, JToken result)
        {
            var resp = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idToken.DeepClone(),
                ["result"] = result
            };
            return resp.ToString(Formatting.None);
        }

        public static string BuildErrorResponse(JToken idToken, SandboxProtocolError error)
        {
            var resp = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idToken.DeepClone(),
                ["error"] = error.ToJson()
            };
            return resp.ToString(Formatting.None);
        }
    }
}
