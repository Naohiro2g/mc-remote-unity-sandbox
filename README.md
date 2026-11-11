# Minecraft Remote Unity Sandbox

> [!NOTE]
> 公開文書は日本語を正本とします。母国語でのIssueやPull Requestも歓迎します。詳しくは[言語方針](https://github.com/Naohiro2g/mc-remote-knowledge/blob/main/LANGUAGE_POLICY.md)をご覧ください。

Unityで、コードによる建築の結果を見て、歩き、確かめられる箱庭を育てています。
今は通信を使わないローカルデモで、6種類の材質、柱・壁・階段、足と照準の座標表示を試せます。

## 試す

開発用のUnity Editor **6000.6.8f1**を用意してください。現在はBuilt-in Render Pipeline（BRP、従来の描画方式）とInput Managerを使います。

1. このrepoをcloneします。
2. Linux／macOSでは、次の準備スクリプトへUnity Editorのパスを渡します。
3. Unity Hubに`unity/`を追加し、`LocalSandboxDemo`シーンを開いてPlayを押します。

```sh
bash scripts/prepare-project.sh /path/to/Unity/Editor/Unity
```

WindowsなどでのEditorからの準備、操作方法、検証コマンドは[ローカルデモの手順](docs/local-demo_ja.md)にあります。
WASD／矢印で歩き、クリックで見回し、Escでマウスを解放、1／2で場面を切り替えます。

## 目指すものと現在地

人の創造性を引き出すことをミッションに、MinecraftやScratchと結びつく探究の入口を目指します。
将来はPython・Scratch・C#から建築し、複数人がブラウザで同じ箱庭を見られるUnity Webへ進める構想です。
通信・認証・共有状態の同期、Web build、URPとの比較は後続です。

一般用途の[C# Client Library](https://github.com/Naohiro2g/minecraft-remote-csharp)は別repoです。
このデモはその実装や通信機能に依存しません。Unityは6.6の修正版で試作し、6.7 LTSを検証して基準版にする方針です。

## 開発資料

- [観察メモ](docs/observations_ja.md)：試用条件・結果・残る確認。
- [Unity内部の先行作業](docs/unity-internal-preparation_ja.md)：通信を待たずに進める範囲。
- [共有箱庭の設計メモ](docs/unity-shared-sandbox-draft_ja.md)：サーバーとUnity Webを含む到達形の案。
- [移行の記録](docs/migration-20261111/README_ja.md)：C# repoからの履歴・ファイル対応と検証。
- [ナレッジのUnityスポーク](https://github.com/Naohiro2g/mc-remote-knowledge/blob/main/19-unity/README_ja.md)：repoの位置づけと開発方針。

| 置き場所 | 内容 |
| --- | --- |
| `unity/` | Assets・Packages・ProjectSettingsを管理するUnity Project |
| `scripts/` | 準備スクリプト |
| `docs/` | 操作・観察・設計・移行の記録と図 |

素材は自作の材質とコードで生成する境界線を使います。Minecraftのテクスチャは同梱していません。
