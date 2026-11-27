# PC73のCodexに貼る実行指示

次期LocalBrainのPC73主開発担当として作業を開始してください。PC70側は別のCodexで進行しています。対象は `ishikawa-moto/LocalLLM_Client` と `ishikawa-moto/LocalLLM_Server`。現行稼働とユーザー作業中のチェックアウトを維持し、最終承認後の一括切替に備えます。

## 最初に実機とGitを照合

1. `hostname`、Windowsの実行ユーザー、既存Client HostとVS Code/Continueの起動経路を確認。現行設定はキー名・適用状態・指紋だけを記録し、認証値・Slack/OCR原文は取得結果に出さない。
2. 両リポジトリで `git --no-optional-locks status --porcelain=v1 --branch`、`git rev-parse HEAD`、remoteと対象ブランチのSHAを確認。未コミット変更を記録する。次期版は別cloneに置き、共通 `codex/next-localbrain-bonsai-durable` を基点にPC73担当を `codex/next-localbrain-bonsai-durable-pc73` で作業する。remoteに共通ブランチが無い場合はPC70とSHAを照合し、検証済みの許可済みLAN配布または到着待ちで進める。既存 `main` への編集、push、merge、本番設定・ポート・配備先・起動タスクの変更は行わない。
3. Serverの `next/README.md` と、両repoの `next/` のHEADを記録。API `localbrain-handoff/1` とPi Durable `1.6.1` を固定。生の社内資料・秘密・認証情報はコミットや証跡に入れない。PC70側の接続先・証明書とトークンは認可済み経路で受領して、独立テスト環境の設定へ置く。

## PC73実装

- `next/worker-client.mjs` と `next/worker.mjs` を既存の安全性が確認できたスーパーバイザーへ接続。PC70の予備開発にも同一ワーカーを使う。Pi標準MCP、Codemode、Tool Search、Skillsと独立VS Codeプロファイルを結線する。現行MCP、Continueの設定、既存LLM接続を変更しない。
- 起動時は中央に登録する。起動インスタンスは認証されたスーパーバイザーに限り、Piにトークンを渡さない。プロセスの二重起動を防止し、同じインスタンス・Goal・epochで `startOrResume` を冪等にする。すべての書込コマンドで中央の `check` とローカルのワークスペース権限を確認する。状態、Goal、Canonicalの正本はPC70。
- `stopAndCheckpoint` は新規コマンドを止め、現在の操作を安全地点で完了し、非mainの許可済み変更を選別してコミット。許可済み内容をGitHubの指定非mainまたは認可済みLAN経路へ保全し、転送先で両repo SHAと成果物指紋を再照合する。OSの管理範囲から起動した子プロセスと外部ブローカーを収束できたこと、旧書込権を失効したことを実測する。いずれかが未確認なら `UNKNOWN` とし、移管完了や電源断は保留する。
- `prepareCheckout` は受領SHA、環境マニフェスト、ワークスペースの清浄性、ブランチと転送の許可、停止証跡を検証。競合や未コミットを上書きしない。PC70で受領グラントが確定した場合にだけ明示シャットダウン操作を完了し、全対象Goalに対して `taskPreserved` を確認する。通信断から別ウォーカーが所有権を取得する経路を作らない。
- PC73の再起動または登録をPC70が検出したら、PC73由来の未完了Goalを安全な区切りで受け取り、同じGoalを自動再開。PC70由来タスクはPC70で継続する。
- 既存の難易度別委譲と決定的検証を利用。LOWはBonsai失敗後Terra、再検証後Haiku。NORMALはTerra→Haiku→Sol→Opus、HIGHはSol→Opus。外部CLIの利用は中央の送信ポリシー適用後のみ。Slack・OCR原文や秘密を外部へ送らない。CodeGraph、LSP、レビュー補助は効果を測れる場合だけ取り込む。

## 結合検証とPC70への返却

使い捨ての非main作業で、Goalから変更・テスト→明示移管→PC70で同じSHAとGoalを再開→PC73再起動後に戻って検証完了まで進める。両機で同時に書込みができないこと、旧グラントや不正なSHA・未確認停止が拒否されること、通信断で移管しないこと、PC73停止中もPC70が単独OCRできることを確認する。PC73の独立VS CodeからBonsaiとMCPを確認し、ランタイムの実測とユーザーの現行経路を区別して記録する。

PC70へ、両repoのHEAD、APIバージョン、エンドポイントの識別子、実行したGoal、実行権世代の遷移、証跡の保管場所と指紋、テストの合否、実装済み・未確認・ブロッカーを返す。資格情報や原文は返却文に含めない。エラーは次期版側で修正・再検証する。最終切替前に旧安定版の復旧条件と切替手順をPC70と照合する。
