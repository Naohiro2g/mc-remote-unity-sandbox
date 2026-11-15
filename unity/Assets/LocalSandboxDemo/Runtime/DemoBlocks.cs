using System;
using System.Collections.Generic;
using UnityEngine;

namespace Code2Create.UnitySandbox.LocalDemo
{
    internal sealed class DemoBlocks
    {
        private readonly Transform parent;
        private readonly DemoPalette palette;
        private readonly List<BoxEntry> boxes = new List<BoxEntry>();
        private readonly HashSet<Vector3Int> revealedEggCenters = new HashSet<Vector3Int>();

        private GameObject highlightCube;
        private float highlightRemaining;

        internal int Count { get; private set; }
        internal Vector3Int? LastPlacedCell { get; private set; }

        internal DemoBlocks(Transform parent, DemoPalette palette)
        {
            this.parent = parent;
            this.palette = palette;
            RebuildInitialTerrain();
        }

        internal void RebuildInitialTerrain()
        {
            ClearAllBoxes();
            Count = 0;
            LastPlacedCell = null;
            revealedEggCenters.Clear();

            // 1.21.11準拠の初期地形: y=59..-64 石、y=61..60 土、各区画[-48..48]のy=62は草（区画間はy=61の土を露出）
            AddBoxInternal(new Vector3Int(-200, -64, -200), new Vector3Int(199, 59, 199), "minecraft:stone", false, false, true);
            AddBoxInternal(new Vector3Int(-200, 60, -200), new Vector3Int(199, 61, 199), "minecraft:dirt", false, false, true);

            for (int plotIndex = 0; plotIndex < 9; plotIndex++)
            {
                var origin = DemoCoordinates.PlotOrigin(plotIndex);
                var plotMin = new Vector3Int(origin.x - 48, 62, origin.z - 48);
                var plotMax = new Vector3Int(origin.x + 48, 62, origin.z + 48);
                AddBoxInternal(plotMin, plotMax, "minecraft:grass_block", false, false, true);
            }
        }

        internal GameObject Put(Vector3Int cell, DemoMaterial kind)
        {
            string blockId = DemoPalette.BlockIdOf(kind);
            return ApplyBox(cell, cell, blockId, snowy: false, unstable: false, trackPlacement: false, countAsModified: true);
        }

        internal GameObject ApplyBox(
            Vector3Int min,
            Vector3Int max,
            string blockId,
            bool snowy = false,
            bool unstable = false,
            bool trackPlacement = true,
            bool countAsModified = true)
        {
            NormalizeBounds(ref min, ref max);
            bool isAir = string.Equals(blockId, "minecraft:air", StringComparison.Ordinal);

            if (isAir && min.y <= 59)
            {
                RevealNearbyEasterEggs(min, max);
            }

            CarveBox(min, max, countAsModified);

            GameObject created = null;
            if (!isAir)
            {
                created = AddBoxInternal(min, max, blockId, snowy, unstable, isTerrain: false);
                if (countAsModified)
                {
                    Count += BoxVolume(min, max);
                }

                if (trackPlacement)
                {
                    LastPlacedCell = new Vector3Int(
                        (min.x + max.x) / 2,
                        max.y,
                        (min.z + max.z) / 2);
                    ShowHighlight(min, max);
                }
            }
            else if (trackPlacement)
            {
                LastPlacedCell = new Vector3Int(
                    (min.x + max.x) / 2,
                    min.y,
                    (min.z + max.z) / 2);
            }

            return created;
        }

        internal bool Remove(Vector3Int cell)
        {
            return CarveBox(cell, cell, countAsModified: true) > 0;
        }

        internal void Clear()
        {
            // 非地形ブロックだけを消去し、地形の穴は初期状態へ戻す。
            RebuildInitialTerrain();
        }

        internal void Dispose()
        {
            ClearAllBoxes();
            if (highlightCube != null)
            {
                DemoPalette.Release(highlightCube);
                highlightCube = null;
            }
        }

        internal void UpdateHighlight(float deltaTime)
        {
            if (highlightCube == null || highlightRemaining <= 0f) return;
            highlightRemaining -= deltaTime;
            if (highlightRemaining <= 0f)
            {
                highlightCube.SetActive(false);
            }
        }

        private void ShowHighlight(Vector3Int min, Vector3Int max)
        {
            if (highlightCube == null)
            {
                highlightCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                highlightCube.name = "Placement highlight";
                highlightCube.transform.SetParent(parent, false);
                var col = highlightCube.GetComponent<Collider>();
                if (col != null) col.enabled = false;
                highlightCube.GetComponent<Renderer>().sharedMaterial = palette.Highlight;
            }

            Vector3 size = new Vector3(max.x - min.x + 1.06f, max.y - min.y + 1.06f, max.z - min.z + 1.06f);
            Vector3 center = new Vector3(
                (min.x + max.x + 1) * 0.5f,
                (min.y + max.y + 1) * 0.5f,
                (min.z + max.z + 1) * 0.5f);

            highlightCube.transform.position = center;
            highlightCube.transform.localScale = size;
            highlightCube.SetActive(true);
            highlightRemaining = 2.0f;
        }

        private void RevealNearbyEasterEggs(Vector3Int cutMin, Vector3Int cutMax)
        {
            for (int plotIndex = 0; plotIndex < 9; plotIndex++)
            {
                var origin = DemoCoordinates.PlotOrigin(plotIndex);
                if (cutMax.x < origin.x - 4 || cutMin.x > origin.x + 4 ||
                    cutMax.z < origin.z - 4 || cutMin.z > origin.z + 4)
                {
                    continue;
                }

                for (int y = -60; y <= 50; y += 10)
                {
                    if (y < cutMin.y - 3 || y > cutMax.y + 3) continue;
                    var key = new Vector3Int(origin.x, y, origin.z);
                    if (!revealedEggCenters.Add(key)) continue;

                    string eggBlock = EasterEggBlockForY(y);
                    var plateMin = new Vector3Int(origin.x - 1, y, origin.z - 1);
                    var plateMax = new Vector3Int(origin.x + 1, y, origin.z + 1);
                    CarveBox(plateMin, plateMax, countAsModified: false);
                    AddBoxInternal(plateMin, plateMax, eggBlock, false, false, isTerrain: true);
                }
            }
        }

        private static string EasterEggBlockForY(int y)
        {
            switch (y)
            {
                case 50:
                case -10:
                    return "minecraft:iron_block";
                case 30:
                case -30:
                    return "minecraft:sea_lantern";
                case 20:
                    return "minecraft:glass";
                case 0:
                case -40:
                    return "minecraft:diamond_block";
                case -60:
                    return "minecraft:tnt";
                default:
                    return "minecraft:gold_block";
            }
        }

        private int CarveBox(Vector3Int cutMin, Vector3Int cutMax, bool countAsModified)
        {
            int removedCells = 0;
            for (int i = boxes.Count - 1; i >= 0; i--)
            {
                var existing = boxes[i];
                if (!Intersects(existing.Min, existing.Max, cutMin, cutMax, out var ixMin, out var ixMax))
                {
                    continue;
                }

                int overlapVolume = BoxVolume(ixMin, ixMax);
                removedCells += overlapVolume;
                if (countAsModified && !existing.IsTerrain)
                {
                    Count = Math.Max(0, Count - overlapVolume);
                }

                existing.Visual.SetActive(false);
                DemoPalette.Release(existing.Visual);
                boxes.RemoveAt(i);

                AddSplitRemains(existing, ixMin, ixMax);
            }
            return removedCells;
        }

        private void AddSplitRemains(BoxEntry orig, Vector3Int ixMin, Vector3Int ixMax)
        {
            Vector3Int min = orig.Min;
            Vector3Int max = orig.Max;

            if (min.x < ixMin.x)
                AddBoxInternal(new Vector3Int(min.x, min.y, min.z), new Vector3Int(ixMin.x - 1, max.y, max.z), orig.BlockId, orig.Snowy, orig.Unstable, orig.IsTerrain);
            if (ixMax.x < max.x)
                AddBoxInternal(new Vector3Int(ixMax.x + 1, min.y, min.z), new Vector3Int(max.x, max.y, max.z), orig.BlockId, orig.Snowy, orig.Unstable, orig.IsTerrain);
            if (min.y < ixMin.y)
                AddBoxInternal(new Vector3Int(ixMin.x, min.y, min.z), new Vector3Int(ixMax.x, ixMin.y - 1, max.z), orig.BlockId, orig.Snowy, orig.Unstable, orig.IsTerrain);
            if (ixMax.y < max.y)
                AddBoxInternal(new Vector3Int(ixMin.x, ixMax.y + 1, min.z), new Vector3Int(ixMax.x, max.y, max.z), orig.BlockId, orig.Snowy, orig.Unstable, orig.IsTerrain);
            if (min.z < ixMin.z)
                AddBoxInternal(new Vector3Int(ixMin.x, ixMin.y, min.z), new Vector3Int(ixMax.x, ixMax.y, ixMin.z - 1), orig.BlockId, orig.Snowy, orig.Unstable, orig.IsTerrain);
            if (ixMax.z < max.z)
                AddBoxInternal(new Vector3Int(ixMin.x, ixMin.y, ixMax.z + 1), new Vector3Int(ixMax.x, ixMax.y, max.z), orig.BlockId, orig.Snowy, orig.Unstable, orig.IsTerrain);
        }

        private GameObject AddBoxInternal(Vector3Int min, Vector3Int max, string blockId, bool snowy, bool unstable, bool isTerrain)
        {
            Vector3 size = new Vector3(max.x - min.x + 1, max.y - min.y + 1, max.z - min.z + 1);
            Vector3 center = new Vector3(
                (min.x + max.x + 1) * 0.5f,
                (min.y + max.y + 1) * 0.5f,
                (min.z + max.z + 1) * 0.5f);

            bool isGrass = string.Equals(blockId, "minecraft:grass_block", StringComparison.Ordinal);

            Material mat = palette.MaterialFor(blockId, unstable);
            var go = Cube(blockId, parent, center, size, mat);

            if (!isTerrain && size != Vector3.one && mat.mainTexture != null)
            {
                ApplyRepeatedBlockUvs(go, size);
            }

            if (isGrass)
            {
                var topMat = snowy ? palette.SnowyGrassTop : palette.GrassTop;
                float topW = isTerrain ? size.x : Mathf.Max(0.98f, size.x - 0.02f);
                float topD = isTerrain ? size.z : Mathf.Max(0.98f, size.z - 0.02f);
                var top = Cube(
                    "Grass top",
                    go.transform,
                    new Vector3(center.x, max.y + 1.0f, center.z),
                    new Vector3(topW, 0.02f, topD),
                    topMat);
                // topはvisual専用。WorldScaleに影響されないようワールド座標基準に補正する。
                top.transform.localScale = new Vector3(
                    topW / size.x,
                    0.02f / size.y,
                    topD / size.z);
                top.transform.localPosition = new Vector3(0f, 0.5f, 0f);
                var col = top.GetComponent<Collider>();
                if (col != null) col.enabled = false;
            }

            boxes.Add(new BoxEntry(min, max, blockId, snowy, unstable, isTerrain, go));
            return go;
        }

        private void ClearAllBoxes()
        {
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Visual != null)
                {
                    boxes[i].Visual.SetActive(false);
                    DemoPalette.Release(boxes[i].Visual);
                }
            }
            boxes.Clear();
        }

        private static bool Intersects(
            Vector3Int aMin, Vector3Int aMax,
            Vector3Int bMin, Vector3Int bMax,
            out Vector3Int ixMin, out Vector3Int ixMax)
        {
            ixMin = new Vector3Int(Mathf.Max(aMin.x, bMin.x), Mathf.Max(aMin.y, bMin.y), Mathf.Max(aMin.z, bMin.z));
            ixMax = new Vector3Int(Mathf.Min(aMax.x, bMax.x), Mathf.Min(aMax.y, bMax.y), Mathf.Min(aMax.z, bMax.z));
            return ixMin.x <= ixMax.x && ixMin.y <= ixMax.y && ixMin.z <= ixMax.z;
        }

        private static int BoxVolume(Vector3Int min, Vector3Int max)
        {
            return (max.x - min.x + 1) * (max.y - min.y + 1) * (max.z - min.z + 1);
        }

        private static void NormalizeBounds(ref Vector3Int min, ref Vector3Int max)
        {
            var lo = new Vector3Int(Mathf.Min(min.x, max.x), Mathf.Min(min.y, max.y), Mathf.Min(min.z, max.z));
            var hi = new Vector3Int(Mathf.Max(min.x, max.x), Mathf.Max(min.y, max.y), Mathf.Max(min.z, max.z));
            min = lo;
            max = hi;
        }

        internal static GameObject Cube(string name, Transform parent, Vector3 center, Vector3 size, Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.position = center;
            cube.transform.localScale = size;
            // Ensure WebGL IL2CPP stripping preserves BoxCollider and Collider.
            var col = cube.GetComponent<BoxCollider>();
            if (col == null) col = cube.AddComponent<BoxCollider>();
            col.enabled = true;
            cube.GetComponent<Renderer>().sharedMaterial = material;
            return cube;
        }

        private static void ApplyRepeatedBlockUvs(GameObject cube, Vector3 size)
        {
            var mf = cube.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;

            var mesh = UnityEngine.Object.Instantiate(mf.sharedMesh);
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uvs = new Vector2[vertices.Length];

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                Vector3 n = normals[i];
                float ux = v.x + 0.5f;
                float uy = v.y + 0.5f;
                float uz = v.z + 0.5f;

                if (Mathf.Abs(n.y) > 0.5f)
                {
                    uvs[i] = new Vector2(ux * size.x, uz * size.z);
                }
                else if (Mathf.Abs(n.z) > 0.5f)
                {
                    uvs[i] = new Vector2(ux * size.x, uy * size.y);
                }
                else
                {
                    uvs[i] = new Vector2(uz * size.z, uy * size.y);
                }
            }

            mesh.uv = uvs;
            mf.sharedMesh = mesh;
            cube.AddComponent<DemoOwnedMesh>().Value = mesh;
        }

        private sealed class DemoOwnedMesh : MonoBehaviour
        {
            internal Mesh Value;
            private void OnDestroy() => DemoPalette.Release(Value);
        }

        private sealed class BoxEntry
        {
            public readonly Vector3Int Min;
            public readonly Vector3Int Max;
            public readonly string BlockId;
            public readonly bool Snowy;
            public readonly bool Unstable;
            public readonly bool IsTerrain;
            public readonly GameObject Visual;

            public BoxEntry(Vector3Int min, Vector3Int max, string blockId, bool snowy, bool unstable, bool isTerrain, GameObject visual)
            {
                Min = min;
                Max = max;
                BlockId = blockId;
                Snowy = snowy;
                Unstable = unstable;
                IsTerrain = isTerrain;
                Visual = visual;
            }
        }
    }
}
