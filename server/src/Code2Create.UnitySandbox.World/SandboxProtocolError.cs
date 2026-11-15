using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.World
{
    /// <summary>
    /// JSON-RPC 2.0 error payload carrying a stable data.reason per wire-format-design_ja.md §7.3.
    /// </summary>
    public sealed class SandboxProtocolError
    {
        public int Code { get; }
        public string Message { get; }
        public string Reason { get; }
        public IReadOnlyDictionary<string, object?> Data { get; }

        public SandboxProtocolError(int code, string message, string reason, IReadOnlyDictionary<string, object?>? extraData = null)
        {
            Code = code;
            Message = message;
            Reason = reason;
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["reason"] = reason
            };
            if (extraData != null)
            {
                foreach (var kv in extraData)
                {
                    dict[kv.Key] = kv.Value;
                }
            }
            Data = dict;
        }

        public JObject ToJson()
        {
            var dataObj = new JObject();
            foreach (var kv in Data)
            {
                dataObj[kv.Key] = kv.Value is null ? JValue.CreateNull() : JToken.FromObject(kv.Value);
            }

            return new JObject
            {
                ["code"] = Code,
                ["message"] = Message,
                ["data"] = dataObj
            };
        }

        public static SandboxProtocolError InvalidParams(string? path = null, string message = "invalid params")
        {
            Dictionary<string, object?>? extra = null;
            if (!string.IsNullOrEmpty(path))
            {
                extra = new Dictionary<string, object?> { ["path"] = path };
            }
            return new SandboxProtocolError(-32602, message, "invalid_params", extra);
        }

        public static SandboxProtocolError UnknownBlock(string blockId) =>
            new SandboxProtocolError(-32602, "unknown block", "unknown_block",
                new Dictionary<string, object?> { ["block_id"] = blockId });

        public static SandboxProtocolError UnknownProperty(string blockId, string property) =>
            new SandboxProtocolError(-32602, "unknown property", "unknown_property",
                new Dictionary<string, object?>
                {
                    ["block_id"] = blockId,
                    ["property"] = property
                });

        public static SandboxProtocolError InvalidPropertyValue(string blockId, string property, object? value, IReadOnlyList<object> allowed) =>
            new SandboxProtocolError(-32602, "invalid property value", "invalid_property_value",
                new Dictionary<string, object?>
                {
                    ["block_id"] = blockId,
                    ["property"] = property,
                    ["value"] = value,
                    ["allowed"] = allowed
                });

        public static SandboxProtocolError OutOfBounds(Int3 violating) =>
            new SandboxProtocolError(-32000, "out of bounds", "out_of_bounds",
                new Dictionary<string, object?>
                {
                    ["bounds"] = new Dictionary<string, object?>
                    {
                        ["y_min"] = WorldConstants.YMin,
                        ["y_max"] = WorldConstants.YMax,
                        ["world_border"] = new Dictionary<string, object?>
                        {
                            ["center"] = new Dictionary<string, object?>
                            {
                                ["x"] = WorldConstants.WorldBorderCenterX,
                                ["z"] = WorldConstants.WorldBorderCenterZ
                            },
                            ["size"] = WorldConstants.WorldBorderSize
                        }
                    },
                    ["violating"] = new Dictionary<string, object?>
                    {
                        ["x"] = violating.X,
                        ["y"] = violating.Y,
                        ["z"] = violating.Z
                    }
                });

        public static SandboxProtocolError BuildRangeExceeded(Int3 center, int buildRange, Int3 violating) =>
            new SandboxProtocolError(-32000, "build range exceeded", "build_range_exceeded",
                new Dictionary<string, object?>
                {
                    ["center"] = new Dictionary<string, object?>
                    {
                        ["x"] = center.X,
                        ["y"] = center.Y,
                        ["z"] = center.Z
                    },
                    ["build_range"] = buildRange,
                    ["violating"] = new Dictionary<string, object?>
                    {
                        ["x"] = violating.X,
                        ["y"] = violating.Y,
                        ["z"] = violating.Z
                    }
                });

        public static SandboxProtocolError WorkLimitExceeded(long requestedVolume, int maxVolume) =>
            new SandboxProtocolError(-32000, "work limit exceeded", "work_limit_exceeded",
                new Dictionary<string, object?>
                {
                    ["requested"] = requestedVolume,
                    ["limit"] = maxVolume
                });

        public static SandboxProtocolError MethodNotFound(string method) =>
            new SandboxProtocolError(-32601, $"method not found: {method}", "method_not_found");

        public static SandboxProtocolError ExpectedHello() =>
            new SandboxProtocolError(-32000, "expected hello", "expected_hello");

        public static SandboxProtocolError ProtocolRequired() =>
            new SandboxProtocolError(-32602, "protocol required", "protocol_required");

        public static SandboxProtocolError ProtocolMismatch(string serverProtocol, string clientRequires) =>
            new SandboxProtocolError(-32000, "protocol mismatch", "protocol_mismatch",
                new Dictionary<string, object?>
                {
                    ["server"] = serverProtocol,
                    ["client_requires"] = clientRequires
                });

        public static SandboxProtocolError AuthRequired() =>
            new SandboxProtocolError(-32000, "auth required", "auth_required");

        public static SandboxProtocolError TokenNotFound() =>
            new SandboxProtocolError(-32000, "token not found", "token_not_found");

        public static SandboxProtocolError TokenExpired() =>
            new SandboxProtocolError(-32000, "token expired", "token_expired");

        public static SandboxProtocolError TokenInvalid() =>
            new SandboxProtocolError(-32000, "token invalid", "token_invalid");

        public static SandboxProtocolError PairNotFound() =>
            new SandboxProtocolError(-32000, "pair not found", "pair_not_found");

        public static SandboxProtocolError PairExpired() =>
            new SandboxProtocolError(-32000, "pair expired", "pair_expired");

        public static SandboxProtocolError UnknownDimension(string dimension) =>
            new SandboxProtocolError(-32000, "unknown dimension", "unknown_dimension",
                new Dictionary<string, object?>
                {
                    ["dimension"] = dimension
                });
    }

    public sealed class SandboxOperationException : Exception
    {
        public SandboxProtocolError Error { get; }

        public SandboxOperationException(SandboxProtocolError error)
            : base($"{error.Reason}: {error.Message}")
        {
            Error = error;
        }
    }
}
