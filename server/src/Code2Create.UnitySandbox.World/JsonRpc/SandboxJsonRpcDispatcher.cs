using System;
using System.Threading;
using System.Threading.Tasks;
using Code2Create.UnitySandbox.World.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.World.JsonRpc
{
    /// <summary>
    /// Dispatches authenticated world, catalog, events, build, chat, and flush JSON-RPC operations against SandboxWorld.
    /// </summary>
    public sealed class SandboxJsonRpcDispatcher
    {
        private readonly SandboxWorld world;
        private readonly Action<StreamBuildContext>? onBuildContextChanged;

        public SandboxJsonRpcDispatcher(SandboxWorld world, Action<StreamBuildContext>? onBuildContextChanged = null)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.onBuildContextChanged = onBuildContextChanged;
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
                    int afterSequence = RequireStrictInt(arr[0], "params[0]");
                    if (afterSequence != 0)
                    {
                        throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params[0]"));
                    }
                    if (arr.Count == 2)
                    {
                        if (arr[1] is not JObject opts ||
                            opts.Count != 1 ||
                            !opts.TryGetValue("max_events", StringComparison.Ordinal, out var maxToken))
                        {
                            throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params[1]"));
                        }
                        int maxEvents = RequireStrictInt(maxToken, "params[1].max_events");
                        if (maxEvents < 1)
                        {
                            throw new SandboxOperationException(SandboxProtocolError.InvalidParams("params[1].max_events"));
                        }
                    }

                    return new JObject
                    {
                        ["events"] = new JArray(),
                        ["through_sequence"] = 0,
                        ["latest_sequence"] = 0,
                        ["filtered_out"] = 0,
                        ["overflow_dropped_total"] = 0,
                        ["capacity_dropped_total"] = 0,
                        ["explicitly_discarded_total"] = 0
                    };
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
            if (token.Type != JTokenType.Integer)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(path));
            }

            long value = token.Value<long>();
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(path));
            }
            return (int)value;
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
