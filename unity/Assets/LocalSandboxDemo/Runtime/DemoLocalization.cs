using UnityEngine;

namespace Code2Create.UnitySandbox.LocalDemo
{
    internal enum DemoLanguage
    {
        Ja = 0,
        En = 1
    }

    internal static class DemoText
    {
        private static readonly string[] DirJa = { "北", "北東", "東", "南東", "南", "南西", "西", "北西" };
        private static readonly string[] DirEn = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        internal static string CoordTargetName(DemoLanguage lang, bool targetMode)
        {
            if (lang == DemoLanguage.En) return targetMode ? "Target" : "Legs";
            return targetMode ? "照準" : "脚";
        }

        internal static string CompassDir(DemoLanguage lang, int index)
        {
            string[] names = lang == DemoLanguage.En ? DirEn : DirJa;
            return names[((index % 8) + 8) % 8];
        }

        internal static string TargetToggleButton(DemoLanguage lang, bool targetMode)
        {
            if (lang == DemoLanguage.En) return targetMode ? "Target(Q×2)" : "Legs(Q×2)";
            return targetMode ? "照準(Q×2)" : "脚(Q×2)";
        }

        internal static string SpeedButton(DemoLanguage lang, bool fast)
        {
            if (lang == DemoLanguage.En) return fast ? "Spd×2" : "Spd×1";
            return fast ? "速度×2" : "速度×1";
        }

        internal static string ExpandButton(DemoLanguage lang, bool expanded)
        {
            if (lang == DemoLanguage.En) return expanded ? "Hide" : "Info";
            return expanded ? "たたむ" : "ひらく";
        }

        internal static string ResyncButton(DemoLanguage lang) => lang == DemoLanguage.En ? "Resync" : "再同期";
        internal static string FullscreenButton(DemoLanguage lang) => lang == DemoLanguage.En ? "Full" : "全画面";
        internal static string LanguageToggleButton(DemoLanguage lang) => lang == DemoLanguage.En ? "JA" : "EN";

        internal static string SyncStatus(DemoLanguage lang, SyncStatusKind kind)
        {
            if (lang == DemoLanguage.En)
            {
                switch (kind)
                {
                    case SyncStatusKind.LocalNoServer: return "Local (no server)";
                    case SyncStatusKind.Connecting: return "Connecting...";
                    case SyncStatusKind.Online: return "Online (sync)";
                    case SyncStatusKind.OfflineLocalOk: return "Offline (local)";
                    case SyncStatusKind.OfflineReconnecting: return "Offline (retry)";
                    default: return "Local";
                }
            }

            switch (kind)
            {
                case SyncStatusKind.LocalNoServer: return "ローカル体験（同期サーバー未指定）";
                case SyncStatusKind.Connecting: return "同期サーバーへ接続中...";
                case SyncStatusKind.Online: return "オンライン（自動同期中）";
                case SyncStatusKind.OfflineLocalOk: return "オフライン（ローカル体験可）";
                case SyncStatusKind.OfflineReconnecting: return "オフライン（自動再接続待ち）";
                default: return "ローカル体験中";
            }
        }

        internal static string TopDetailLine(DemoLanguage lang, SyncStatusKind kind, string activeStreamId, int blockCount, bool isFlying)
        {
            string statusText = SyncStatus(lang, kind);
            if (lang == DemoLanguage.En)
            {
                string pairTag = string.IsNullOrEmpty(activeStreamId) ? "Unpaired" : $"Paired ({activeStreamId})";
                string moveMode = isFlying ? "Fly" : "Walk";
                return $"{statusText} | {pairTag} | {blockCount} blk | {moveMode}";
            }

            string jaPair = string.IsNullOrEmpty(activeStreamId) ? "未ペアリング" : $"接続中 ({activeStreamId})";
            string jaMove = isFlying ? "フライト" : "歩行";
            return $"{statusText} | {jaPair} | {blockCount}個 | {jaMove}";
        }

        internal static string PlotButtonLabel(DemoLanguage lang, int index)
            => lang == DemoLanguage.En ? $"Plot {index}" : $"区画{index}";

        internal static string PlotBillboardLabel(DemoLanguage lang, int index, Vector3Int origin)
            => lang == DemoLanguage.En
                ? $"Plot {index} ({origin.x}, {origin.y}, {origin.z})"
                : $"区画{index} ({origin.x}, {origin.y}, {origin.z})";

        internal static string LastBlockButton(DemoLanguage lang) => lang == DemoLanguage.En ? "Last Block" : "直前のブロックへ";
        internal static string HighlightToggleButton(DemoLanguage lang, bool enabled)
        {
            if (lang == DemoLanguage.En) return enabled ? "Glow:ON" : "Glow:OFF";
            return enabled ? "発光:ON" : "発光:OFF";
        }

        internal static string PresetButton(DemoLanguage lang, int index)
        {
            if (lang == DemoLanguage.En)
            {
                switch (index)
                {
                    case 0: return "1:Columns";
                    case 1: return "2:Wall/Stair";
                    case 2: return "3:10 Blocks";
                    default: return "Origin(R)";
                }
            }

            switch (index)
            {
                case 0: return "1:列と柱";
                case 1: return "2:壁・階段";
                case 2: return "3:全10種";
                default: return "原点(R)";
            }
        }

        internal static string KeybindLine1(DemoLanguage lang)
            => lang == DemoLanguage.En
                ? "Look: Click view (Esc: release/exit)"
                : "視線: クリック後マウス制御 (Esc:解除、脱出)";

        internal static string KeybindLine2(DemoLanguage lang)
            => lang == DemoLanguage.En
                ? "Move: WASD / Jump:Space / Reset:R"
                : "移動: WASD / ジャンプ:Space / 初期位置:R";

        internal static string KeybindLine3(DemoLanguage lang)
            => lang == DemoLanguage.En
                ? "Fly : Space×2 (Up:Space Dn:Shift) Off:Shift×2"
                : "飛行: Space×2 (昇:Space 降:Shift)  解除:Shift×2";

        internal static string KeybindLine4(DemoLanguage lang)
            => lang == DemoLanguage.En
                ? "Chat: T or /   Coords(Legs↔Target): Q×2"
                : "会話: T または /   座標切替(脚↔照準): Q×2";

        internal static string ChatTitle(DemoLanguage lang)
            => lang == DemoLanguage.En
                ? "Chat / Pairing (/mcremote pair NNN-NNN)"
                : "チャット / ペアリング (/mcremote pair NNN-NNN)";

        internal static string WelcomeMessage(DemoLanguage lang, string version)
            => lang == DemoLanguage.En
                ? $"Welcome to the .NET Sandbox! (v{version})"
                : $"ようこそ、.NET箱庭へ！ (v{version})";

        internal static string SnapshotReceivedMessage(DemoLanguage lang, int regionCount)
            => lang == DemoLanguage.En
                ? $"[Sync] Snapshot received ({regionCount} regions)"
                : $"[同期] スナップショットを受信しました (地域 {regionCount}件)";

        internal static string OriginSyncedMessage(DemoLanguage lang, Vector3Int origin)
            => lang == DemoLanguage.En
                ? $"[Pairing] Origin synced to {origin}"
                : $"[ペアリング] 原点を {origin} へ同期しました";

        internal static string PairSucceededMessage(DemoLanguage lang, string pairCode)
            => lang == DemoLanguage.En
                ? $"[Paired] Code {pairCode} approved!"
                : $"[ペアリング成功] コード {pairCode} を承認しました！";

        internal static string PairFailedMessage(DemoLanguage lang, string reason, int remaining)
        {
            string formatted = FormatPairReason(lang, reason);
            return lang == DemoLanguage.En
                ? $"[Pair failed] {formatted} (remaining: {remaining})"
                : $"[ペアリング失敗] {formatted} (残り試行: {remaining})";
        }

        private static string FormatPairReason(DemoLanguage lang, string reason)
        {
            if (lang == DemoLanguage.En)
            {
                switch (reason)
                {
                    case "invalid_pair_code_format": return "Enter a 6-digit NNN-NNN code";
                    case "invalid_pair_code": return "Pair code not found";
                    case "pair_code_expired": return "Pair code expired (120s)";
                    case "rate_limited": return "Too many attempts (please wait)";
                    default: return string.IsNullOrEmpty(reason) ? "Failed" : reason;
                }
            }

            switch (reason)
            {
                case "invalid_pair_code_format": return "NNN-NNN の6桁形式で入力してください";
                case "invalid_pair_code": return "コードが見つかりません";
                case "pair_code_expired": return "コードの有効期限（120秒）が切れました";
                case "rate_limited": return "試行回数の上限に達しました（少し待ってください）";
                default: return string.IsNullOrEmpty(reason) ? "失敗しました" : reason;
            }
        }

        internal static string LocalChatPrefix(DemoLanguage lang) => lang == DemoLanguage.En ? "[Local] " : "[ローカル] ";

        internal static string PairOfflineHint(DemoLanguage lang)
            => lang == DemoLanguage.En
                ? "[Hint] Pair approval works when connected to the sync server."
                : "[ヒント] 同期サーバーに接続するとペアリング承認が有効になります。";

        internal static string PairAssistButton(DemoLanguage lang) => lang == DemoLanguage.En ? "Pair Cmd" : "入力補助";
        internal static string SendButton(DemoLanguage lang) => lang == DemoLanguage.En ? "Send" : "送信";

        internal static string ChatPlaceholder(DemoLanguage lang)
            => lang == DemoLanguage.En
                ? "Message or /mcremote pair NNN-NNN"
                : "メッセージ または /mcremote pair NNN-NNN";
    }
}
