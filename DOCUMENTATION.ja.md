# LocalBrain文書の日本語案内

説明文書を日本語化しました。コマンド、API名、設定キー、モデル名、ファイル名、コード例は原表記を保持しています。

## 運用・機能の説明

| 内容 | 文書 |
| --- | --- |
| 初期設定 | [README.md](README.md) |
| フォルダーの役割と整理 | [フォルダー案内](docs/FOLDER_GUIDE.ja.md) |
| ビルド・リリース管理 | [配備手順](tools/deployment/README.md) |
| Agentのコマンド実行 | [AGENT_COMMAND_EXECUTION.md](AGENT_COMMAND_EXECUTION.md) |
| コンテキスト引継ぎ | [CONTEXT_HANDOFF.md](CONTEXT_HANDOFF.md) |
| コンテキスト計測 | [CONTEXT_TELEMETRY.md](CONTEXT_TELEMETRY.md) |
| Reviewer振分け | [REVIEWER_ROUTING.md](REVIEWER_ROUTING.md) |
| 作業ループ監視 | [WORK_LOOP_MONITORING.md](WORK_LOOP_MONITORING.md) |
| OCR | [LOCAL_OCR.md](LOCAL_OCR.md) |
| ブラウザーChatの画像OCR | [CHAT_IMAGE_OCR.md](CHAT_IMAGE_OCR.md) |
| Slack | [SLACK_CONNECT.md](SLACK_CONNECT.md) |
| Slack検証の範囲 | [SLACK_VALIDATION.md](SLACK_VALIDATION.md) |
| 実行ルールの日本語説明 | [ルールの読み方](docs/RULES_GUIDE.ja.md) |

## 履歴と現在の状態の区別

[Agent試行](AGENT_V2.md)、[実装状況](IMPLEMENTATION_STATUS.md)、[検証履歴](SLACK_VALIDATION.md)、[統合監査](INTEGRATED_SLACK_OCR_AUDIT.md)には、日付ごとの過去の候補・配備・未確認事項が含まれます。今回の日本語化では、その時点の状態を現在の成功として書き換えていません。「未配備」「未コミット」「候補」「残作業」も、記録された時点の意味です。実際の最新版・稼働状態は、現在のGit、インストール記録、起動設定、実プロセスと照合してください。

[A/B/C評価計画の日本語訳](EVALUATION_PLAN_ABC.ja.md)は、取消済み計画の履歴です。原文[EVALUATION_PLAN_ABC.md](EVALUATION_PLAN_ABC.md)はユーザー添付のバイト単位の記録で、既存資料にSHA-256が記載されているため、上書きせず保持します。翻訳は比較やモデル起動の再開を指示しません。

## 原文を残すもの

- `.continue/rules`と`tools/continue-rules`はモデル向けの実行指示です。単なる説明文書と混同せず、今回の文書翻訳では動作を変えません。日本語の説明は別に用意しました。
- 配備パケット、承認対象、監査の原本、過去のソーススナップショットは、ハッシュ・署名・当時のレビューとの対応を守るため保持します。日本語案内と原文を区別します。
- 第三者のライセンス、依存パッケージのREADME、モデル提供元の原文は配布元の記録です。公式原文を置換しません。
- OCR結果・知識候補・利用者データは運用説明書ではありません。日本語化を理由に再解析・再送・書換えしません。
- `private-*`はGit除外した環境固有の旧原本です。公開文書は接頭辞のない正式ファイルを参照します。秘密情報を含み得る原本を、公開PRのために読み出したり転記したりしません。

この文書変更はプログラム・設定・DB・モデルを変更せず、サービス再起動を必要としません。日本語の文章に切り替えても、その説明に書かれた機能が新しく有効化されるわけではありません。
