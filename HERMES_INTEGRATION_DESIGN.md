# Hermes Agent統合の設計メモ（ClientPC、2026-09-25）

> **履歴資料。** Hermes統合はユーザーのGSQ+Pi採用決定により中止されました。実装・インストールは行っていません。現行仕様は [GSQ_PI_SPEC.md](GSQ_PI_SPEC.md) です。

状態：**撤回された設計のみ。Hermes未導入、本試験未実行。** 当時の評価条件は [EVALUATION_PLAN_ABC.md](EVALUATION_PLAN_ABC.md)、32K/64Kの適合条件は [PLAN_TRANSITION_ABC.md](PLAN_TRANSITION_ABC.md) を参照してください。現在の採用仕様は [GSQ_PI_SPEC.md](GSQ_PI_SPEC.md) です。

## 調査で確かめたこと

- ClientPCのWindows PATHとUbuntu WSLにはHermesコマンドがありません。
- [Hermes公式インストール文書](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/getting-started/installation.md)はWindowsネイティブとWSL2の両方を提供します。Windowsソースインストーラには `-HermesHome`、`-InstallDir`、`-SkipBrowser`、`-NonInteractive` の隔離・非対話オプションがあります。インストーラを直接実行する前に、取得する版・commitと変更先を固定して確認します。
- WSL内から既存のWindows loopback Bridge `__BRIDGE_HOST__:__BRIDGE_PORT__/v1/models` への読取専用接続は、この時点で `HTTP=000` でした。Piは専用のWindows stdio relayを使っています。HermesのWSL構成で同じURLが使えると仮定しません。
- [Hermes公式モデル設定文書](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/integrations/providers.md)はカスタムOpenAI互換endpointをサポートする一方、ツール付きAgentの実際の文脈長に64,000以上を要求します。ServerPCのGSQは報告上32,768です。
- [Hermes公式ツール文書](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/features/tools.md)にはterminal、file、web、memoryなどのtoolsetがあります。これらを単に隠すだけではLocalBrainのファイル許可リストや検証ゲートの代替になりません。

## 既存ClientPCコードで共通化する位置

| 領域 | 現状 | Hermes統合時の境界 |
| --- | --- | --- |
| Actor | `AgentTaskRunner.cs` が `PiRpcRunner.RunAsync` を直接呼ぶ | Actor実行だけharness adapterへ分離し、PiとHermesを選択する |
| Fresh Critic | 別のPi RPC no-tools/no-sessionでGSQを呼ぶ | A/B/Cで同じGSQ Critic実装を使う。Actorのsessionや履歴を渡さない |
| 安全規則 | `AgentTaskRequest`、`HighRiskApproval`、`GitEvidence` | request/hashにharness・Actorモデルを束縛し、無効な組合せや途中切替を拒否する |
| 監督・検証 | `LayaSupervisor`、`ToolRouter`、`ValidationGate`、`SolReviewer` | A/B/Cで同じ条件を適用し、Hermes側の「完了」を検証済みと見なさない |
| 観測 | `PiRunResult`、`ProgressTracker`、`TelemetryRecorder` | Hermesのイベントを共通の件数・エラー・進捗形式へ正規化し、本文・認証情報を保存しない |
| 知識 | `SecondBrainEvidence` | SecondBrainを唯一の長期プロジェクト知識とし、Hermes memoryは必要なsession状態に限定する |
| ServerPCモデル | BridgeとGatewayの現在のGSQ経路 | CのBonsai Actor後にGSQ Criticへ安全に戻す管理契約・失敗時復旧が必要。未実装 |

## 隔離導入の順序

1. ServerPCで**実際の64K以上のGSQ安定動作**が確認できるまで、HermesとGSQのActor接続試験は行いません。`context_length` の偽装や無断のServerPC設定変更はしません。
2. Hermesの公式版を固定し、Windowsネイティブの隔離ディレクトリと非対話インストールを第一候補として評価します。これなら既存のWindows loopback Bridgeへ接続できる可能性がありますが、インストール後に実測します。既存のOpenCode OAuthやContinue設定は流用・変更しません。
3. Hermesの実効toolsetを列挙し、任意terminal、Web、外部接続、独立した長期memoryを無効にします。書込はLocalBrainの厳密な `allowed_files` とGit前後差分で防ぎます。Hermesの素のファイルツールで許可リストを保証できなければ、ホスト管理の専用tool adapterまたはOS隔離ができるまで実行しません。
4. 制御面を共通化してから、隔離Gitリポジトリで読取、許可された編集、拒否される編集、テスト失敗、Critic不合格、Sol不通、再開を順に確認します。実運用Continue profileや本番hostは切り替えません。
5. B/Cのモデル切替はServerPCの正確な管理契約が確定してから追加します。Bonsaiの版、MTP、モデルハッシュとruntimeを固定し、GSQ Criticへの復帰失敗時は検証ゲートを閉じます。

新しいA/B/C試験は実リポジトリの3課題×3構成を各1回、最大9回です。ここでの隔離導入検査は製品接続の確認であり、本試験回数には算入しません。
