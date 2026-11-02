# PC73 移行の判定条件（2026-11-02 開発候補）

**最新方針:** 最短サービス再開では [SERVICE-RESTART.md](SERVICE-RESTART.md) の既存 Host/PiRpcRunner
経路を優先します。以下の新規 AgentV3 候補適用・隔離基盤の完成と PC73 メモリ比較試験は、
既存サービス再開の共通条件ではありません。未検証の各新経路だけ採用を保留し、既存の認可・証跡・実行上限を維持します。

PC73 本番への配備はまだ許可された状態ではありません。以下は作業順序と未検証境界であり、配備済みの記録ではありません。Canonical、承認履歴、証跡、SecondBrain、本番設定は移行元の正本を維持します。

1. 新規証明書や署名基盤を要件にしません。PC70 REI では通常 EXE の Canonical・管理タスク・許可ファイル・取消試験が成功しました。Work 内のファイル/パイプ拒否を PC70/PC73 全体のコード整合性障害と扱わないでください。人間の実承認と Pi/Qwen/Bridge を通す結合確認は別に必要です。[WINDOWS-VALIDATION.md](WINDOWS-VALIDATION.md) の手順を使い、実際にブロックされた場合だけ原文・実行環境を保存して保留します。
2. Pi はリポジトリの lock に従い 1.4.0、Node は対応する検証済み版を固定します。私有 WSL runtime に Node、bubblewrap、Pi、provider、settings、loop・memory 拡張を準備します。既存の qualified Qwen tokenizer と固定 chat template を使い、別モデルの tokenizer で代用しません。
3. Host の `v3Development` は既定無効です。`HOST-INTEGRATION.md` の限定 LOW proposal 経路を、登録済み開発用 Git root と明示的な native grant で確認します。`candidate_ready` は未適用・未合格で、同じ workspace の次作業を待機させます。既存 cancel の exact task ID 指定で、終了確認済みの候補を証跡を残して明示破棄できる実装がありますが、Windows実行は未検証です。正式な候補検査・適用は未実装で、状態ファイルを編集して解除しません。通常運用への切替条件は未達です。
4. Gateway の開発ブランチには Continue、Slack 解析、Work の順序と同順位 FIFO、実行中推論の非割込みがあります。本番 Gateway は未更新です。正式な管理者権限での別配備と、実際の PC73 要求による確認が必要です。Host の状態照会は Gateway が返した待機・開始通知だけを表示対象にし、追加推論は行いません。Continue UI での表示は未確認です。
5. Slack OCR は `SLACK_CONNECT.md` と Server の `SLACK-SERVER.md` に従い、PC70 の既存 Bridge/OCR scheduler/AppContainer と専用 mailbox を接続します。PC73 Host と PC70 worker の mTLS identity、IP、owner、TLS 名、`origin=slack` を確認してから Remote OCR を有効にします。PC73 のローカル OCR 設定は Remote OCR 経路では不要です。PC70 の OS 隔離が確認できない場合は有効化せず、通常 Python やクラウドへ迂回しません。Slack 会話要約は新経路で使用しません。
6. TaskMemory は既存イベント ID 最大8件の選択と原文を維持します。追加検索・Recall は同じ task の Host 認可を通し、検索8回・Recall32回を既存 Canonical に記録します。別 task・別 project の検索許可を追加するものではありません。SecondBrain の既存読取・採用ポリシーも維持します。Slack の原文・結果をこれらの検索や外部レビューへ混入させません。
7. PC73の専用メモリ比較試験は移行条件にしません。32K Context、実行時間等の既知の上限を維持し、通常利用で問題が出た場合に調査します。未測定の削減量を推計して報告しません。
8. Opus/Haiku の CLI と認証は未確認で、Host からの Sol subscription-only 経路も未接続です。未確認の API や追加課金へ自動切替しません。独立レビューが必要な変更は未合格のまま残します。

本番適用前に必要な機能を検証し、元のサービス停止・再起動と復旧手順を配備環境で確定します。全体検証後に development-cleanup（利用できなければ範囲を確認した手動整理）を行います。使用中の worktree、正本データ、認証情報、正式証跡と Git 履歴は削除しません。
