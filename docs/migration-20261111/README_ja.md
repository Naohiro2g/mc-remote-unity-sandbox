# C# repoからのUnity移行（2026-11-11）

## 入力と履歴

- 元repo: `Naohiro2g/minecraft-remote-csharp`、`main`の`534e422e4894d43d377b71c77068d2c391e0ca05`。
- 参照knowledge: `a409c9695e1a117d88abc36084b9c582b54d7c50`、`00-hub/unity-sandbox-migration-instructions_ja.md`、`19-unity/README_ja.md`、`18-csharp-client/README_ja.md`、決定`2026-11-11-02`。
- 新repoのbootstrap: `2c1ed9febdee9d2a3d225ae6e3ed503ec1e23270`。初期commitとAGENTS.mdを含む履歴を保持した。
- `git-filter-repo`（公式source commit `d7b75aca907380f608892cc289e616f195427b99`）を新しい作業用cloneで実行し、対象pathだけを抽出・改名した。元repoの履歴は書き換えていない。
- 抽出した5コミットを`--allow-unrelated-histories`でbootstrapの履歴へ接続した。取り込みmergeは`0e6ab40`。対象32ファイルは、このcommitで元のbytesと一致する。

[commit-map.tsv](commit-map.tsv)は旧→新commitの対応。新SHAが0の行は対象pathを変更しないため除去されたcommitを表す。
[history-blobs.json](history-blobs.json)は対象履歴のユニークな40 blob（元のpath、Git blob ID、bytes、SHA-256）。40件すべてが抽出後の履歴から到達可能であることを照合した。

[files.json](files.json)は32ファイルの移行元→移行先と、元・取り込み時・文書等の修正後のbytes／SHA-256。`content_updated`の行ではpath変更に必要な参照・説明・準備手順を修正している。Runtime／EditorのC#コードと既存画像は変更していない。

## 移行前の全blob検査

元のmainとその全祖先について、対象pathの`git rev-list --objects`と`git cat-file`で40 blobを列挙・読み出した。テキスト38件、PNG 2件で、音声・外部texture・modelのfileはない。

- 全テキストを対象に、operator-localのpath、秘密鍵、token形式、credential代入、private IP、利用者のhome path、埋め込みbinaryを検査し、該当なし。
- URLと素材・認証に関する記述、素材生成コードを確認。URLは公開文書への参照で、token等は構想・説明中の語だった。
- PNG 2件を目視。自作デモの材質比較画像とhuman owner提供の試用画像。`DemoPalette`は色と境界線textureをコードで生成しており、Minecraftから抽出した素材はない。

この範囲で、移行を止めるべきMojang素材、token、private endpoint、operator-local fileは見つからなかった。

## 配置と開発手順

Unity Projectは`unity/`へ置いた。後で加える.NET hostや世界モデル、文書、補助scriptと並べて管理し、Unity固有の生成fileを隔離しやすくするため。

human ownerが用意したUnity用ignore規則は`unity/.gitignore`へ移し、元デモの`.local/`規則も保持した。rootの`.gitignore`はoperator-local・環境設定・共通の生成物を扱う。Unity用の`*.csproj`／`*.sln`除外はUnity Project内に限定し、将来の.NET hostのprojectを隠さない。

UnityのYAMLが出力する空値の末尾空白は保持し、`.gitattributes`で`.asset`・`.unity`・`.meta`のその書式を扱う。

準備scriptは`unity/`を直接開いて検証する。ソースの上書きコピーや、利用者のシーンの再生成を行わない。Assetsと.meta、Packagesのmanifest／lock、ProjectSettings、再現用デモシーンを管理する。

## 参照の修正

- デモの手順を`docs/local-demo_ja.md`、観察を`docs/observations_ja.md`、画像を`docs/images/`、準備scriptを`scripts/prepare-project.sh`へ移した。
- 設計メモからデモへのリンク、デモから先行作業文書へのリンク、projectの場所、実行例、「このrepo」が指す作業範囲を修正した。
- 図は`docs/diagrams/`を保ったため、設計メモからの相対参照を保持した。
- 移した文書内の既存knowledge参照URLは、指示に従い変更していない。過去の判断・参照commitを表すリンクとして残す。
- coordinatorには、`19-unity/README_ja.md`の旧C# repoの`680ebb3`への設計文書参照と、デモの正本の所在を、merge後の本repoのpath／commitへ更新してもらう。`18-csharp-client/README_ja.md`はUnityからlibraryを利用する関係と新スポークへの入口を保持する。

## 検証

Unity 6.6修正版での準備script、座標・配置検証、Play Mode、描画の結果は、[観察メモ](../observations_ja.md)の2026-11-11の節へ記録した。6000.6.8f1で準備script・座標/配置・Play Mode・描画を確認し、各Editorプロセスの終了0と成功markerを確認した。Web build、URP、実機性能、通信・共有世界の同期は今回の検証範囲に含めない。
