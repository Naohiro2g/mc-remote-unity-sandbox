using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Code2Create.UnitySandbox.World
{
    public sealed class BlockPropertyDefinition
    {
        public string Name { get; }
        public IReadOnlyList<BlockStateScalar> AllowedValues { get; }
        public BlockStateScalar DefaultValue { get; }

        public BlockPropertyDefinition(string name, IReadOnlyList<BlockStateScalar> allowedValues, BlockStateScalar defaultValue)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Property name is required.", nameof(name));
            if (allowedValues == null || allowedValues.Count == 0) throw new ArgumentException("Allowed values cannot be empty.", nameof(allowedValues));
            if (!allowedValues.Any(v => v.Equals(defaultValue)))
            {
                throw new ArgumentException($"Default value '{defaultValue}' must be in allowed values for property '{name}'.");
            }

            Name = name;
            AllowedValues = allowedValues;
            DefaultValue = defaultValue;
        }
    }

    public sealed class BlockDefinition
    {
        public string BlockId { get; }
        public IReadOnlyDictionary<string, BlockPropertyDefinition> Properties { get; }
        public BlockValue DefaultBlockValue { get; }

        public BlockDefinition(string blockId, params BlockPropertyDefinition[] properties)
        {
            if (string.IsNullOrEmpty(blockId) || !SandboxBlockRegistry.IsCanonicalResourceId(blockId))
            {
                throw new ArgumentException($"Canonical resource ID required: '{blockId}'.", nameof(blockId));
            }

            BlockId = blockId;
            var map = new SortedDictionary<string, BlockPropertyDefinition>(StringComparer.Ordinal);
            var defaults = new SortedDictionary<string, BlockStateScalar>(StringComparer.Ordinal);
            foreach (var prop in properties)
            {
                map[prop.Name] = prop;
                defaults[prop.Name] = prop.DefaultValue;
            }

            Properties = map;
            DefaultBlockValue = new BlockValue(blockId, defaults);
        }
    }

    /// <summary>
    /// Extensible block registry and BlockSpec validator aligned with wire §5.0.2, §7.1, §7.2.1, and §7.3.
    /// </summary>
    public sealed class SandboxBlockRegistry
    {
        private static readonly Regex NamespaceRegex = new Regex("^[a-z0-9_.-]+$", RegexOptions.Compiled);
        private static readonly Regex PathRegex = new Regex("^[a-z0-9_./-]+$", RegexOptions.Compiled);

        private readonly SortedDictionary<string, BlockDefinition> blocks = new SortedDictionary<string, BlockDefinition>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, BlockDefinition> Blocks => blocks;

        public SandboxBlockRegistry Register(BlockDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            blocks[definition.BlockId] = definition;
            return this;
        }

        /// <summary>
        /// Creates a registry populated with the 10 basic Unity Sandbox blocks (human owner decision 2026-11-14).
        /// </summary>
        public static SandboxBlockRegistry CreateBasic()
        {
            return new SandboxBlockRegistry()
                .Register(new BlockDefinition("minecraft:air"))
                .Register(new BlockDefinition("minecraft:grass_block",
                    new BlockPropertyDefinition(
                        "snowy",
                        new[] { BlockStateScalar.FromBoolean(false), BlockStateScalar.FromBoolean(true) },
                        BlockStateScalar.FromBoolean(false))))
                .Register(new BlockDefinition("minecraft:dirt"))
                .Register(new BlockDefinition("minecraft:stone"))
                .Register(new BlockDefinition("minecraft:gold_block"))
                .Register(new BlockDefinition("minecraft:iron_block"))
                .Register(new BlockDefinition("minecraft:sea_lantern"))
                .Register(new BlockDefinition("minecraft:glass"))
                .Register(new BlockDefinition("minecraft:diamond_block"))
                .Register(new BlockDefinition("minecraft:tnt",
                    new BlockPropertyDefinition(
                        "unstable",
                        new[] { BlockStateScalar.FromBoolean(false), BlockStateScalar.FromBoolean(true) },
                        BlockStateScalar.FromBoolean(false))));
        }

        public static bool IsCanonicalResourceId(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            int firstColon = value.IndexOf(':');
            if (firstColon <= 0) return false;
            if (value.IndexOf(':', firstColon + 1) >= 0) return false;

            string ns = value.Substring(0, firstColon);
            string path = value.Substring(firstColon + 1);
            return NamespaceRegex.IsMatch(ns) && PathRegex.IsMatch(path);
        }

        public static bool TryNormalizeResourceId(string raw, out string canonical)
        {
            canonical = string.Empty;
            if (string.IsNullOrEmpty(raw)) return false;

            if (raw.IndexOf(':') < 0)
            {
                if (!PathRegex.IsMatch(raw)) return false;
                canonical = "minecraft:" + raw;
                return true;
            }

            if (!IsCanonicalResourceId(raw)) return false;
            canonical = raw;
            return true;
        }

        public BlockValue ResolveAndValidate(JToken specToken, string paramPath = "params[3]")
        {
            if (specToken is not JObject specObj)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(paramPath));
            }

            if (specObj.Count != 2 ||
                !specObj.TryGetValue("block_id", StringComparison.Ordinal, out var blockIdToken) ||
                !specObj.TryGetValue("state", StringComparison.Ordinal, out var stateToken))
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(paramPath));
            }

            if (blockIdToken.Type != JTokenType.String)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(paramPath + ".block_id"));
            }

            if (stateToken is not JObject stateObj)
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams(paramPath + ".state"));
            }

            // Validate scalar-only values in state before block lookup or property lookup as needed.
            foreach (var prop in stateObj.Properties())
            {
                if (prop.Value.Type != JTokenType.Boolean &&
                    prop.Value.Type != JTokenType.Integer &&
                    prop.Value.Type != JTokenType.Float &&
                    prop.Value.Type != JTokenType.String)
                {
                    throw new SandboxOperationException(
                        SandboxProtocolError.InvalidParams($"{paramPath}.state.{prop.Name}"));
                }
            }

            string rawBlockId = blockIdToken.Value<string>()!;
            if (!TryNormalizeResourceId(rawBlockId, out string canonicalId))
            {
                throw new SandboxOperationException(SandboxProtocolError.UnknownBlock(rawBlockId));
            }

            if (!blocks.TryGetValue(canonicalId, out var definition))
            {
                throw new SandboxOperationException(SandboxProtocolError.UnknownBlock(canonicalId));
            }

            // Start from defaults (no merge with existing world block state).
            var resolvedState = new SortedDictionary<string, BlockStateScalar>(StringComparer.Ordinal);
            foreach (var kv in definition.Properties)
            {
                resolvedState[kv.Key] = kv.Value.DefaultValue;
            }

            foreach (var prop in stateObj.Properties())
            {
                if (!definition.Properties.TryGetValue(prop.Name, out var propDef))
                {
                    throw new SandboxOperationException(SandboxProtocolError.UnknownProperty(canonicalId, prop.Name));
                }

                bool matched = false;
                object? rawClrValue = null;
                foreach (var allowed in propDef.AllowedValues)
                {
                    if (allowed.MatchesWireToken(prop.Value, out rawClrValue))
                    {
                        resolvedState[prop.Name] = allowed;
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    var allowedClr = propDef.AllowedValues.Select(v => v.ToClrObject()).ToArray();
                    throw new SandboxOperationException(
                        SandboxProtocolError.InvalidPropertyValue(canonicalId, prop.Name, rawClrValue, allowedClr));
                }
            }

            return new BlockValue(canonicalId, resolvedState);
        }

        public BlockValue ResolveSimple(string blockId, IReadOnlyDictionary<string, BlockStateScalar>? partialState = null)
        {
            var stateObj = new JObject();
            if (partialState != null)
            {
                foreach (var kv in partialState)
                {
                    stateObj[kv.Key] = kv.Value.ToJValue();
                }
            }

            var spec = new JObject
            {
                ["block_id"] = blockId,
                ["state"] = stateObj
            };
            return ResolveAndValidate(spec, "spec");
        }

        /// <summary>
        /// Builds the canonical catalog payload and SHA-256 catalogHash per wire-format-design_ja.md §7.2.1.
        /// </summary>
        public JObject BuildCatalogResult()
        {
            var blockObj = new JObject();
            foreach (var kv in blocks)
            {
                var statesObj = new JObject();
                var defaultStateObj = new JObject();
                foreach (var prop in kv.Value.Properties)
                {
                    var arr = new JArray();
                    foreach (var v in prop.Value.AllowedValues)
                    {
                        arr.Add(v.ToJValue());
                    }
                    statesObj[prop.Key] = arr;
                    defaultStateObj[prop.Key] = prop.Value.DefaultValue.ToJValue();
                }

                blockObj[kv.Key] = new JObject
                {
                    ["default_state"] = defaultStateObj,
                    ["states"] = statesObj
                };
            }

            var body = new JObject
            {
                ["block"] = blockObj,
                ["entity"] = new JObject(),
                ["particle"] = new JObject()
            };

            string canonicalJson = ToSortedCanonicalJson(body);
            string hashHex;
            using (var sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(canonicalJson);
                byte[] digest = sha.ComputeHash(bytes);
                hashHex = BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
            }

            return new JObject
            {
                ["catalogHash"] = hashHex,
                ["block"] = blockObj,
                ["entity"] = new JObject(),
                ["particle"] = new JObject()
            };
        }

        public static string ToSortedCanonicalJson(JToken token)
        {
            return SortKeys(token).ToString(Formatting.None);
        }

        private static JToken SortKeys(JToken token)
        {
            if (token is JObject obj)
            {
                var sorted = new JObject();
                foreach (var prop in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    sorted[prop.Name] = SortKeys(prop.Value);
                }
                return sorted;
            }
            if (token is JArray arr)
            {
                var copy = new JArray();
                foreach (var item in arr)
                {
                    copy.Add(SortKeys(item));
                }
                return copy;
            }
            return token.DeepClone();
        }
    }
}
