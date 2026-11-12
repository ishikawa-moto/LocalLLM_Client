# 現行 v3 の外部レビュー

このブランチはレビュー機能の開発候補です。本番配備・安定版確定ではありません。
既存の Windows Host / Pi 1.4.0 / Bridge / Gateway / Canonical を再利用します。

| リスク | 最終レビュー | 完了条件 |
|---|---|---|
| LOW | 外部呼出しなし | 既存の指定テスト、ローカル Critic、許可範囲、false-verified 検査 |
| NORMAL | Sol `gpt-6.1-sol`。UNAVAILABLE のときだけ Opus `claude-opus-5.5` | 上記ローカル検査と有効な外部 PASS |
| HIGH | Sol の計画レビューと最終レビュー | 既存の人間承認、ローカル検査、両 Sol PASS を維持 |

HIGH の Opus 代替は実装しません。既存ゲートが Sol を必須にしているため、Sol を利用できなければ停止します。
Opus 設計・Haiku 実装補助・救済経路の拡張は今回の対象外です。Laya/Astra を呼びません。
保存状態・公開結果の `copilot` は既存フィールド名で、Opus fallback を示します。Astra の旧結果欄は呼出しを意味しません。
ISSUES/INVALID、取消、Host の認可拒否、承認待ちから PASS を取り直す fallback は行いません。

## パケット境界

ReviewerEvidence の上限 64,000 UTF-8 bytes、秘密検出、HostTaskJournal / HostEgress の選択資料・送信方針・監査・単回承認を再利用します。
要件、受入条件、対象ファイル、限定差分、指定テスト成否、許可された SecondBrain 参照だけを送ります。
Actor の会話・セッション・作業ディレクトリは渡しません。Host の redaction で内容が変わった場合は INVALID で止めます。
Slack / Browser OCR の RestrictedExecution では送信を拒否します。Slack 本文・画像・OCR資料を試験や外部レビューに使いません。

Sol は正式 Codex CLI の ChatGPT 認証だけを使います。API/BYOK/プロキシ/トレース環境変数と Host capabilities を継承しません。
`--ignore-user-config --strict-config --ephemeral`、固定モデル、標準入力パケット、空の一時ディレクトリを使用します。
shell、code-mode host、画像、Web、MCP、plugins、memory、subagents、hooks 等を無効化し、
追加の named permission profile は root read を deny、最小 OS 読取のみ、書込・ネットワーク不可とします。
ツールイベント、失敗、未完了、未知イベント、形式不正を PASS にしません。ツールイベント拒否自体を OS 隔離の代用とはしません。

Opus は Copilot CLI の正式な契約内認証と packet-only 境界を、その Host で確認した場合だけ有効です。
汎用 view を許可する旧アダプターを廃止し、資料は stdin のみ、全ツールを deny、組込 MCP・remote・custom instructions を無効化します。
実CLIでこの入力・拒否・認証条件を確認できなければ、UNAVAILABLE のままです。
モデルの回答に「使える」と書かれていることだけでは資格確認になりません。

## Host 設定

リポジトリ設定・モデル入力ではなく、既存 control-plane の `reviewers.json` のみを読みます。
実行先は絶対 EXE パスと SHA256 で固定し、実行中はファイルを read-lock します。
未設定、未資格、ハッシュ変更、認証・接続・利用枠の失敗は UNAVAILABLE です。
各 Host で正式認証・契約内利用・モデル・packet 境界を確認するまで true にしないでください。
PC70 の資格を PC73 へコピーしません。CLI 更新時は再資格確認が必要です。

```json
{
  "sol": {
    "path": "C:\\approved\\codex.exe",
    "sha256": "<SHA256 of qualified binary>",
    "subscriptionVerified": false,
    "packetBoundaryVerified": false
  },
  "opus": null
}
```

設定・認証・証明書を Git に登録しません。手動送信ポリシーでは既存の正確な manifest に対する人間承認が必要です。
Sol の承認は Opus の承認へ流用しません。
要求モデルと実行成否、phase、packet hash、解析済み判定を Canonical に保存し、実際の開始だけ既存レビュー回数へ加算します。
CLI が backend model ID を返さない場合は null / unverified とします。stderr や認証情報は保存しません。

## 確認方法と配備

Release build 後、既存 Test EXE の `--external-review-availability-proof` で限定のローカル確認を実行します。
`--external-review-public-live <absolute-codex.exe> <sha256>` は公開合成資料・一時 Canonical による Sol 最終レビューの結合確認です。
この fixture の資格フラグはテスト入力であり、本番の packet 境界資格の証明・有効化にはなりません。
PC73 の手順と復旧は [レビュー切替手順](pi-v3/REVIEW-DEPLOYMENT.md) を参照してください。

正式資料: [Codex 設定](https://learn.chatgpt.com/docs/config-file/config-reference)、
[Copilot CLI オプション](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference)、
[Copilot 認証](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/authenticate-copilot-cli)。
