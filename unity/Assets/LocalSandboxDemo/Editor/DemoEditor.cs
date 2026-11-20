using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Code2Create.UnitySandbox.LocalDemo
{
    public static class DemoEditor
    {
        private const string MainScenePath = "Assets/LocalSandboxDemo/Scenes/LocalSandboxDemo.unity";

        [MenuItem("Tools/Local Sandbox/Create Demo Scene")]
        public static void CreateScene()
        {
            CheckPipeline();
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Local Sandbox Demo").AddComponent<LocalSandboxDemo>();
            Directory.CreateDirectory("Assets/LocalSandboxDemo/Scenes");
            string path = Application.isBatchMode
                ? MainScenePath
                : AssetDatabase.GenerateUniqueAssetPath(MainScenePath);
            EditorSceneManager.SaveScene(scene, path);
            Selection.activeObject = GameObject.Find("Local Sandbox Demo");
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.LookAt(
                    DemoCoordinates.McToUnityPos(new Vector3(100.5f, 64f, 100.5f)),
                    Quaternion.Euler(25, 0, 0),
                    25);
            Debug.Log("デモシーンを作成しました。Playで開始します: " + path);
        }

        [MenuItem("Tools/Local Sandbox/Build WebGL")]
        public static void BuildWebGL()
        {
            CheckPipeline();
            EnsureAlwaysIncludedShader("Standard");
            EnsureAlwaysIncludedShader("Sprites/Default");

            if (!File.Exists(MainScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("Local Sandbox Demo").AddComponent<LocalSandboxDemo>();
                Directory.CreateDirectory("Assets/LocalSandboxDemo/Scenes");
                EditorSceneManager.SaveScene(scene, MainScenePath);
            }

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(MainScenePath, true)
            };

            PlayerSettings.runInBackground = true;
            string versionFile = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "VERSION"));
            if (File.Exists(versionFile))
            {
                string versionText = File.ReadAllText(versionFile).Trim();
                if (!string.IsNullOrEmpty(versionText))
                {
                    PlayerSettings.bundleVersion = versionText;
                }
            }
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Minimal);
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.defaultWebScreenWidth = 1280;
            PlayerSettings.defaultWebScreenHeight = 720;

            string outputDir = Environment.GetEnvironmentVariable("UNITY_WEBGL_BUILD_PATH");
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                outputDir = Path.GetFullPath("Builds/WebGL");
            }
            Directory.CreateDirectory(outputDir);

            var buildOptions = new BuildPlayerOptions
            {
                scenes = new[] { MainScenePath },
                locationPathName = outputDir,
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            };

            var report = BuildPipeline.BuildPlayer(buildOptions);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException($"WebGL build failed: {report.summary.result}, errors={report.summary.totalErrors}");
            }

            ApplyResponsiveWebGlLayout(outputDir);
            Debug.Log($"LOCAL_SANDBOX_WEBGL_BUILD_PASS: {outputDir} ({report.summary.totalSize} bytes)");
        }

        private static void ApplyResponsiveWebGlLayout(string outputDir)
        {
            string indexPath = Path.Combine(outputDir, "index.html");
            if (!File.Exists(indexPath)) return;
            string html = File.ReadAllText(indexPath);
            const string marker = "<!-- unity-sandbox-responsive -->";
            if (html.Contains(marker)) return;
            const string responsiveCss =
                "    " + marker + "\n" +
                "    <style>\n" +
                "      html, body { width: 100%; height: 100%; margin: 0; padding: 0; overflow: hidden; background: #1e242b; }\n" +
                "      #unity-container.unity-desktop { position: fixed; inset: 0; width: 100%; height: 100%; left: 0; top: 0; transform: none; }\n" +
                "      #unity-fullscreen-container, #unity-canvas { width: 100% !important; height: 100% !important; display: block; }\n" +
                "      #unity-footer { display: none; }\n" +
                "    </style>\n";
            html = html.Replace("</head>", responsiveCss + "  </head>");
            File.WriteAllText(indexPath, html);
        }

        [MenuItem("Tools/Local Sandbox/Validate Demo")]
        public static void Validate()
        {
            CheckPipeline();
            // 利用者の編集中のシーンを保存・破棄せず、独立したsceneで検証する。
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Validation demo");
                SceneManager.MoveGameObjectToScene(root, scene);
                var demo = root.AddComponent<LocalSandboxDemo>();
                demo.Build();
                Require(DemoCoordinates.Cell(DemoCoordinates.McToUnityPos(new Vector3(-.01f, 63, -1.01f))) == new Vector3Int(-1, 63, -2), "negative floor");
                Require(DemoCoordinates.Center(new Vector3Int(-1, 62, -1)) == new Vector3(-.5f, 62.5f, .5f), "cell center");
                Require(DemoCoordinates.PlotOrigin(0) == new Vector3Int(-100, 0, -100), "plot 0");
                Require(DemoCoordinates.PlotOrigin(4) == Vector3Int.zero, "plot 4");
                Require(DemoCoordinates.PlotOrigin(8) == new Vector3Int(100, 0, 100), "plot 8");
                Require(DemoCoordinates.Relative(new Vector3Int(120, 63, 130), demo.DisplayOrigin) == new Vector3Int(20, 63, 30), "relative coordinates");
                Require(demo.Blocks.Count == 120, "six materials, ten in row and ten in column");
                Physics.SyncTransforms();
                var physics = scene.GetPhysicsScene();
                Require(physics.Raycast(DemoCoordinates.McToUnityPos(new Vector3(-.01f, 80, -1.01f)), Vector3.down, out var ground, 30), "ground collider");
                Require(DemoCoordinates.HitCell(ground) == new Vector3Int(-1, 62, -2), "ground top y=62");
                var body = demo.GetComponentInChildren<CharacterController>();
                body.Move(Vector3.down * 2);
                Require(DemoCoordinates.Cell(body.transform.position).y == 63, "feet at ground: " + body.transform.position);

                // Dug grass reveals dirt at y=61
                demo.Blocks.ApplyBox(new Vector3Int(0, 62, 0), new Vector3Int(0, 62, 0), "minecraft:air", trackPlacement: false, countAsModified: false);
                Physics.SyncTransforms();
                Require(physics.Raycast(DemoCoordinates.McToUnityPos(new Vector3(0.5f, 70, 0.5f)), Vector3.down, out var dugHit, 20), "dug cell hits dirt");
                Require(DemoCoordinates.HitCell(dugHit) == new Vector3Int(0, 61, 0), "dug cell reveals y=61 dirt");

                // Restore terrain and test replace/remove invariants
                demo.Populate(false);
                var cell = new Vector3Int(-10, 64, -10);
                demo.Blocks.Put(cell, DemoMaterial.Gold);
                var replacement = demo.Blocks.Put(cell, DemoMaterial.Iron);
                Require(demo.Blocks.Count == 121, "replace keeps count");
                Physics.SyncTransforms();
                foreach (var normal in new[] { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back })
                {
                    Require(physics.Raycast(DemoCoordinates.Center(cell) + normal * 2, -normal, out var hit, 2), "six faces ray");
                    Require(hit.collider.gameObject == replacement, "replacement collider");
                    Require(DemoCoordinates.HitCell(hit) == cell, "six faces cell");
                }
                Require(demo.Blocks.Remove(cell) && !demo.Blocks.Remove(cell), "remove and missing cell");
                demo.Populate(true);
                Require(demo.Blocks.Count == 150, "wall and staircase");
                demo.Populate(false);
                Require(demo.Blocks.Count == 120, "scene reset");

                // Warp to plot landing facing North in flying mode
                var walker = demo.GetComponentInChildren<LocalSandboxWalker>();
                Require(walker != null, "walker exists");
                walker.WarpToPlot(4);
                Require(walker.IsFlying, "warp enables flying");
                Require(DemoCoordinates.Cell(walker.transform.position) == new Vector3Int(5, 96, 40), "warp plot 4 pos");
                Vector3 toPlot4Origin = (DemoCoordinates.McToUnityPos(new Vector3(0.5f, 96f, 0.5f)) - demo.View.transform.position).normalized;
                Require(Vector3.Dot(demo.View.transform.forward, toPlot4Origin) > 0.999f, "warp plot 4 look at origin");
                walker.WarpToPlot(0);
                Require(DemoCoordinates.Cell(walker.transform.position) == new Vector3Int(-95, 96, -60), "warp plot 0 pos");
                walker.ResetPose();
                Require(!walker.IsFlying, "reset clears flying");

                // Placement highlight stays for 0.5s and honors on/off toggle
                Require(demo.Blocks.HighlightEnabled, "highlight enabled by default");
                var hlCell = new Vector3Int(10, 64, 10);
                demo.Blocks.ApplyBox(hlCell, hlCell, "minecraft:gold_block", trackPlacement: true, countAsModified: true);
                Require(demo.Blocks.IsHighlightActive, "highlight shown on placement");
                demo.Blocks.UpdateHighlight(0.4f);
                Require(demo.Blocks.IsHighlightActive, "highlight active at 0.4s");
                demo.Blocks.UpdateHighlight(0.2f);
                Require(!demo.Blocks.IsHighlightActive, "highlight expires after 0.5s");
                demo.Blocks.HighlightEnabled = false;
                var hlCell2 = new Vector3Int(11, 64, 10);
                demo.Blocks.ApplyBox(hlCell2, hlCell2, "minecraft:iron_block", trackPlacement: true, countAsModified: true);
                Require(!demo.Blocks.IsHighlightActive && demo.Blocks.LastPlacedCell == hlCell2, "highlight off suppresses glow but tracks cell");
                demo.Blocks.HighlightEnabled = true;
                demo.Populate(false);

                ValidateTargetHud(demo, physics);
                ValidateRightHandedOrientationAndNorthCamera(demo);

                Debug.Log("LOCAL_SANDBOX_VALIDATION_PASS: right-handed north view, negative/relative coordinates, plots, warp, highlight, dug terrain, six hit faces, ground, replace/remove, 16m HUD target, scene reset");
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void ValidateTargetHud(LocalSandboxDemo demo, PhysicsScene physics)
        {
            // Grass top must be completely matte to avoid plastic specular glare
            Material grassTopMat = null;
            foreach (var r in demo.GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject.name == "Grass top")
                {
                    grassTopMat = r.sharedMaterial;
                    break;
                }
            }
            Require(grassTopMat != null, "grass top material found");
            Require(grassTopMat.GetFloat("_Metallic") == 0f && grassTopMat.GetFloat("_Glossiness") <= 0.05f, "grass top is matte");

            // Distance limit (16m) for McRemote-aligned target raycast & hairline outline (camera south facing north)
            var targetCell = new Vector3Int(20, 64, 5);
            demo.Blocks.ApplyBox(targetCell, targetCell, "minecraft:gold_block", trackPlacement: false, countAsModified: false);
            Physics.SyncTransforms();
            Vector3 northRayDir = DemoCoordinates.McToUnityDir(Vector3.back); // McRemote -Z = North = Unity +Z
            Require(
                physics.Raycast(DemoCoordinates.McToUnityPos(new Vector3(20.5f, 64.5f, 19.0f)), northRayDir, out var nearHit, LocalSandboxDemo.MaxTargetDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                DemoCoordinates.HitCell(nearHit) == targetCell,
                "target hit within 16m");
            Require(
                !physics.Raycast(DemoCoordinates.McToUnityPos(new Vector3(20.5f, 64.5f, 22.5f)), northRayDir, out _, LocalSandboxDemo.MaxTargetDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore),
                "target miss beyond 16m");

            demo.View.transform.position = DemoCoordinates.McToUnityPos(new Vector3(20.5f, 64.5f, 19.0f));
            demo.View.transform.rotation = Quaternion.LookRotation(northRayDir, Vector3.up);
            demo.RefreshTargetOutline();
            Require(demo.IsTargetOutlineActive && demo.CurrentTargetCell == targetCell, "hairline outline active within 16m");

            demo.View.transform.position = DemoCoordinates.McToUnityPos(new Vector3(20.5f, 64.5f, 22.5f));
            demo.RefreshTargetOutline();
            Require(!demo.IsTargetOutlineActive && demo.CurrentTargetCell == null, "hairline outline hidden beyond 16m");

            var walker = demo.GetComponentInChildren<LocalSandboxWalker>();
            if (walker != null) walker.ResetPose();

            // McRemote-aligned target/origin missing coordinate formatting
            LocalSandboxDemo.FormatHudCoordinates(null, demo.DisplayOrigin, relativeOriginAvailable: true, out string missAbs, out string missRel);
            Require(missAbs == "[-]" && missRel == "[-]", "target miss paired format");

            LocalSandboxDemo.FormatHudCoordinates(new Vector3Int(110, 63, 120), demo.DisplayOrigin, relativeOriginAvailable: false, out string legsAbs, out string noOriginRel);
            Require(legsAbs == "110 63 120" && noOriginRel == "[ - - - ]", "unpaired legs format");

            LocalSandboxDemo.FormatHudCoordinates(null, demo.DisplayOrigin, relativeOriginAvailable: false, out string bothMissAbs, out string bothMissRel);
            Require(bothMissAbs == "[-]" && bothMissRel == "[ - - - ]", "target miss without origin format");

            demo.Populate(false);
            Physics.SyncTransforms();
            Require(demo.Blocks.Count == 120, "scene reset after HUD validation");
        }

        private static void ValidateRightHandedOrientationAndNorthCamera(LocalSandboxDemo demo)
        {
            var walker = demo.GetComponentInChildren<LocalSandboxWalker>();
            Require(walker != null, "walker exists for orientation validation");
            walker.ResetPose();

            // 1. Initial camera spawns south of Plot 8 facing due North (mcYaw = -180, HUD azimuth 0 / 北)
            Require(DemoCoordinates.Cell(walker.transform.position) == new Vector3Int(100, 63, 114), "initial spawn cell (100, 63, 114)");
            walker.GetMinecraftPose(out Vector3 initMcPos, out float initMcYaw, out float initMcPitch);
            Require(Vector3.Distance(initMcPos, new Vector3(100.5f, 63.04f, 114.5f)) < 0.01f, "initial spawn exact pos");
            Require(Mathf.Abs(Mathf.DeltaAngle(initMcYaw, -180f)) < 0.01f, "initial camera yaw is -180 (North)");
            Require(Mathf.Abs(initMcPitch - 8f) < 0.01f, "initial camera pitch is 8");
            Vector3 initMcFwd = DemoCoordinates.UnityToMcDir(walker.transform.forward);
            Require(Vector3.Dot(initMcFwd, Vector3.back) > 0.999f, "initial forward is McRemote -Z (North)");
            demo.BuildMcRemoteHudLines(out _, out string initHud);
            Require(initHud.Contains("#北#") || initHud.Contains("#N#"), "initial HUD shows North: " + initHud);

            // 2. Right-handed screen projection checks for all 4 cardinal headings around Plot 4 center (0.5, 63.04, 0.5)
            demo.View.aspect = 16f / 9f;
            Vector3 pivot = new Vector3(0.5f, 63.04f, 0.5f);
            Vector3 eyeMc = pivot + Vector3.up * 1.6f;

            var cardinalCases = new[]
            {
                (mcYaw: -180f, name: "North", expectedJa: "#北#", expectedEn: "#N#", expectedDir: new Vector3(0f, 0f, -1f), expectedMcRight: new Vector3(1f, 0f, 0f)),
                (mcYaw: -90f,  name: "East",  expectedJa: "#東#", expectedEn: "#E#", expectedDir: new Vector3(1f, 0f, 0f),  expectedMcRight: new Vector3(0f, 0f, 1f)),
                (mcYaw: 0f,    name: "South", expectedJa: "#南#", expectedEn: "#S#", expectedDir: new Vector3(0f, 0f, 1f),  expectedMcRight: new Vector3(-1f, 0f, 0f)),
                (mcYaw: 90f,   name: "West",  expectedJa: "#西#", expectedEn: "#W#", expectedDir: new Vector3(-1f, 0f, 0f), expectedMcRight: new Vector3(0f, 0f, -1f)),
            };

            foreach (var c in cardinalCases)
            {
                walker.ApplyRemotePose(true, pivot, true, c.mcYaw, 0f);
                walker.GetMinecraftPose(out Vector3 roundPos, out float roundYaw, out float roundPitch);
                Require(Vector3.Distance(roundPos, pivot) < 0.001f, c.name + " pose pos roundtrip");
                Require(Mathf.Abs(Mathf.DeltaAngle(roundYaw, c.mcYaw)) < 0.01f, c.name + " pose yaw roundtrip");
                Require(Mathf.Abs(roundPitch) < 0.01f, c.name + " pose pitch roundtrip");

                Vector3 actualMcDir = DemoCoordinates.UnityToMcDir(demo.View.transform.forward);
                Require(Vector3.Distance(actualMcDir, c.expectedDir) < 0.001f, c.name + " direction vector " + actualMcDir);
                Vector3 actualMcRight = DemoCoordinates.UnityToMcDir(demo.View.transform.right);
                Require(Vector3.Distance(actualMcRight, c.expectedMcRight) < 0.001f, c.name + " right vector " + actualMcRight);

                demo.BuildMcRemoteHudLines(out _, out string hudLine);
                Require(hudLine.Contains(c.expectedJa) || hudLine.Contains(c.expectedEn), c.name + " HUD compass: " + hudLine);

                Vector3 unityFrontRight = DemoCoordinates.McToUnityPos(eyeMc + c.expectedDir * 10f + c.expectedMcRight * 4f);
                Vector3 unityFrontLeft = DemoCoordinates.McToUnityPos(eyeMc + c.expectedDir * 10f - c.expectedMcRight * 4f);
                Vector3 vpRight = demo.View.WorldToViewportPoint(unityFrontRight);
                Vector3 vpLeft = demo.View.WorldToViewportPoint(unityFrontLeft);
                Require(vpRight.z > 0f && vpRight.x > 0.55f, c.name + " right target must appear on right of screen: " + vpRight);
                Require(vpLeft.z > 0f && vpLeft.x < 0.45f, c.name + " left target must appear on left of screen: " + vpLeft);
            }

            // 3. Oblique yaw/pitch forward vector must match (-sin(yaw)*cos(pitch), -sin(pitch), cos(yaw)*cos(pitch))
            float testYaw = -135f;
            float testPitch = 25f;
            walker.ApplyRemotePose(true, pivot, true, testYaw, testPitch);
            Vector3 obliqueActual = DemoCoordinates.UnityToMcDir(demo.View.transform.forward);
            float yr = testYaw * Mathf.Deg2Rad;
            float pr = testPitch * Mathf.Deg2Rad;
            Vector3 obliqueExpected = new Vector3(-Mathf.Sin(yr) * Mathf.Cos(pr), -Mathf.Sin(pr), Mathf.Cos(yr) * Mathf.Cos(pr));
            Require(Vector3.Distance(obliqueActual, obliqueExpected) < 0.001f, "oblique direction formula match");

            // 4. Plot labels and +Z South guide line colors/orientation
            Color expectedPaleGreen = new Color(.85f, .9f, .75f);
            var generatedRoot = demo.transform.Find("Local demo (generated)");
            Require(generatedRoot != null, "generated root exists");
            for (int i = 0; i < 9; i++)
            {
                var labelObj = generatedRoot.Find($"PlotLabel_{i}");
                Require(labelObj != null, $"PlotLabel_{i} exists");
                var tm = labelObj.GetComponent<TextMesh>();
                Require(tm != null && Vector4.Distance(tm.color, expectedPaleGreen) < 0.01f, $"PlotLabel_{i} is pale green");
                Require(Quaternion.Angle(labelObj.rotation, Quaternion.identity) < 0.01f, $"PlotLabel_{i} faces South (readable when looking North)");
            }
            var origin4Z = generatedRoot.Find("Origin 4 +Z");
            Require(origin4Z != null, "Origin 4 +Z guide exists");
            var lr = origin4Z.GetComponent<LineRenderer>();
            Require(lr != null && lr.GetPosition(1).z < -40f, "Origin 4 +Z points South (-uZ)");

            walker.ResetPose();
        }

        // -executeMethod ...DemoEditor.CapturePreview -demoPreview /absolute/path.png
        public static void CapturePreview()
        {
            CheckPipeline();
            var scene = EditorSceneManager.NewPreviewScene();
            RenderTexture target = null;
            Texture2D image = null;
            var previousTarget = RenderTexture.active;
            try
            {
                var root = new GameObject("Preview");
                SceneManager.MoveGameObjectToScene(root, scene);
                var demo = root.AddComponent<LocalSandboxDemo>();
                demo.Build();
                demo.View.scene = scene;
                demo.View.transform.position = DemoCoordinates.McToUnityPos(new Vector3(100.5f, 90f, 145f));
                demo.View.transform.LookAt(DemoCoordinates.McToUnityPos(new Vector3(100.5f, 66f, 97f)));
                target = new RenderTexture(1440, 900, 24);
                demo.View.targetTexture = target;
                demo.View.Render();
                RenderTexture.active = target;
                image = new Texture2D(1440, 900, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 1440, 900), 0, 0);
                image.Apply();
                var args = Environment.GetCommandLineArgs();
                int flag = Array.IndexOf(args, "-demoPreview");
                if (flag < 0 || flag + 1 >= args.Length) throw new ArgumentException("-demoPreview <path.png> が必要です。");
                File.WriteAllBytes(args[flag + 1], image.EncodeToPNG());
                Debug.Log("LOCAL_SANDBOX_PREVIEW_SAVED: " + args[flag + 1]);
            }
            finally
            {
                RenderTexture.active = previousTarget;
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void EnsureAlwaysIncludedShader(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) return;

            var settings = GraphicsSettings.GetGraphicsSettings();
            if (settings == null) return;
            var so = new SerializedObject(settings);
            var prop = so.FindProperty("m_AlwaysIncludedShaders");
            if (prop == null) return;

            for (int i = 0; i < prop.arraySize; i++)
            {
                if (prop.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                {
                    return;
                }
            }

            prop.InsertArrayElementAtIndex(prop.arraySize);
            prop.GetArrayElementAtIndex(prop.arraySize - 1).objectReferenceValue = shader;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        private static void CheckPipeline()
        {
            if (GraphicsSettings.defaultRenderPipeline != null)
                throw new InvalidOperationException("この試作は3D (Built-in Render Pipeline)用です。");
        }

        private static void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Local sandbox validation failed: " + check);
        }
    }
}
