# Unity箱庭：表示同期とローカルWebビルド設計

> 参照ナレッジコミット: `3a265e76249484c06abe4b785d6ce5f12ce3141b` (`2026-11-14-01` .. `2026-11-20-01` 反映)\
> 確認Unity版: `6000.6.8f1 (237b6593792b)` (Built-in Render Pipeline, WebGL)

## 1. 構成と責務の分離

1. **`server/src/Code2Create.UnitySandbox.World` (`netstandard2.1`)**
   - `UnityEngine` に依存しない純粋 C# の世界モデル（`SandboxWorld`, `InitialTerrain`, `SandboxBlockRegistry`, `SandboxDimensionResolver`, `SandboxWireNumbers`, `SandboxDirectionUnit`, `SandboxEventRing`, `WorldBox`, `SandboxProtocolError`, `SandboxJsonRpcDispatcher`）。
   - Minecraft `1.21.11` 準拠の範囲（`y: -64..319`, `x/z: -200..199`, `y_sea = 62`, 足元 `y = 63`）、初期地形（各区画 `[-48..48]` の `y=62` 草・区画間 `y=62` 空気、`y=61..60` 土、`y=59..-64` 石＋各区画原点下の `y % 10 == 0` イースターエッグ）、基本10ブロック（passable は `minecraft:air` のみ）と状態解決・カタログ生成（`catalog.get`）、次元解決（`minecraft:overworld` のみ）、`events.poll`（接続 epoch ごとの有限 event ring で `chat_posted` を保持）、`build.setDimension`/`build.setOrigin`、`connection.flush`、世界外エラー `out_of_bounds`（`code: -32000`, `data.bounds`（`y_min`, `y_max`, `world_border`）, `data.violating` `{x, y, z}`）、`world.getBlocks` 軸・件数上限 `work_limit_exceeded`（各軸 `<= 10`・総数 `<= 1,000`）、および `world.setBlocks` 体積上限 `block_count_limit_exceeded`（`code: -32000`, `data.limit: 32768`, `data.requested`）を提供する。
2. **`server/src/Code2Create.UnitySandbox.Server` (`net9.0`)**
   - Unity Web の静的配信（`--web-root`）、`/ws/sync` の表示同期 WebSocket サーバー（`DisplaySyncHub`, `ViewerBindingAndPairing`）、および改行区切り JSON-RPC 2.0 の TCP リスナー（`TcpJsonRpcServer`, `SandboxRpcSession`、CLI 既定 `--tcp-port 25585`）を統合する。
3. **`unity/` (`6000.6.8f1`)**
   - WebGL／ローカル両対応の描画・歩行・操作クライアント（`LocalSandboxDemo`, `DemoCoordinates`, `DemoBlocks`, `DemoPalette`, `DemoLocalization`, `LocalSandboxWalker`, `SandboxSyncClient`, `SandboxWebSocket.jslib`）。`DemoCoordinates` が Minecraft 右手系（`+X` 東・`+Y` 上・`+Z` 南 / `-Z` 北）を Unity 左手系へ Z 反転（`u = (mc.x, mc.y, -mc.z)`）し、初期カメラは区画8南側から北向き（`yaw = -180`）で開始する（`2026-11-20-01`）。wire 値・カタログ・20 RPC メソッドは McRemote と同一契約を維持する。

## 2. TCP JSON-RPC 2.0 受付契約（順番 4、`2026-11-14-01` 〜 `2026-11-14-05` 準拠）

Bridge 経由の Scratch および一般の McRemote クライアント向けに、McRemote と同じ改行区切り JSON-RPC 2.0 を TCP で受け付ける。

- **起動オプション**：`--url`（既定 `http://127.0.0.1:5080`）、`--web-root`（WebGL ビルド出力先）、`--tcp-host`（既定 `127.0.0.1`）、`--tcp-port`（既定 `25585`）、`--server-version`（省略時は `VERSION` 等から自動解決）。`/healthz` は `status`, `version`, `protocol`, `worldEpoch`, `seq`, `connections`, `tcpPort` を返す。
- **pre-hello ゲート**：`hello` 完了前は `hello` と `auth.*`（`auth.pairBegin`, `auth.pairPoll`）のみ許可し、その他のメソッドには `expected_hello`（`code: -32000`）を返して TCP 接続を閉じる。
- **ペアリングと `viewerId` 紐付け**：
  - `auth.pairBegin` は `pairing_id` と 6 桁 ASCII の `pair_code`（有効期間 `120` 秒）を発行する。
  - Unity Web 側が `/ws/sync` 上で `/mcremote pair NNN-NNN` を承認すると、その画面の `viewerId` に紐づく `streamId` と別 UUID（`player`）に対し Bearer token（`mcrs_...` / `mcrl_...`）が発行される。
  - `auth.pairPoll` は `pairing_id` で相関するため TCP 接続を跨いでも動作し（Bridge の ~10 秒切断・別 TCP 接続に対応）、`ok` を返した後に当該 `pairing_id` を削除する（以降の再ポーリングは `pair_not_found`）。
  - トークン・`viewerId` ⇔ `streamId` 紐付け・ワールド状態はすべてインメモリのみに保持する（`2026-11-15-06`）。世界リセット（`world.Reset()`）は地形を初期化して `worldEpoch` を進めるが token と紐付けは維持し、**サーバープロセス再起動時はすべて破棄**されて再起動前のトークンは `token_not_found`（`-32000`）で拒否される。
- **`hello` 応答**：`protocol` は `WorldConstants.ProtocolVersion`（LTS `2321.2.0` 系列は `23.3.0`、`2400` 系列は `24.0.0`。major 一致・minor はサーバー互換範囲内、不一致時は `protocol_mismatch`）、`mc_version: "1.21.11"`、`supported_mc_versions: ["1.21.11"]`、`server: { "name": "mc-remote-unity-sandbox", "version": "<VERSION単一ソース>" }`（`2026-11-14-03`・`2026-11-15-04` 準拠の object 形）、`dimension: "minecraft:overworld"`、既定 `origin: [0, 0, 0]`、`world_constants: { "y_sea": 62 }`、`permissions: { "online": true, "offline": true, "buildRange": 1000 }`、`player`（`viewerId` とは別の UUID）、`catalogHash` を返す。
- **対応メソッド**：`hello`, `auth.pairBegin`, `auth.pairPoll`, `catalog.get`, `events.poll`（ペアリング済み画面の通常チャットを `chat_posted` として配送。`/` 開始コマンド・`chat.post`・他画面チャットは除外）, `chat.post`, `world.setBlock`, `world.getBlock`, `world.setBlocks`（FAST notification 含む／`32,768` 超過時は `block_count_limit_exceeded`）, `world.getBlocks`（`x/y/z` 昇順・軸長 `<= 10`・最大 `1,000` セル超過で世界境界前に `work_limit_exceeded`）, `world.getHeight`（任意 `max_y` 対応・`minecraft:air` のみ passable・未検出時は `height_not_found`・水平範囲外は `out_of_bounds`）, `player.getPos`, `player.setPos`, `player.getPose`, `player.setPose`, `player.getDirection`, `player.setDirection`（ペアリング済み Unity Web 画面の一人称歩行者 `viewerId` を対象とし、接続中の `/ws/sync` が無ければ `player_offline`、水平 `[-200, 200)` または高さ `[-64, 320]` の範囲外への設定は副作用前に `out_of_bounds`、ゼロベクトルは `zero_direction`）, `build.setDimension`, `build.setOrigin`, `connection.flush`。未対応メソッドには `method_not_found`（`-32601`）を返す。

## 3. WebSocket 表示同期プロトコル（`/ws/sync`）

1メッセージ＝1 JSON テキストフレームで送受信する。`wireScopeId` は持たず、`viewerId` は外部ブロードキャストへ含めない。

| 向き | `type` | 主なフィールド | 説明 |
| --- | --- | --- | --- |
| S → C | `snapshot` | `worldEpoch`, `seq`, `worldConstants`, `regions[]`, `activeStream` | 初回接続・再接続・`resync`・世界リセット時に送信。結合された差分箱（`bounds` + `block`）のみを送り、初期地形全体は展開しない。 |
| S → C | `blockDelta` | `worldEpoch`, `seq`, `streamId`, `bounds` (`min`/`max`), `block` | `setBlock`（1×1×1）および `setBlocks`（範囲箱のまま展開しない）を順番通り通知。 |
| S → C | `chat` | `worldEpoch`, `seq`, `streamId`, `message` | `chat.post` や通常ブラウザチャット等の通知。 |
| S → C | `streamUpdated` | `activeStream` (`streamId`, `dimension`, `origin`) | ペアリング確定後、または原点変更時に該当 `viewerId` の画面へ送信。 |
| S → C | `pairResult` | `approved`, `pairCode`, `reason`, `remainingAttempts`, `activeStream` | ペアリングコード検証結果（成功／期限切れ／一致せず／レート制限）。 |
| S → C | `playerPose` | `poseSeq`, `hasPos`, `pos`, `hasRotation`, `yaw`, `pitch` | `player.setPos`/`setPose`/`setDirection` および再接続・`resync` 時の位置・視線同期を該当 `viewerId` の画面のみへ送信（`viewerId` 非露出）。 |
| C → S | `resync` | `viewerId` | 現在時点の `snapshot` 再送を要求。 |
| C → S | `approvePair` | `viewerId`, `pairCode` | `NNN-NNN` コード承認を要求。 |
| C → S | `chat` | `viewerId`, `message` | `/mcremote pair NNN-NNN` は `approvePair` と同じ検証・レート制限を通す。通常テキストは画面ブロードキャストと該当 `viewerId` に紐づくアクティブセッションへの `chat_posted` 捕捉を行う。 |
| C → S | `playerPose` | `viewerId`, `poseSeq`, `pos` (`[x,y,z]`), `yaw`, `pitch` | Unity Web の一人称歩行者位置・視線をサーバーへ報告。サーバー発行 `poseSeq` より古い報告は無視してテレポート巻き戻りを防ぐ。 |

## 4. 設計上の3つの論点に対する方針（`unity-first-slice-followup_ja.md` 対応）

### (1) `snapshot` の扱いと配置上限を設けない理由

- **質問**：`snapshot` が大きくなりすぎないための上限をどうするか？ 配置回数の上限は設けるか？
- **決定と実装**：
  - ワールド全体の配置回数や変更セル数に人工的な上限は設けず、**「置ける操作を上限で弾かない」**方針を採る（単発 `setBlocks` の1回体積上限は保護のため `32,768` セル＝`block_count_limit_exceeded`）。
  - 代わりに、`SandboxWorld` は `16×16×16` チャンク内の変更セルを `InitialTerrain` との差分として保持し、`GetSnapshot()` で同一ブロック種の連続箱領域（`WorldBoxChange`）へ 3 次元貪欲結合して送信する。`setBlocks` で大きな箱を置いた場合も、最小の箱数に圧縮して1メッセージ（またはチャンク分割された小数箱）で送る。
  - Unity 側（`DemoBlocks`）も同様に箱のまま受け取り、重なった既存領域だけを軸平行の最大6分割で差し引くことで、`32,768` セルの箱を1 GameObject で描画できる。

### (2) ブラウザの画面識別子（`viewerId`）と `hello.player` の UUID

- **決定と実装（`2026-11-14-01` 反映）**：
  - ブラウザ側の `SandboxWebSocket.jslib` は `window.crypto.getRandomValues` で 128bit 乱数（`v_` + 32 hex 文字）を生成し、`localStorage`（キー：`mc_remote_unity_viewer_id_v1`）に永続化する。
  - `viewerId` は `/ws/sync?viewerId=...` およびクライアント発メッセージにのみ乗せ、**サーバーから他者へブロードキャストする `blockDelta` / `chat` や JSON-RPC 応答へは一切含めない**。
  - `InMemoryViewerStreamBindingStore.GetOrCreatePlayerUuid(viewerId)` は、各 `viewerId` に対して完全に別の UUID v4 を発行・保持し、後続チケット（順番4）の `hello.player` に使う。
  - 世界のリセット（`world.Reset()`）は `worldEpoch` を進め地形を戻すが、**`viewerId` ⇔ `streamId` の結び付きおよび token は消さない**。

### (3) ペアリング試行回数のレート制限

- **決定と実装**：
  - 6桁コード（`NNN-NNN`）の総当たりを防ぐため、`PairingRateLimiter` により **接続（WebSocket）および `viewerId` ごとに 60 秒間で最大 5 回までの失敗** に制限し、超過時は `pairResult` で `reason: "rate_limited"` を返す。成功した承認は失敗回数へ加算しない。

## 5. 同梱日本語フォントの出どころと記録

- **収録ファイル**: `unity/Assets/LocalSandboxDemo/Resources/Fonts/NotoSansJP-Subset.ttf`
- **サイズ**: `11,128,896` bytes
- **ライセンス**: `SIL Open Font License 1.1`（全文を `unity/Assets/LocalSandboxDemo/Resources/Fonts/LICENSE_NotoSansCJK.txt` に同梱、`14,712` bytes）
- **生成元**: Linux パッケージ `fonts-noto-cjk` の `/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc` (`font-number=5`、`Noto Sans Mono CJK JP`) から、`pyftsubset` により ASCII・ひらがな・カタカナ・約物・罫線・記号・常用／JIS第一水準等の日本語範囲（`U+0020-007E, U+00A0-00FF, U+2010-203B, U+2190-2199, U+2500-25FF, U+3000-303F, U+3040-309F, U+30A0-30FF, U+31F0-31FF, U+4E00-9FFF, U+FF00-FFEF`）を抽出して等幅表示対応・軽量化。

## 6. バージョン単一ソース（`VERSION`）とコンテナイメージ

- **バージョン管理（`2026-11-15-04`, `2026-11-17-01`, `2026-11-19-01`, `2026-11-19-02`）**: リポジトリ直下の `VERSION`（LTS 系列 `2321.2.0`／最新版 `2400.0.0rc1` 等）を単一ソースとし、サーバーの `hello.server.version` / `/healthz`、Unity WebGL ビルド時の `PlayerSettings.bundleVersion`（`DemoEditor.BuildWebGL`）、コンテナの OCI ラベル（`org.opencontainers.image.version`）へ伝搬する。
- **コンテナ構成（`2026-11-15-05`, `2026-11-17-02`, `2026-11-17-03`）**: `Dockerfile` は `.NET` サーバーの `Release` publish 出力、WebGL 静的成果物（`/app/wwwroot`）、`/app/LICENSE`、および `THIRD-PARTY-NOTICES.txt`（`/app/THIRD-PARTY-NOTICES.txt` と配信ルート `/app/wwwroot/THIRD-PARTY-NOTICES.txt`）を単一ランタイムコンテナへ同梱し、`USER $APP_UID`（UID `1654`、非 root ユーザー）で実行する。OCI ラベル `org.opencontainers.image.licenses` は `2026-11-17-03` に基づき `"MIT"` とし、第三者の権利表示・SIL OFL 1.1 全文は `THIRD-PARTY-NOTICES.txt` へ収録する。
  - **公開ポート**: `8080/tcp`（HTTP WebGL + `/ws/sync` + `/healthz`）、`25585/tcp`（TCP JSON-RPC 2.0）。
  - **ボリューム**: 不要（状態・トークン・紐付けはすべてインメモリ保持、`2026-11-15-06`）。
  - **候補タグ運用（`2026-11-17-02`）**: `scripts/build-container.sh` は `VERSION` を確定した GitHub コミットから `ghcr.io/naohiro2g/mc-remote-unity-sandbox:candidate-<7桁>` としてビルドし、凍結前公開タグでの GHCR 照合後に同一ダイジェストへ `v2321.2.0` を付与する（通常のローカル `build-container.sh` 自体は自動 `push` を行わない）。

## 7. 外部 TCP 開放に伴うサーバー資源制限（`2026-11-20-04`, McRemote `2026-10-07-02` 準拠）

公開 VPS で Unity 箱庭の TCP ポートを外へ開けるにあたり、McRemote 方針（認証前の資源制限・1行サイズ超過は新しいエラー reason を増やさず接続を閉じるのみ）と接続生存確認方針（`2026-11-13-02`）に揃え、`SandboxConnectionLimits` により以下を既定適用する。

- **TCP 同時接続数**: 全体最大 `64`、hello 完了前最大 `32`（`hello` 成功で hello 済み枠へ昇格）、送り元 IP 別最大 `32`（IPv4 射影 IPv6 は IPv4 へ正規化）。超過接続は即座に TCP を閉じる。
- **hello 前の待機時間と hello 後の生存確認**: hello 前の無通信上限 `30` 秒（1行完了まで）・接続からの合計期限 `180` 秒（超過時は pending pairing を消さず TCP のみ切断し別接続からの継続を許容）。hello 後は無通信切断を行わず、TCP keepalive（idle `60` 秒・interval `30` 秒・retry `8` 回、無応答約 `5` 分回収）を有効化する。
- **`auth.pairBegin` と承認待ち `pair_code`**: `PairBeginRateLimiter` により送り元 IP 別 `20` 回/`60` 秒・全体 `20` 回/`1` 秒かつ `120` 回/`60` 秒（超過時は応答なしで TCP 切断）。未承認アクティブ `pair_code` 上限は `128` 件、期限切れ（`120` 秒）後 `+60` 秒猶予（この間は `auth.pairPoll` へ `pair_expired` を維持）を過ぎた未 poll レコードは `byId` から自動削除し、ハード総数上限 `256` 件到達時は期限切れの古い順から退去（すべて有効期間内なら拒否切断）とする。
- **フレームサイズ上限**: TCP 1 行最大 `65,536` bytes（`64 KiB`、`\n` 待機中に超過時点で読み込みを打ち切り即 TCP 切断。hello 前後共通）。Unity Web `/ws/sync` の受信 1 メッセージ最大 `65,536` bytes（`64 KiB`、超過時は `1009 MessageTooBig` で切断）。
