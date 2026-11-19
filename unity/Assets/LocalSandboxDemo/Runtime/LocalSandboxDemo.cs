using System;
using System.Collections.Generic;
using UnityEngine;

namespace Code2Create.UnitySandbox.LocalDemo
{
    [DisallowMultipleComponent]
    public sealed class LocalSandboxDemo : MonoBehaviour
    {
#if !(UNITY_WEBGL && !UNITY_EDITOR)
        private const string ChatControlName = "SandboxChatInput";
#endif
        private const int MaxChatLogLines = 80;

#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void SandboxWsInstallGuards();
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void SandboxWsToggleFullscreen();
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern string SandboxWsGetInitialLanguage();
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void SandboxWsSaveLanguage(string lang);
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void SandboxWsSyncChatInputLayout(
            float nx, float ny, float nw, float nh, string placeholder, int visible);
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern int SandboxWsIsChatFocused();
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void SandboxWsFocusChat(int setSlashIfEmpty, int moveCaretToEnd);
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void SandboxWsBlurChat();
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern string SandboxWsGetChatText();
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern void SandboxWsSetChatText(string text, int moveCaretToEnd);
        [System.Runtime.InteropServices.DllImport("__Internal")] private static extern string SandboxWsPopSubmittedChat();
#endif

        [SerializeField] private Vector3Int displayOrigin = new Vector3Int(100, 0, 100);
        [SerializeField, Range(1, 20)] private int columnHeight = 10;
        [SerializeField] private Font hudFont;

        private DemoPalette palette;
        private DemoBlocks blocks;
        private GameObject generated;
        private LocalSandboxWalker walker;
        private SandboxSyncClient syncClient;

        private const float DoubleTapThreshold = 0.35f;
        private const float CoordShadowOffsetPx = 0.8f;
        internal const float MaxTargetDistance = 16f;

        private Font loadedFont;
        private GUIStyle textStyle;
        private GUIStyle coordTextStyle;
        private GUIStyle coordShadowStyle;
        private GUIStyle smallTextStyle;
        private GUIStyle chatTitleStyle;
        private GUIStyle chatLogStyle;
        private GUIStyle buttonStyle;
        private GUIStyle inputStyle;
        private GUIStyle darkBoxStyle;
        private bool lastCompactStyle;

        private DemoLanguage currentLang = DemoLanguage.Ja;
        private readonly TextMesh[] plotLabelMains = new TextMesh[9];
        private readonly TextMesh[] plotLabelShadows = new TextMesh[9];

        private string layoutName = "材質比較：列と柱";
        private bool hudExpanded = true;
        private bool hudTargetMode;
        private float lastQDownTime = -10f;
#if UNITY_WEBGL && !UNITY_EDITOR
        private bool lastWebGlCaptureKeyboard = true;
#else
        private string chatInput = string.Empty;
        private bool focusChatRequested;
        private bool unfocusChatRequested;
        private bool placeCaretAtEndRequested;
        private char suppressActivationChar;
        private float suppressActivationUntil;
#endif
        private readonly List<string> chatLog = new List<string>();
        private readonly List<float> chatLineHeights = new List<float>();
        private readonly GUIContent reusableChatContent = new GUIContent();
        private readonly GUIContent reusableHudContent = new GUIContent();
        private float chatScrollOffsetPx;
        private readonly List<Rect> uiHitRects = new List<Rect>();
        private Rect chatPanelRect;
        private string activeStreamId = string.Empty;
        private bool hasOnlineSnapshot;
        private long lastAckedPoseSeq;
        private bool hasSentPoseSinceConnect;
        private Vector3 lastSentPos;
        private float lastSentMcYaw;
        private float lastSentMcPitch;
        private float nextPoseSendAt;
        private GameObject targetOutlineRoot;
        private readonly LineRenderer[] targetOutlineLines = new LineRenderer[6];
        private readonly Vector3[] outlineLoopPoints = new Vector3[4];
        private readonly Vector3[] outlineEdgePoints = new Vector3[2];
        private Vector3Int? currentTargetCell;

        internal DemoBlocks Blocks => blocks;
        internal Camera View => walker.View;
        internal Vector3Int DisplayOrigin => displayOrigin;
        internal bool IsTargetOutlineActive => targetOutlineRoot != null && targetOutlineRoot.activeSelf;
        internal Vector3Int? CurrentTargetCell => currentTargetCell;

        private void Awake()
        {
            Application.runInBackground = true;
        }

        private void Start()
        {
            Build();
            InitSyncClient();
        }

        internal void Build()
        {
            if (generated != null) return;
            Application.runInBackground = true;
            LoadBundledFont();
#if UNITY_WEBGL && !UNITY_EDITOR
            SandboxWsInstallGuards();
            currentLang = string.Equals(SandboxWsGetInitialLanguage(), "en", StringComparison.OrdinalIgnoreCase)
                ? DemoLanguage.En
                : DemoLanguage.Ja;
#endif

            palette = new DemoPalette();
            generated = new GameObject("Local demo (generated)");
            generated.transform.SetParent(transform, false);

            for (int i = 0; i < 9; i++)
            {
                var origin = DemoCoordinates.PlotOrigin(i);
                var color = i == 4 ? new Color(1, .8f, .2f) : i == 8 ? new Color(.2f, .8f, 1) : new Color(.85f, .9f, .75f);
                var p = (Vector3)origin + new Vector3(0.5f, 63.025f, 0.5f);
                Line("Origin " + i + " +X", color, .12f, new[] { p, p + Vector3.right * 48.5f });
                DottedLine("Origin " + i + " -X", color, .12f, p, p + Vector3.left * 48.5f, 48);
                Line("Origin " + i + " +Z", color, .12f, new[] { p, p + Vector3.forward * 48.5f });
                DottedLine("Origin " + i + " -Z", color, .12f, p, p + Vector3.back * 48.5f, 48);
                DottedLine("Origin " + i + " Y", color, .12f, p, new Vector3(p.x, 96.0f, p.z), 33);
                CreatePlotLabel(i, origin, color);
            }

            var sunObject = new GameObject("Demo sun");
            sunObject.transform.SetParent(generated.transform, false);
            sunObject.transform.position = new Vector3(100, 160, 100);
            sunObject.transform.rotation = Quaternion.Euler(48, -30, 0);
            var sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;

            blocks = new DemoBlocks(generated.transform, palette);
            Populate(false);

            var player = new GameObject("Feet");
            player.transform.SetParent(generated.transform, false);
            player.layer = 2; // Ignore Raycast: 自分の身体を照準に含めない。
            walker = player.AddComponent<LocalSandboxWalker>();
            walker.Initialize(displayOrigin);
            CreateTargetOutline();
            RefreshTargetOutline();

            string ver = string.IsNullOrEmpty(Application.version) ? "2321.2.0" : Application.version;
            AddChatLine(DemoText.WelcomeMessage(currentLang, ver));
        }

        private void InitSyncClient()
        {
            if (syncClient != null) return;
            syncClient = new SandboxSyncClient();
            syncClient.Connected += OnSyncConnected;
            syncClient.SnapshotReceived += OnSnapshotReceived;
            syncClient.BlockDeltaReceived += OnBlockDeltaReceived;
            syncClient.ChatReceived += OnChatReceived;
            syncClient.StreamUpdatedReceived += OnStreamUpdatedReceived;
            syncClient.PairResultReceived += OnPairResultReceived;
            syncClient.PlayerPoseReceived += OnPlayerPoseReceived;
            syncClient.PasteReceived += OnPasteReceived;
            syncClient.Start();
        }

        private void LoadBundledFont()
        {
            if (hudFont != null)
            {
                loadedFont = hudFont;
                return;
            }
            loadedFont = Resources.Load<Font>("Fonts/NotoSansJP-Subset");
            if (loadedFont == null)
            {
                loadedFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "Noto Sans Mono CJK JP", "Noto Sans CJK JP", "Yu Gothic", "Hiragino Sans", "Arial" },
                    18);
            }
        }

        private void SetLanguage(DemoLanguage lang)
        {
            if (currentLang == lang) return;
            currentLang = lang;
#if UNITY_WEBGL && !UNITY_EDITOR
            SandboxWsSaveLanguage(currentLang == DemoLanguage.En ? "en" : "ja");
#endif
            RefreshPlotLabels();
        }

        private void RefreshPlotLabels()
        {
            for (int i = 0; i < 9; i++)
            {
                string labelText = DemoText.PlotBillboardLabel(currentLang, i, DemoCoordinates.PlotOrigin(i));
                if (plotLabelMains[i] != null) plotLabelMains[i].text = labelText;
                if (plotLabelShadows[i] != null) plotLabelShadows[i].text = labelText;
            }
        }

        private void CreatePlotLabel(int index, Vector3Int origin, Color color)
        {
            string labelText = DemoText.PlotBillboardLabel(currentLang, index, origin);

            var labelGo = new GameObject($"PlotLabel_{index}");
            labelGo.transform.SetParent(generated.transform, false);
            labelGo.transform.position = new Vector3(origin.x + 0.5f, 100f, origin.z + 0.5f);
            var textMesh = labelGo.AddComponent<TextMesh>();
            textMesh.text = labelText;
            textMesh.fontSize = 72;
            textMesh.characterSize = 0.22f;
            textMesh.anchor = TextAnchor.MiddleCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.color = color;
            plotLabelMains[index] = textMesh;
            var mainRenderer = labelGo.GetComponent<MeshRenderer>();
            if (loadedFont != null)
            {
                textMesh.font = loadedFont;
                if (mainRenderer != null && loadedFont.material != null)
                {
                    mainRenderer.sharedMaterial = loadedFont.material;
                }
            }
            if (mainRenderer != null)
            {
                mainRenderer.sortingOrder = 1;
            }

            var shadowGo = new GameObject("Shadow");
            shadowGo.transform.SetParent(labelGo.transform, false);
            shadowGo.transform.localPosition = new Vector3(0.08f, -0.08f, 0f);
            var shadowMesh = shadowGo.AddComponent<TextMesh>();
            shadowMesh.text = labelText;
            shadowMesh.fontSize = 72;
            shadowMesh.characterSize = 0.22f;
            shadowMesh.anchor = TextAnchor.MiddleCenter;
            shadowMesh.alignment = TextAlignment.Center;
            shadowMesh.color = new Color(0f, 0f, 0f, 0.88f);
            plotLabelShadows[index] = shadowMesh;
            var shadowRenderer = shadowGo.GetComponent<MeshRenderer>();
            if (loadedFont != null)
            {
                shadowMesh.font = loadedFont;
                if (shadowRenderer != null && loadedFont.material != null)
                {
                    shadowRenderer.sharedMaterial = loadedFont.material;
                }
            }
            if (shadowRenderer != null)
            {
                shadowRenderer.sortingOrder = 0;
            }
        }

        internal void Populate(bool alternate)
        {
            PopulateMode(alternate ? 1 : 0);
        }

        internal void PopulateMode(int mode)
        {
            blocks.Clear();
            var origin = new Vector3Int(100, 63, 100); // 配置用。HUDの原点設定とは独立。

            if (mode == 2)
            {
                layoutName = "全10種ブロック見本（草/雪草/土/石/金/鉄/光/硝子/ダイヤ/TNT）";
                string[] demoIds =
                {
                    "minecraft:grass_block", "minecraft:grass_block", "minecraft:dirt", "minecraft:stone",
                    "minecraft:gold_block", "minecraft:iron_block", "minecraft:sea_lantern", "minecraft:glass",
                    "minecraft:diamond_block", "minecraft:tnt"
                };
                for (int i = 0; i < demoIds.Length; i++)
                {
                    bool snowy = i == 1;
                    bool unstable = demoIds[i] == "minecraft:tnt";
                    var start = origin + new Vector3Int(-28 + i * 6, 0, 0);
                    for (int y = 0; y < 4; y++)
                    {
                        blocks.ApplyBox(start + new Vector3Int(0, y, 0), start + new Vector3Int(1, y, 1),
                            demoIds[i], snowy: snowy, unstable: unstable, trackPlacement: false, countAsModified: true);
                    }
                }
                Physics.SyncTransforms();
                return;
            }

            bool alternate = mode == 1;
            layoutName = alternate ? "壁・階段" : "材質比較：列と柱";
            for (int m = 0; m < 6; m++)
            {
                var kind = (DemoMaterial)m;
                var start = origin + new Vector3Int(-36 + m * 12, 0, 0);
                if (!alternate)
                {
                    for (int x = 0; x < 10; x++) blocks.Put(start + new Vector3Int(x, 0, 0), kind);
                    for (int y = 0; y < columnHeight; y++) blocks.Put(start + new Vector3Int(4, y, 4), kind);
                }
                else
                {
                    for (int x = 0; x < 5; x++)
                    for (int y = 0; y < 3; y++) blocks.Put(start + new Vector3Int(x, y, 0), kind);
                    for (int step = 0; step < 4; step++)
                    for (int y = 0; y <= step; y++) blocks.Put(start + new Vector3Int(step, y, 5), kind);
                }
            }
            Physics.SyncTransforms();
        }

        private void OnSyncConnected()
        {
            hasSentPoseSinceConnect = false;
            nextPoseSendAt = 0f;
            lastAckedPoseSeq = 0;
        }

        private void OnPlayerPoseReceived(SyncPlayerPosePayload pose)
        {
            if (pose == null) return;
            lastAckedPoseSeq = pose.PoseSeq;
            if (walker == null) return;

            walker.ApplyRemotePose(pose.HasPos, pose.Pos, pose.HasRotation, pose.Yaw, pose.Pitch);
            Physics.SyncTransforms();
            SendCurrentPoseImmediate();
        }

        private void OnSnapshotReceived(SyncSnapshotPayload snap)
        {
            hasOnlineSnapshot = true;
            blocks.RebuildInitialTerrain();
            for (int i = 0; i < snap.Regions.Count; i++)
            {
                var r = snap.Regions[i];
                blocks.ApplyBox(r.Min, r.Max, r.BlockId, r.Snowy, r.Unstable, trackPlacement: false, countAsModified: true);
            }
            Physics.SyncTransforms();
            ApplyActiveStream(snap.ActiveStream);
            layoutName = $"同期ワールド (epoch {snap.WorldEpoch} / seq {snap.Seq})";
            AddChatLine(DemoText.SnapshotReceivedMessage(currentLang, snap.Regions.Count));
        }

        private void OnBlockDeltaReceived(SyncBlockDeltaPayload delta)
        {
            if (delta.Region == null) return;
            var r = delta.Region;
            blocks.ApplyBox(r.Min, r.Max, r.BlockId, r.Snowy, r.Unstable, trackPlacement: true, countAsModified: true);
            Physics.SyncTransforms();
            layoutName = $"同期ワールド (epoch {delta.WorldEpoch} / seq {delta.Seq})";
        }

        private void OnChatReceived(SyncChatPayload chat)
        {
            string tag = string.IsNullOrEmpty(chat.StreamId) ? "chat" : chat.StreamId;
            AddChatLine($"[{tag}] {chat.Message}");
        }

        private void OnStreamUpdatedReceived(SyncActiveStream stream)
        {
            bool changed = stream != null &&
                           (displayOrigin != stream.Origin ||
                            !string.Equals(activeStreamId, stream.StreamId ?? string.Empty, StringComparison.Ordinal));
            ApplyActiveStream(stream);
            if (changed)
            {
                AddChatLine(DemoText.OriginSyncedMessage(currentLang, stream.Origin));
            }
        }

        private void OnPasteReceived(string pastedText)
        {
            string cleaned = (pastedText ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
            if (string.IsNullOrEmpty(cleaned)) return;

#if UNITY_WEBGL && !UNITY_EDITOR
            string current = SandboxWsGetChatText() ?? string.Empty;
            string next = string.Equals(current, "/mcremote pair ", StringComparison.OrdinalIgnoreCase) &&
                          !cleaned.StartsWith("/", StringComparison.Ordinal)
                ? current + cleaned
                : cleaned;
            if (next.Length > 160) next = next.Substring(0, 160);
            SandboxWsSetChatText(next, 1);
            SandboxWsFocusChat(0, 1);
            if (walker != null) walker.IsTypingChat = true;
            WebGLInput.captureAllKeyboardInput = false;
            lastWebGlCaptureKeyboard = false;
#else
            if (string.Equals(chatInput, "/mcremote pair ", StringComparison.OrdinalIgnoreCase) &&
                !cleaned.StartsWith("/", StringComparison.Ordinal))
            {
                chatInput = chatInput + cleaned;
            }
            else
            {
                chatInput = cleaned;
            }

            if (chatInput.Length > 160) chatInput = chatInput.Substring(0, 160);
            if (walker != null) walker.IsTypingChat = true;
            focusChatRequested = true;
            placeCaretAtEndRequested = true;
#endif
        }

        private void OnPairResultReceived(SyncPairResultPayload res)
        {
            if (res.Approved)
            {
                ApplyActiveStream(res.ActiveStream);
                AddChatLine(DemoText.PairSucceededMessage(currentLang, res.PairCode));
            }
            else
            {
                AddChatLine(DemoText.PairFailedMessage(currentLang, res.Reason, res.RemainingAttempts));
            }
        }

        private void ApplyActiveStream(SyncActiveStream stream)
        {
            if (stream == null)
            {
                activeStreamId = string.Empty;
                return;
            }
            activeStreamId = stream.StreamId ?? string.Empty;
            displayOrigin = stream.Origin;
        }

        private void Line(string name, Color color, float width, Vector3[] points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(generated.transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = palette.Guide;
            line.startColor = line.endColor = color;
            line.startWidth = line.endWidth = width;
            line.positionCount = points.Length;
            line.SetPositions(points);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private void DottedLine(string name, Color color, float width, Vector3 start, Vector3 end, int segments)
        {
            var go = new GameObject(name);
            go.transform.SetParent(generated.transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = palette.GuideDotted;
            line.textureMode = LineTextureMode.RepeatPerSegment;
            line.startColor = line.endColor = color;
            line.startWidth = line.endWidth = width;
            int count = Mathf.Max(2, segments + 1);
            var points = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                points[i] = Vector3.Lerp(start, end, (float)i / (count - 1));
            }
            line.positionCount = count;
            line.SetPositions(points);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private void Update()
        {
            blocks?.UpdateHighlight(Time.unscaledDeltaTime);
            syncClient?.Tick(Time.unscaledTime);

#if UNITY_WEBGL && !UNITY_EDITOR
            string submitted;
            while (!string.IsNullOrEmpty(submitted = SandboxWsPopSubmittedChat()))
            {
                SendOrHandleChatText(submitted);
            }

            bool webChatFocused = SandboxWsIsChatFocused() != 0;
            bool wantCaptureAll = !webChatFocused;
            if (wantCaptureAll != lastWebGlCaptureKeyboard)
            {
                WebGLInput.captureAllKeyboardInput = wantCaptureAll;
                lastWebGlCaptureKeyboard = wantCaptureAll;
            }
            if (walker != null)
            {
                walker.IsTypingChat = webChatFocused;
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            if (walker != null)
            {
                if (Input.GetMouseButtonDown(0))
                {
                    Vector2 guiPos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                    bool overUi = IsPointOverUi(guiPos);
#if UNITY_WEBGL && !UNITY_EDITOR
                    if ((webChatFocused || walker.IsTypingChat) && !chatPanelRect.Contains(guiPos))
                    {
                        SandboxWsBlurChat();
                        walker.IsTypingChat = false;
                        if (lastWebGlCaptureKeyboard != true)
                        {
                            WebGLInput.captureAllKeyboardInput = true;
                            lastWebGlCaptureKeyboard = true;
                        }
                    }
#else
                    if (walker.IsTypingChat && !chatPanelRect.Contains(guiPos))
                    {
                        walker.IsTypingChat = false;
                        unfocusChatRequested = true;
                    }
#endif
                    if (!overUi)
                    {
                        walker.LockCursor();
                    }
                }

                if (!walker.IsTypingChat)
                {
#if !(UNITY_WEBGL && !UNITY_EDITOR)
                    bool slashDown = Input.GetKeyDown(KeyCode.Slash) ||
                                     Input.GetKeyDown(KeyCode.KeypadDivide) ||
                                     Input.inputString.IndexOf('/') >= 0;
                    if (slashDown)
                    {
                        chatInput = string.IsNullOrEmpty(chatInput) ? "/" : chatInput;
                        suppressActivationChar = '/';
                        suppressActivationUntil = Time.unscaledTime + 0.08f;
                        walker.IsTypingChat = true;
                        focusChatRequested = true;
                        placeCaretAtEndRequested = true;
                    }
                    else if (Input.GetKeyDown(KeyCode.T) || Input.GetKeyDown(KeyCode.Return))
                    {
                        if (Input.GetKeyDown(KeyCode.T))
                        {
                            suppressActivationChar = 't';
                            suppressActivationUntil = Time.unscaledTime + 0.08f;
                        }
                        walker.IsTypingChat = true;
                        focusChatRequested = true;
                        placeCaretAtEndRequested = true;
                    }
                    else
                    {
#endif
                        if (Input.GetKeyDown(KeyCode.Q))
                        {
                            if (Time.unscaledTime - lastQDownTime <= DoubleTapThreshold)
                            {
                                hudTargetMode = !hudTargetMode;
                                lastQDownTime = -10f;
                            }
                            else
                            {
                                lastQDownTime = Time.unscaledTime;
                            }
                        }

                        if (Input.GetKeyDown(KeyCode.Alpha1) && !hasOnlineSnapshot) PopulateMode(0);
                        if (Input.GetKeyDown(KeyCode.Alpha2) && !hasOnlineSnapshot) PopulateMode(1);
                        if (Input.GetKeyDown(KeyCode.Alpha3) && !hasOnlineSnapshot) PopulateMode(2);
#if !(UNITY_WEBGL && !UNITY_EDITOR)
                    }
#endif
                }
            }
#endif
        }

        private void CreateTargetOutline()
        {
            targetOutlineRoot = new GameObject("Target block outline");
            targetOutlineRoot.transform.SetParent(generated.transform, false);
            targetOutlineRoot.layer = 2; // Ignore Raycast
            var outlineColor = new Color(0.02f, 0.03f, 0.05f, 0.80f);

            for (int i = 0; i < targetOutlineLines.Length; i++)
            {
                var lineGo = new GameObject("Edge_" + i);
                lineGo.transform.SetParent(targetOutlineRoot.transform, false);
                lineGo.layer = 2;
                var lr = lineGo.AddComponent<LineRenderer>();
                lr.sharedMaterial = palette.Guide;
                lr.useWorldSpace = true;
                lr.loop = i < 2;
                lr.positionCount = i < 2 ? 4 : 2;
                lr.startColor = lr.endColor = outlineColor;
                lr.startWidth = lr.endWidth = 0.01f;
                lr.numCornerVertices = 2;
                lr.numCapVertices = 2;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                targetOutlineLines[i] = lr;
            }

            targetOutlineRoot.SetActive(false);
        }

        internal void RefreshTargetOutline()
        {
            if (walker == null || View == null || targetOutlineRoot == null) return;

            var physicsScene = gameObject.scene.GetPhysicsScene();
            var ray = View.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!physicsScene.Raycast(ray.origin, ray.direction, out var hit, MaxTargetDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                currentTargetCell = null;
                if (targetOutlineRoot.activeSelf) targetOutlineRoot.SetActive(false);
                return;
            }

            Vector3Int cell = DemoCoordinates.HitCell(hit);
            currentTargetCell = cell;

            float dist = Vector3.Distance(View.transform.position, DemoCoordinates.Center(cell));
            float width = Mathf.Clamp(dist * 0.0024f, 0.003f, 0.042f);
            float pad = Mathf.Max(0.004f, width * 0.55f);

            bool isGrassBlock = hit.collider != null &&
                string.Equals(hit.collider.gameObject.name, "minecraft:grass_block", StringComparison.Ordinal);
            bool restsOnGrass = !isGrassBlock &&
                physicsScene.Raycast(DemoCoordinates.Center(cell), Vector3.down, out var belowHit, 0.6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                belowHit.collider != null &&
                string.Equals(belowHit.collider.gameObject.name, "minecraft:grass_block", StringComparison.Ordinal);

            float minX = cell.x - pad;
            float maxX = cell.x + 1f + pad;
            float minZ = cell.z - pad;
            float maxZ = cell.z + 1f + pad;
            float minY = restsOnGrass ? (cell.y + 0.01f + pad * 0.5f) : (cell.y - pad);
            float maxY = isGrassBlock ? (cell.y + 1.01f + pad) : (cell.y + 1f + pad);

            for (int i = 0; i < targetOutlineLines.Length; i++)
            {
                targetOutlineLines[i].startWidth = targetOutlineLines[i].endWidth = width;
            }

            outlineLoopPoints[0] = new Vector3(minX, maxY, minZ);
            outlineLoopPoints[1] = new Vector3(maxX, maxY, minZ);
            outlineLoopPoints[2] = new Vector3(maxX, maxY, maxZ);
            outlineLoopPoints[3] = new Vector3(minX, maxY, maxZ);
            targetOutlineLines[0].SetPositions(outlineLoopPoints);

            outlineLoopPoints[0] = new Vector3(minX, minY, minZ);
            outlineLoopPoints[1] = new Vector3(maxX, minY, minZ);
            outlineLoopPoints[2] = new Vector3(maxX, minY, maxZ);
            outlineLoopPoints[3] = new Vector3(minX, minY, maxZ);
            targetOutlineLines[1].SetPositions(outlineLoopPoints);

            SetOutlineVerticalEdge(2, minX, minY, maxY, minZ);
            SetOutlineVerticalEdge(3, maxX, minY, maxY, minZ);
            SetOutlineVerticalEdge(4, maxX, minY, maxY, maxZ);
            SetOutlineVerticalEdge(5, minX, minY, maxY, maxZ);

            if (!targetOutlineRoot.activeSelf) targetOutlineRoot.SetActive(true);
        }

        private void SetOutlineVerticalEdge(int lineIndex, float x, float minY, float maxY, float z)
        {
            outlineEdgePoints[0] = new Vector3(x, minY, z);
            outlineEdgePoints[1] = new Vector3(x, maxY, z);
            targetOutlineLines[lineIndex].SetPositions(outlineEdgePoints);
        }

        private void LateUpdate()
        {
            if (walker == null) return;
            RefreshTargetOutline();
            if (syncClient == null || !syncClient.IsConnected) return;
            if (Time.unscaledTime < nextPoseSendAt) return;

            walker.GetMinecraftPose(out Vector3 pos, out float mcYaw, out float mcPitch);
            if (!hasSentPoseSinceConnect ||
                (pos - lastSentPos).sqrMagnitude > 0.000001f ||
                Mathf.Abs(Mathf.DeltaAngle(mcYaw, lastSentMcYaw)) > 0.01f ||
                Mathf.Abs(mcPitch - lastSentMcPitch) > 0.01f)
            {
                SendPose(pos, mcYaw, mcPitch);
            }
        }

        private void SendCurrentPoseImmediate()
        {
            if (walker == null || syncClient == null || !syncClient.IsConnected) return;
            walker.GetMinecraftPose(out Vector3 pos, out float mcYaw, out float mcPitch);
            SendPose(pos, mcYaw, mcPitch);
        }

        private void SendPose(Vector3 pos, float mcYaw, float mcPitch)
        {
            if (syncClient != null && syncClient.SendPlayerPose(pos, mcYaw, mcPitch, lastAckedPoseSeq))
            {
                lastSentPos = pos;
                lastSentMcYaw = mcYaw;
                lastSentMcPitch = mcPitch;
                hasSentPoseSinceConnect = true;
                nextPoseSendAt = Time.unscaledTime + 0.05f;
            }
        }

        private bool IsPointOverUi(Vector2 guiPoint)
        {
            for (int i = 0; i < uiHitRects.Count; i++)
            {
                if (uiHitRects[i].Contains(guiPoint)) return true;
            }
            return false;
        }

        private void AddChatLine(string line)
        {
            chatLog.Add(line);
            while (chatLog.Count > MaxChatLogLines) chatLog.RemoveAt(0);
            chatScrollOffsetPx = 0f;
        }

        private void SendOrHandleChatText(string rawText)
        {
            string text = (rawText ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (syncClient != null && syncClient.IsConnected)
            {
                syncClient.SendChat(text);
            }
            else
            {
                AddChatLine(DemoText.LocalChatPrefix(currentLang) + text);
                if (text.StartsWith("/mcremote pair", StringComparison.OrdinalIgnoreCase))
                {
                    AddChatLine(DemoText.PairOfflineHint(currentLang));
                }
            }
        }

#if !(UNITY_WEBGL && !UNITY_EDITOR)
        private void SubmitChatOrPair()
        {
            string raw = chatInput;
            chatInput = string.Empty;
            walker.IsTypingChat = true;
            focusChatRequested = true;
            placeCaretAtEndRequested = true;
            suppressActivationChar = '\0';
            SendOrHandleChatText(raw);
        }
#endif

        private void EnsureStyles()
        {
            bool compact = Screen.width <= 840;
            if (textStyle != null && compact == lastCompactStyle) return;
            lastCompactStyle = compact;
            if (loadedFont == null) LoadBundledFont();
            int fontSize = compact ? 14 : 16;

            textStyle = new GUIStyle(GUI.skin.label) { font = loadedFont, fontSize = fontSize, wordWrap = true, richText = true };
            textStyle.normal.textColor = Color.white;

            coordTextStyle = new GUIStyle(GUI.skin.label)
            {
                font = loadedFont,
                fontSize = compact ? 16 : 20,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
                richText = true,
                margin = new RectOffset(2, 2, 1, 1),
                padding = new RectOffset(1, 1, 1, 1)
            };
            coordTextStyle.normal.textColor = Color.white;

            coordShadowStyle = new GUIStyle(coordTextStyle) { richText = false };
            coordShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.72f);

            smallTextStyle = new GUIStyle(GUI.skin.label)
            {
                font = loadedFont,
                fontSize = compact ? 11 : 12,
                wordWrap = true,
                richText = true,
                margin = new RectOffset(2, 2, 1, 1),
                padding = new RectOffset(1, 1, 0, 0)
            };
            smallTextStyle.normal.textColor = new Color(.92f, .96f, 1f);

            chatTitleStyle = new GUIStyle(GUI.skin.label)
            {
                font = loadedFont,
                fontSize = compact ? 11 : 12,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
                richText = false,
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(1, 1, 0, 0)
            };
            chatTitleStyle.normal.textColor = new Color(.96f, .98f, 1f);

            chatLogStyle = new GUIStyle(GUI.skin.label)
            {
                font = loadedFont,
                fontSize = compact ? 11 : 12,
                wordWrap = true,
                richText = false,
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(1, 1, 1, 1)
            };
            chatLogStyle.normal.textColor = new Color(.92f, .96f, 1f);

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                font = loadedFont,
                fontSize = compact ? 11 : 12,
                margin = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(4, 4, 2, 2)
            };
            inputStyle = new GUIStyle(GUI.skin.textField) { font = loadedFont, fontSize = compact ? 12 : 13 };

            if (darkBoxStyle == null)
            {
                var bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(.08f, .1f, .14f, .55f));
                bg.Apply();
                darkBoxStyle = new GUIStyle(GUI.skin.box);
                darkBoxStyle.normal.background = bg;
            }
        }

        private void BuildMcRemoteHudLines(out string richLine, out string plainLine)
        {
            string absHex;
            string relHex;
            bool relativeOriginAvailable = !hasOnlineSnapshot || !string.IsNullOrEmpty(activeStreamId);
            Vector3Int? absCell = null;

            if (!hudTargetMode)
            {
                absCell = DemoCoordinates.Cell(walker.transform.position);
                absHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(40f / 360f, 0.40f, 0.90f));
                relHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(320f / 360f, 0.40f, 0.90f));
            }
            else
            {
                absHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(40f / 360f, 0.90f, 0.95f));
                relHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(320f / 360f, 0.48f, 1.00f));
                absCell = currentTargetCell;
            }

            FormatHudCoordinates(absCell, displayOrigin, relativeOriginAvailable, out string absText, out string relText);

            Vector3 fwd = walker.transform.forward;
            float compassDeg = (Mathf.Atan2(fwd.x, -fwd.z) * Mathf.Rad2Deg + 360f) % 360f;
            int dirIdx = Mathf.FloorToInt((compassDeg + 22.5f) / 45f) % 8;
            string dirLabel = DemoText.CompassDir(currentLang, dirIdx);
            string dirHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(compassDeg / 360f, 0.40f, 0.90f));
            string markHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(compassDeg / 360f, 0.75f, 1.00f));
            string modeHex = hudTargetMode ? "FFD166" : "B8E1FF";
            string targetLabel = DemoText.CoordTargetName(currentLang, hudTargetMode);

            plainLine = $"[{targetLabel}] {absText} / {relText} / #{dirLabel}#";
            richLine = $"<color=#{modeHex}>[{targetLabel}]</color> " +
                       $"<color=#{absHex}>{absText}</color> <color=#B0B8C0>/</color> " +
                       $"<color=#{relHex}>{relText}</color> <color=#B0B8C0>/</color> " +
                       $"<color=#{markHex}>#</color><color=#{dirHex}>{dirLabel}</color><color=#{markHex}>#</color>";
        }

        internal static void FormatHudCoordinates(
            Vector3Int? absoluteCell,
            Vector3Int origin,
            bool relativeOriginAvailable,
            out string absText,
            out string relText)
        {
            absText = absoluteCell.HasValue
                ? $"{absoluteCell.Value.x} {absoluteCell.Value.y} {absoluteCell.Value.z}"
                : "[-]";
            if (!relativeOriginAvailable)
            {
                relText = "[ - - - ]";
            }
            else if (!absoluteCell.HasValue)
            {
                relText = "[-]";
            }
            else
            {
                var rel = DemoCoordinates.Relative(absoluteCell.Value, origin);
                relText = $"{rel.x} {rel.y} {rel.z}";
            }
        }

        private void OnGUI()
        {
            if (walker == null) return;
            EnsureStyles();
            uiHitRects.Clear();

#if !(UNITY_WEBGL && !UNITY_EDITOR)
            if (unfocusChatRequested)
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
                walker.IsTypingChat = false;
                unfocusChatRequested = false;
                suppressActivationChar = '\0';
            }

            // チャット入力フォーカス中ならEnterで送信、Escapeでキャンセル、起動キー重複を抑制
            if (walker.IsTypingChat && Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                {
                    SubmitChatOrPair();
                    Event.current.Use();
                    return;
                }
                if (Event.current.keyCode == KeyCode.Escape)
                {
                    walker.IsTypingChat = false;
                    GUI.FocusControl(null);
                    GUIUtility.keyboardControl = 0;
                    suppressActivationChar = '\0';
                    Event.current.Use();
                    return;
                }
                if (suppressActivationChar != '\0')
                {
                    if (Time.unscaledTime > suppressActivationUntil)
                    {
                        suppressActivationChar = '\0';
                    }
                    else if (Event.current.character != '\0')
                    {
                        char low = char.ToLowerInvariant(Event.current.character);
                        char expected = suppressActivationChar;
                        suppressActivationChar = '\0';
                        if (low == expected)
                        {
                            Event.current.Use();
                            return;
                        }
                    }
                }
            }
#endif

            bool narrow = Screen.width <= 840;
            float margin = narrow ? 6f : 8f;

            // 左上：常時表示の大きめ太字等幅座標行＋（展開時のみ）詳細ステータス・操作ボタン
            float topWidth = Mathf.Min(narrow ? 660f : 760f, Screen.width - margin * 2f);
            float topHeight = hudExpanded ? (narrow ? 66f : 74f) : (narrow ? 38f : 44f);
            var topRect = new Rect(margin, margin, topWidth, topHeight);

            // 左右下パネルはウィンドウ幅に関わらず高さを統一する
            float rightWidth = narrow ? 304f : 356f;
            float bottomPanelHeight = hasOnlineSnapshot ? (narrow ? 182f : 202f) : (narrow ? 206f : 228f);
            var rightRect = new Rect(Screen.width - rightWidth - margin, Screen.height - bottomPanelHeight - margin, rightWidth, bottomPanelHeight);

            float chatWidth = narrow
                ? Mathf.Max(220f, Screen.width - rightWidth - margin * 3f)
                : Mathf.Min(540f, Screen.width - rightWidth - margin * 3f);
            float chatHeight = bottomPanelHeight;
            var chatRect = new Rect(margin, Screen.height - chatHeight - margin, chatWidth, chatHeight);
            chatPanelRect = chatRect;

            uiHitRects.Add(topRect);
            uiHitRects.Add(rightRect);
            uiHitRects.Add(chatRect);

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (!chatRect.Contains(Event.current.mousePosition))
                {
                    if (SandboxWsIsChatFocused() != 0 || walker.IsTypingChat)
                    {
                        SandboxWsBlurChat();
                        walker.IsTypingChat = false;
                        if (lastWebGlCaptureKeyboard != true)
                        {
                            WebGLInput.captureAllKeyboardInput = true;
                            lastWebGlCaptureKeyboard = true;
                        }
                    }
                }
                else if (Event.current.mousePosition.y < chatRect.yMax - 5f - (narrow ? 22f : 24f))
                {
                    walker.IsTypingChat = true;
                    if (lastWebGlCaptureKeyboard != false)
                    {
                        WebGLInput.captureAllKeyboardInput = false;
                        lastWebGlCaptureKeyboard = false;
                    }
                    SandboxWsFocusChat(0, 1);
                }
#else
                if (!chatRect.Contains(Event.current.mousePosition))
                {
                    if (walker.IsTypingChat)
                    {
                        GUI.FocusControl(null);
                        GUIUtility.keyboardControl = 0;
                        walker.IsTypingChat = false;
                        suppressActivationChar = '\0';
                    }
                }
                else
                {
                    bool alreadyFocused = GUI.GetNameOfFocusedControl() == ChatControlName;
                    walker.IsTypingChat = true;
                    if (!alreadyFocused || Event.current.mousePosition.y < chatRect.yMax - (narrow ? 28f : 30f))
                    {
                        focusChatRequested = true;
                        if (!alreadyFocused) placeCaretAtEndRequested = true;
                    }
                }
#endif
            }

            if (Event.current.type == EventType.ScrollWheel && chatRect.Contains(Event.current.mousePosition))
            {
                chatScrollOffsetPx = Mathf.Max(0f, chatScrollOffsetPx - Event.current.delta.y * 18f);
                Event.current.Use();
            }

            // 左上パネル描画
            GUI.Box(topRect, GUIContent.none, darkBoxStyle);
            GUILayout.BeginArea(new Rect(topRect.x + 6f, topRect.y + 4f, topRect.width - 12f, topRect.height - 8f));

            GUILayout.BeginHorizontal();
            BuildMcRemoteHudLines(out string hudRich, out string hudPlain);
            reusableHudContent.text = hudPlain;
            Rect coordRect = GUILayoutUtility.GetRect(reusableHudContent, coordShadowStyle);
            GUI.Label(
                new Rect(coordRect.x + CoordShadowOffsetPx, coordRect.y + CoordShadowOffsetPx, coordRect.width + 4f, coordRect.height),
                hudPlain,
                coordShadowStyle);
            GUI.Label(
                new Rect(coordRect.x, coordRect.y, coordRect.width + 4f, coordRect.height),
                hudRich,
                coordTextStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(DemoText.TargetToggleButton(currentLang, hudTargetMode), buttonStyle, GUILayout.Width(narrow ? 72f : 82f)))
            {
                hudTargetMode = !hudTargetMode;
            }
            if (GUILayout.Button(DemoText.SpeedButton(currentLang, walker.IsFastSpeed), buttonStyle, GUILayout.Width(narrow ? 54f : 62f)))
            {
                walker.IsFastSpeed = !walker.IsFastSpeed;
            }
            if (GUILayout.Button(DemoText.ExpandButton(currentLang, hudExpanded), buttonStyle, GUILayout.Width(narrow ? 48f : 56f)))
            {
                hudExpanded = !hudExpanded;
            }
            GUILayout.EndHorizontal();

            if (hudExpanded)
            {
                GUILayout.Space(2f);
                GUILayout.BeginHorizontal();
                SyncStatusKind statusKind = syncClient != null ? syncClient.StatusKind : SyncStatusKind.LocalOnly;
                GUILayout.Label(DemoText.TopDetailLine(currentLang, statusKind, activeStreamId, blocks.Count, walker.IsFlying), smallTextStyle);
                GUILayout.FlexibleSpace();
                if (syncClient != null && syncClient.IsConnected && GUILayout.Button(DemoText.ResyncButton(currentLang), buttonStyle, GUILayout.Width(narrow ? 52f : 60f)))
                {
                    syncClient.RequestResync();
                }
                if (GUILayout.Button(DemoText.FullscreenButton(currentLang), buttonStyle, GUILayout.Width(narrow ? 48f : 56f)))
                {
#if UNITY_WEBGL && !UNITY_EDITOR
                    SandboxWsToggleFullscreen();
#endif
                }
                if (GUILayout.Button(DemoText.LanguageToggleButton(currentLang), buttonStyle, GUILayout.Width(narrow ? 30f : 34f)))
                {
                    SetLanguage(currentLang == DemoLanguage.Ja ? DemoLanguage.En : DemoLanguage.Ja);
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.EndArea();

            // 右下：区画3×3ワープ＋直前のブロックへ／配置発光切替＋キー操作案内
            GUI.Box(rightRect, GUIContent.none, darkBoxStyle);
            GUILayout.BeginArea(new Rect(rightRect.x + 6f, rightRect.y + 4f, rightRect.width - 12f, rightRect.height - 8f));

            for (int row = 2; row >= 0; row--)
            {
                GUILayout.BeginHorizontal();
                for (int col = 0; col < 3; col++)
                {
                    int plotIdx = row * 3 + col;
                    if (GUILayout.Button(DemoText.PlotButtonLabel(currentLang, plotIdx), buttonStyle))
                    {
                        walker.WarpToPlot(plotIdx);
                    }
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(DemoText.LastBlockButton(currentLang), buttonStyle, GUILayout.ExpandWidth(true)))
            {
                if (blocks.LastPlacedCell.HasValue) walker.WarpNearCell(blocks.LastPlacedCell.Value);
                else walker.WarpToPlot(8);
            }
            if (GUILayout.Button(DemoText.HighlightToggleButton(currentLang, blocks.HighlightEnabled), buttonStyle, GUILayout.Width(narrow ? 74f : 86f)))
            {
                blocks.HighlightEnabled = !blocks.HighlightEnabled;
            }
            GUILayout.EndHorizontal();

            if (!hasOnlineSnapshot)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(DemoText.PresetButton(currentLang, 0), buttonStyle)) PopulateMode(0);
                if (GUILayout.Button(DemoText.PresetButton(currentLang, 1), buttonStyle)) PopulateMode(1);
                if (GUILayout.Button(DemoText.PresetButton(currentLang, 2), buttonStyle)) PopulateMode(2);
                if (GUILayout.Button(DemoText.PresetButton(currentLang, 3), buttonStyle)) walker.ResetPose();
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(2f);
            GUILayout.Label(DemoText.KeybindLine1(currentLang), smallTextStyle);
            GUILayout.Label(DemoText.KeybindLine2(currentLang), smallTextStyle);
            GUILayout.Label(DemoText.KeybindLine3(currentLang), smallTextStyle);
            GUILayout.Label(DemoText.KeybindLine4(currentLang), smallTextStyle);

            GUILayout.EndArea();

            // 左下 チャット & ペアリング入力パネル（タイトル／スクロール可能ログ領域／固定入力行を完全分離）
            GUI.Box(chatRect, GUIContent.none, darkBoxStyle);

            float innerX = chatRect.x + 6f;
            float innerW = Mathf.Max(40f, chatRect.width - 12f);
            float headerH = narrow ? 18f : 20f;
            float inputRowH = narrow ? 22f : 24f;

            var headerRect = new Rect(innerX, chatRect.y + 5f, innerW, headerH);
            var inputRowRect = new Rect(innerX, chatRect.yMax - 5f - inputRowH, innerW, inputRowH);
            float logTop = headerRect.yMax + 3f;
            float logBottom = inputRowRect.y - 4f;
            var logViewportRect = new Rect(innerX, logTop, innerW, Mathf.Max(20f, logBottom - logTop));

            GUI.Label(headerRect, DemoText.ChatTitle(currentLang), chatTitleStyle);

            chatLineHeights.Clear();
            float contentWidth = Mathf.Max(40f, logViewportRect.width - 4f);
            float totalLogHeight = 0f;
            for (int i = 0; i < chatLog.Count; i++)
            {
                reusableChatContent.text = chatLog[i];
                float lineH = Mathf.Max(chatLogStyle.lineHeight, chatLogStyle.CalcHeight(reusableChatContent, contentWidth));
                chatLineHeights.Add(lineH);
                totalLogHeight += lineH;
            }
            float maxScroll = Mathf.Max(0f, totalLogHeight - logViewportRect.height);
            chatScrollOffsetPx = Mathf.Clamp(chatScrollOffsetPx, 0f, maxScroll);

            GUI.BeginGroup(logViewportRect);
            float drawY = totalLogHeight <= logViewportRect.height
                ? 0f
                : logViewportRect.height - totalLogHeight + chatScrollOffsetPx;
            for (int i = 0; i < chatLog.Count; i++)
            {
                float lineH = chatLineHeights[i];
                if (drawY + lineH > 0f && drawY < logViewportRect.height)
                {
                    GUI.Label(new Rect(0f, drawY, contentWidth, lineH), chatLog[i], chatLogStyle);
                }
                drawY += lineH;
            }
            GUI.EndGroup();

#if UNITY_WEBGL && !UNITY_EDITOR
            GUILayout.BeginArea(inputRowRect);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(DemoText.PairAssistButton(currentLang), buttonStyle, GUILayout.Width(narrow ? 62f : 76f)))
            {
                string cur = SandboxWsGetChatText() ?? string.Empty;
                if (!cur.StartsWith("/mcremote pair", StringComparison.OrdinalIgnoreCase))
                {
                    SandboxWsSetChatText("/mcremote pair ", 1);
                }
                walker.IsTypingChat = true;
                if (lastWebGlCaptureKeyboard != false)
                {
                    WebGLInput.captureAllKeyboardInput = false;
                    lastWebGlCaptureKeyboard = false;
                }
                SandboxWsFocusChat(0, 1);
            }

            Rect localFieldRect = GUILayoutUtility.GetRect(
                GUIContent.none,
                inputStyle,
                GUILayout.Height(inputRowH),
                GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && Screen.width > 0 && Screen.height > 0)
            {
                float sx = inputRowRect.x + localFieldRect.x;
                float sy = inputRowRect.y + 1f;
                float sw = Mathf.Max(40f, localFieldRect.width);
                float sh = Mathf.Max(16f, inputRowRect.height - 2f);
                SandboxWsSyncChatInputLayout(
                    sx / Screen.width,
                    sy / Screen.height,
                    sw / Screen.width,
                    sh / Screen.height,
                    DemoText.ChatPlaceholder(currentLang),
                    1);
            }

            if (GUILayout.Button(DemoText.SendButton(currentLang), buttonStyle, GUILayout.Width(narrow ? 46f : 56f)))
            {
                string raw = SandboxWsGetChatText();
                SandboxWsSetChatText(string.Empty, 1);
                SendOrHandleChatText(raw);
                walker.IsTypingChat = true;
                if (lastWebGlCaptureKeyboard != false)
                {
                    WebGLInput.captureAllKeyboardInput = false;
                    lastWebGlCaptureKeyboard = false;
                }
                SandboxWsFocusChat(0, 1);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
#else
            GUILayout.BeginArea(inputRowRect);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(DemoText.PairAssistButton(currentLang), buttonStyle, GUILayout.Width(narrow ? 62f : 76f)))
            {
                if (!chatInput.StartsWith("/mcremote pair", StringComparison.OrdinalIgnoreCase))
                {
                    chatInput = "/mcremote pair ";
                }
                walker.IsTypingChat = true;
                focusChatRequested = true;
                placeCaretAtEndRequested = true;
            }

            GUI.SetNextControlName(ChatControlName);
            chatInput = GUILayout.TextField(chatInput ?? string.Empty, 160, inputStyle, GUILayout.Height(inputRowH));
            if (focusChatRequested)
            {
                GUI.FocusControl(ChatControlName);
                if (Event.current.type == EventType.Repaint && GUI.GetNameOfFocusedControl() == ChatControlName)
                {
                    focusChatRequested = false;
                }
            }
            if (GUI.GetNameOfFocusedControl() == ChatControlName)
            {
                walker.IsTypingChat = true;
                if (placeCaretAtEndRequested)
                {
                    if (GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) is TextEditor editor)
                    {
                        int len = (chatInput ?? string.Empty).Length;
                        editor.cursorIndex = len;
                        editor.selectIndex = len;
                    }
                    if (Event.current.type == EventType.Repaint)
                    {
                        placeCaretAtEndRequested = false;
                    }
                }
            }

            if (GUILayout.Button(DemoText.SendButton(currentLang), buttonStyle, GUILayout.Width(narrow ? 46f : 56f)))
            {
                SubmitChatOrPair();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
#endif

            // 画面中央レティクル（黒・太め・大きめ）
            float cx = Mathf.Round(Screen.width * 0.5f);
            float cy = Mathf.Round(Screen.height * 0.5f);
            Color prevGuiColor = GUI.color;
            GUI.color = new Color(0.05f, 0.05f, 0.05f, 0.92f);
            GUI.DrawTexture(new Rect(cx - 11f, cy - 1.5f, 22f, 3f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1.5f, cy - 11f, 3f, 22f), Texture2D.whiteTexture);
            GUI.color = prevGuiColor;
        }

        private void OnDestroy()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            WebGLInput.captureAllKeyboardInput = true;
            lastWebGlCaptureKeyboard = true;
            SandboxWsSyncChatInputLayout(0f, 0f, 0f, 0f, string.Empty, 0);
#endif
            syncClient?.Dispose();
            blocks?.Dispose();
            DemoPalette.Release(generated);
            palette?.Dispose();
        }
    }

    internal sealed class DemoOwnedMaterial : MonoBehaviour
    {
        internal Material Value;
        private void OnDestroy() => DemoPalette.Release(Value);
    }
}
