using System;
using System.Collections.Generic;
using UnityEngine;

namespace Code2Create.UnitySandbox.LocalDemo
{
    [DisallowMultipleComponent]
    public sealed class LocalSandboxDemo : MonoBehaviour
    {
        private const string ChatControlName = "SandboxChatInput";

        [SerializeField] private Vector3Int displayOrigin = new Vector3Int(100, 0, 100);
        [SerializeField, Range(1, 20)] private int columnHeight = 10;
        [SerializeField] private Font hudFont;

        private DemoPalette palette;
        private DemoBlocks blocks;
        private GameObject generated;
        private LocalSandboxWalker walker;
        private SandboxSyncClient syncClient;

        private Font loadedFont;
        private GUIStyle textStyle;
        private GUIStyle smallTextStyle;
        private GUIStyle buttonStyle;
        private GUIStyle inputStyle;
        private GUIStyle darkBoxStyle;
        private bool lastCompactStyle;

        private string layoutName = "材質比較：列と柱";
        private bool hudExpanded = true;
        private bool hudTargetMode;
        private float lastQDownTime = -10f;
        private const float DoubleTapThreshold = 0.35f;
        private string chatInput = string.Empty;
        private bool focusChatRequested;
        private bool unfocusChatRequested;
        private readonly List<string> chatLog = new List<string>();
        private readonly List<Rect> uiHitRects = new List<Rect>();
        private Rect chatPanelRect;
        private string activeStreamId = string.Empty;
        private bool hasOnlineSnapshot;

        internal DemoBlocks Blocks => blocks;
        internal Camera View => walker.View;
        internal Vector3Int DisplayOrigin => displayOrigin;

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

            AddChatLine("ようこそ Unity箱庭！ 左下のチャットから /mcremote pair NNN-NNN を入力できます。");
        }

        private void InitSyncClient()
        {
            if (syncClient != null) return;
            syncClient = new SandboxSyncClient();
            syncClient.SnapshotReceived += OnSnapshotReceived;
            syncClient.BlockDeltaReceived += OnBlockDeltaReceived;
            syncClient.ChatReceived += OnChatReceived;
            syncClient.StreamUpdatedReceived += OnStreamUpdatedReceived;
            syncClient.PairResultReceived += OnPairResultReceived;
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
                loadedFont = Font.CreateDynamicFontFromOSFont(new[] { "Noto Sans CJK JP", "Yu Gothic", "Hiragino Sans", "Arial" }, 18);
            }
        }

        private void CreatePlotLabel(int index, Vector3Int origin, Color color)
        {
            string labelText = $"区画{index} ({origin.x}, {origin.y}, {origin.z})";

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
            AddChatLine($"[同期] スナップショットを受信しました (地域 {snap.Regions.Count}件)");
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
                AddChatLine($"[ペアリング] 原点を {stream.Origin} へ同期しました");
            }
        }

        private void OnPasteReceived(string pastedText)
        {
            string cleaned = (pastedText ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
            if (string.IsNullOrEmpty(cleaned)) return;

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
        }

        private void OnPairResultReceived(SyncPairResultPayload res)
        {
            if (res.Approved)
            {
                ApplyActiveStream(res.ActiveStream);
                AddChatLine($"[ペアリング成功] コード {res.PairCode} を承認しました！");
            }
            else
            {
                AddChatLine($"[ペアリング失敗] {FormatPairReason(res.Reason)} (残り試行: {res.RemainingAttempts})");
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

        private static string FormatPairReason(string reason)
        {
            switch (reason)
            {
                case "invalid_pair_code_format": return "NNN-NNN の6桁形式で入力してください";
                case "invalid_pair_code": return "コードが見つかりません";
                case "pair_code_expired": return "コードの有効期限（120秒）が切れました";
                case "rate_limited": return "試行回数の上限に達しました（少し待ってください）";
                default: return string.IsNullOrEmpty(reason) ? "失敗しました" : reason;
            }
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

#if ENABLE_LEGACY_INPUT_MANAGER
            if (walker != null)
            {
                if (Input.GetMouseButtonDown(0))
                {
                    Vector2 guiPos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                    bool overUi = IsPointOverUi(guiPos);
                    if (walker.IsTypingChat && !chatPanelRect.Contains(guiPos))
                    {
                        walker.IsTypingChat = false;
                        unfocusChatRequested = true;
                    }
                    if (!overUi)
                    {
                        walker.LockCursor();
                    }
                }

                if (!walker.IsTypingChat)
                {
                    if (Input.GetKeyDown(KeyCode.T) || Input.GetKeyDown(KeyCode.Return))
                    {
                        walker.IsTypingChat = true;
                        focusChatRequested = true;
                    }
                    else
                    {
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
                    }
                }
            }
#endif
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
            while (chatLog.Count > 30) chatLog.RemoveAt(0);
        }

        private void SubmitChatOrPair()
        {
            string text = (chatInput ?? string.Empty).Trim();
            chatInput = string.Empty;
            walker.IsTypingChat = false;
            GUI.FocusControl(null);
            GUIUtility.keyboardControl = 0;

            if (string.IsNullOrEmpty(text)) return;

            if (syncClient != null && syncClient.IsConnected)
            {
                syncClient.SendChat(text);
            }
            else
            {
                AddChatLine("[ローカル] " + text);
                if (text.StartsWith("/mcremote pair", StringComparison.OrdinalIgnoreCase))
                {
                    AddChatLine("[ヒント] 同期サーバーに接続するとペアリング承認が有効になります。");
                }
            }
        }

        private void EnsureStyles()
        {
            bool compact = Screen.width <= 840;
            if (textStyle != null && compact == lastCompactStyle) return;
            lastCompactStyle = compact;
            if (loadedFont == null) LoadBundledFont();
            int fontSize = compact ? 14 : 16;

            textStyle = new GUIStyle(GUI.skin.label) { font = loadedFont, fontSize = fontSize, wordWrap = true, richText = true };
            textStyle.normal.textColor = Color.white;

            smallTextStyle = new GUIStyle(GUI.skin.label)
            {
                font = loadedFont,
                fontSize = compact ? 11 : 13,
                wordWrap = true,
                richText = true,
                margin = new RectOffset(2, 2, 1, 1),
                padding = new RectOffset(1, 1, 0, 0)
            };
            smallTextStyle.normal.textColor = new Color(.92f, .96f, 1f);

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                font = loadedFont,
                fontSize = compact ? 11 : 13,
                margin = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(4, 4, 2, 2)
            };
            inputStyle = new GUIStyle(GUI.skin.textField) { font = loadedFont, fontSize = compact ? 12 : 14 };

            if (darkBoxStyle == null)
            {
                var bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(.08f, .1f, .14f, .42f));
                bg.Apply();
                darkBoxStyle = new GUIStyle(GUI.skin.box);
                darkBoxStyle.normal.background = bg;
            }
        }

        private string BuildMcRemoteHudLine()
        {
            string absText;
            string relText;
            string absHex;
            string relHex;

            if (!hudTargetMode)
            {
                var legs = DemoCoordinates.Cell(walker.transform.position);
                var rel = DemoCoordinates.Relative(legs, displayOrigin);
                absText = $"{legs.x} {legs.y} {legs.z}";
                relText = $"{rel.x} {rel.y} {rel.z}";
                absHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(40f / 360f, 0.40f, 0.90f));
                relHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(320f / 360f, 0.40f, 0.90f));
            }
            else
            {
                absHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(40f / 360f, 0.90f, 0.95f));
                relHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(320f / 360f, 0.75f, 0.96f));
                var ray = View.ViewportPointToRay(new Vector3(.5f, .5f, 0f));
                if (Physics.Raycast(ray, out var hit, 120f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    var cell = DemoCoordinates.HitCell(hit);
                    var rel = DemoCoordinates.Relative(cell, displayOrigin);
                    absText = $"{cell.x} {cell.y} {cell.z}";
                    relText = $"{rel.x} {rel.y} {rel.z}";
                }
                else
                {
                    absText = "[-]";
                    relText = "[-]";
                }
            }

            Vector3 fwd = walker.transform.forward;
            float compassDeg = (Mathf.Atan2(fwd.x, -fwd.z) * Mathf.Rad2Deg + 360f) % 360f;
            string[] dirNames = { "北", "北東", "東", "南東", "南", "南西", "西", "北西" };
            string dirLabel = dirNames[Mathf.FloorToInt((compassDeg + 22.5f) / 45f) % 8];
            string dirHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(compassDeg / 360f, 0.40f, 0.90f));
            string markHex = ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(compassDeg / 360f, 0.75f, 1.00f));
            string modeHex = hudTargetMode ? "FFD166" : "B8E1FF";

            return $"<color=#{modeHex}>[{(hudTargetMode ? "照準" : "脚")}]</color> " +
                   $"<color=#{absHex}>{absText}</color> <color=#B0B8C0>/</color> " +
                   $"<color=#{relHex}>{relText}</color> <color=#B0B8C0>/</color> " +
                   $"<color=#{markHex}><b>#</b></color><color=#{dirHex}>{dirLabel}</color><color=#{markHex}><b>#</b></color>";
        }

        private void OnGUI()
        {
            if (walker == null) return;
            EnsureStyles();
            uiHitRects.Clear();

            if (unfocusChatRequested)
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
                walker.IsTypingChat = false;
                unfocusChatRequested = false;
            }

            // チャット入力フォーカス中ならEnterで送信、Escapeでキャンセル
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
                    Event.current.Use();
                    return;
                }
            }

            bool narrow = Screen.width <= 840;
            float margin = narrow ? 6f : 8f;

            // 左上：ステータス＋McRemote風HUD（絶対／建築原点相対／#方位#）
            float topWidth = Mathf.Min(narrow ? 480f : 540f, Screen.width - margin * 2f);
            float topHeight = hudExpanded ? (narrow ? 62f : 70f) : (narrow ? 32f : 38f);
            var topRect = new Rect(margin, margin, topWidth, topHeight);
            uiHitRects.Add(topRect);

            GUI.Box(topRect, GUIContent.none, darkBoxStyle);
            GUILayout.BeginArea(new Rect(topRect.x + 6f, topRect.y + 4f, topRect.width - 12f, topRect.height - 8f));

            GUILayout.BeginHorizontal();
            string statusText = syncClient != null ? syncClient.StatusLabel : "ローカル体験中";
            string pairTag = string.IsNullOrEmpty(activeStreamId) ? "未ペアリング" : $"接続中 ({activeStreamId})";
            string moveMode = walker.IsFlying ? "フライト" : "歩行";
            GUILayout.Label($"{statusText} | {pairTag} | {blocks.Count}個 | {moveMode}", smallTextStyle);
            GUILayout.FlexibleSpace();
            if (syncClient != null && syncClient.IsConnected && GUILayout.Button("再同期", buttonStyle, GUILayout.Width(narrow ? 52f : 60f)))
            {
                syncClient.RequestResync();
            }
            if (GUILayout.Button(hudExpanded ? "畳む" : "開く", buttonStyle, GUILayout.Width(narrow ? 42f : 48f)))
            {
                hudExpanded = !hudExpanded;
            }
            GUILayout.EndHorizontal();

            if (hudExpanded)
            {
                GUILayout.Space(2f);
                GUILayout.BeginHorizontal();
                GUILayout.Label(BuildMcRemoteHudLine(), textStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(hudTargetMode ? "照準(Q×2)" : "脚(Q×2)", buttonStyle, GUILayout.Width(narrow ? 72f : 84f)))
                {
                    hudTargetMode = !hudTargetMode;
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.EndArea();

            // 右下（新設）：区画3×3ワープ＋直前のブロックへ＋キー操作案内
            float rightWidth = narrow ? 264f : 330f;
            float rightHeight = hasOnlineSnapshot ? (narrow ? 164f : 194f) : (narrow ? 190f : 220f);
            var rightRect = new Rect(Screen.width - rightWidth - margin, Screen.height - rightHeight - margin, rightWidth, rightHeight);
            uiHitRects.Add(rightRect);

            GUI.Box(rightRect, GUIContent.none, darkBoxStyle);
            GUILayout.BeginArea(new Rect(rightRect.x + 6f, rightRect.y + 4f, rightRect.width - 12f, rightRect.height - 8f));

            for (int row = 2; row >= 0; row--)
            {
                GUILayout.BeginHorizontal();
                for (int col = 0; col < 3; col++)
                {
                    int plotIdx = row * 3 + col;
                    if (GUILayout.Button($"区画{plotIdx}", buttonStyle))
                    {
                        walker.WarpToPlot(plotIdx);
                    }
                }
                GUILayout.EndHorizontal();
            }

            if (GUILayout.Button("直前のブロックへ", buttonStyle))
            {
                if (blocks.LastPlacedCell.HasValue) walker.WarpNearCell(blocks.LastPlacedCell.Value);
                else walker.WarpToPlot(8);
            }

            if (!hasOnlineSnapshot)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("1:列と柱", buttonStyle)) PopulateMode(0);
                if (GUILayout.Button("2:壁・階段", buttonStyle)) PopulateMode(1);
                if (GUILayout.Button("3:全10種", buttonStyle)) PopulateMode(2);
                if (GUILayout.Button("原点(R)", buttonStyle)) walker.ResetPose();
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(2f);
            GUILayout.Label("視点:クリック(Esc解放) 移動:WASD 会話:T", smallTextStyle);
            GUILayout.Label("飛行:Space×2 (Space昇/Shift降/×2解除)", smallTextStyle);
            GUILayout.Label("HUD(脚↔照準):Q×2 / 初期位置:R", smallTextStyle);

            GUILayout.EndArea();

            // 左下 チャット & ペアリング入力パネル
            float chatWidth = narrow
                ? Mathf.Max(220f, Screen.width - rightWidth - margin * 3f)
                : Mathf.Min(500f, Screen.width - rightWidth - margin * 3f);
            float chatHeight = narrow ? 164f : 200f;
            var chatRect = new Rect(margin, Screen.height - chatHeight - margin, chatWidth, chatHeight);
            chatPanelRect = chatRect;
            uiHitRects.Add(chatRect);

            if (walker.IsTypingChat &&
                Event.current.type == EventType.MouseDown &&
                !chatRect.Contains(Event.current.mousePosition))
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
                walker.IsTypingChat = false;
            }

            GUI.Box(chatRect, GUIContent.none, darkBoxStyle);
            GUILayout.BeginArea(new Rect(chatRect.x + 6f, chatRect.y + 4f, chatRect.width - 12f, chatRect.height - 8f));
            GUILayout.Label("チャット / ペアリング (/mcremote pair NNN-NNN)", smallTextStyle);

            int maxVisible = narrow ? 3 : 4;
            int startIdx = Math.Max(0, chatLog.Count - maxVisible);
            for (int i = startIdx; i < chatLog.Count; i++)
            {
                GUILayout.Label(chatLog[i], smallTextStyle);
            }
            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("入力補助", buttonStyle, GUILayout.Width(narrow ? 58f : 74f)))
            {
                if (!chatInput.StartsWith("/mcremote pair", StringComparison.OrdinalIgnoreCase))
                {
                    chatInput = "/mcremote pair ";
                }
                walker.IsTypingChat = true;
                focusChatRequested = true;
            }

            GUI.SetNextControlName(ChatControlName);
            chatInput = GUILayout.TextField(chatInput ?? string.Empty, 160, inputStyle);
            if (focusChatRequested)
            {
                GUI.FocusControl(ChatControlName);
                focusChatRequested = false;
            }
            if (GUI.GetNameOfFocusedControl() == ChatControlName)
            {
                walker.IsTypingChat = true;
            }

            if (GUILayout.Button("送信", buttonStyle, GUILayout.Width(narrow ? 44f : 54f)))
            {
                SubmitChatOrPair();
            }
            GUILayout.EndHorizontal();

            GUILayout.EndArea();

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
