# レビュー機能だけの切替・復旧

本書は実行手順です。配備済み・安定版確定の証明ではありません。
Gateway `55a2069`、Slack Host、OCR、TaskMemory、SecondBrain、Pi runtime は変更対象外です。
PC73 の古い PID を使用せず、その場で Host / Bridge の実行パス・開始時刻・既存 installation と照合します。

## PC70 の開発成果

- 専用ブランチ `codex/v3-stable-review-20261105`。ベースは Client `d2a5d26`、実行コードの基準は正式 Host `049f0c3` と同じです。
- ローカル試験は Test EXE の `--external-review-availability-proof`。LOW、NORMAL Sol、条件付き Opus、HIGH 停止、Slack 拒否、厳格な結果解析、未資格経路と証跡を確認します。
- 公開合成資料の `--external-review-public-live <Codex EXE 絶対パス> <SHA256>` は一時 Canonical のみを使用します。
  Sol 最終レビューの送信・回答・Canonical 結合試験であり、人間承認や PC73 本番実行の代替ではありません。
- 本番の `reviewers.json` は未作成・未変更。Opus は実 CLI・認証・モデルを確認するまで無効です。

## PC73 の採用前確認

1. 現在の Host release、ハッシュ、起動方法、実行ユーザー、installation、Continue MCP の参照先を読み取り確認します。
   既存 release、設定、証明書、承認履歴、Canonical を保全します。Canonical 全 DB や Slack 内容を外部へ渡しません。
2. Host と同じ Windows ユーザーの既存 Codex で `login status` を実行し、ChatGPT 認証であることを確認します。
   非推論の利用枠情報とモデル一覧で Sol が選べることを確認します。API キー、BYOK、有料リセットへ切り替えません。
3. 対象 Codex EXE の絶対パス・バージョン・SHA256 と、[制限設定](../REVIEWER_ROUTING.md)の実効性を確認します。
   空の試験ディレクトリ、公開の限定資料だけで、独立セッション、stdin 入力、shell / read / write / MCP / hooks / plugin 不可、
   取消・終了、未知イベント拒否を検証します。`--external-review-public-live` の成功だけで全境界を資格済みにはしません。
   OS 保護を変更・回避せず、境界を確認できなければ `packetBoundaryVerified=false` のまま保留します。
4. Opus を使う場合も、その PC の正式 Copilot CLI 本体・契約内認証・`claude-opus-5.5` と stdin / 全ツール拒否を別途確認します。
   Copilot shim の存在だけを導入済みと扱いません。CLI 設定による BYOK、custom MCP、hooks、会話復元等が残っていれば有効化しません。
   Opus 未資格でも Sol は利用できますが、NORMAL の実 fallback 検証は未完了として残します。HIGH は Sol 不在で停止します。
5. Host が選んだ既存 export policy が `manual` の場合は、Sol/Opus それぞれの正確な manifest に通常の人間承認を与えます。
   試験のために本番ポリシーを `auto` へ変更しません。HIGH の既存人間承認と計画・最終 Sol ゲートを維持します。

## 成果物の準備と切替

1. 検証済みの正確なコミットを clean worktree に用意し、既存 `tools/Build-ReproducibleClient.ps1`
   を実際の既存 SDK バージョンを指定して実行します。認証・本番設定を出力に入れません。
2. 同梱 `tools/deployment/Stage-ClientRelease.ps1` で新規 release ディレクトリへハッシュ付きで stage します。
   Stage は有効化しません。既存 installation、旧 manifest、起動参照をローカルの復旧用ディレクトリへ保存します。
3. 必要な承認と短い Host 保守時間を確認した後、進行中タスクがないことを確認します。
   現在の正式な Host 起動参照だけを新 release に切り替えます。installation を使う場合、
   `release` / `source_commit` / `manifest_sha256` / `product_dll_sha256` は新成果物に対応させ、
   configs、証明書、ポート、Canonical、Pi private runtime の binding は維持します。
   PC ごとの起動参照が未確認なら自動変更しません。Gateway や Slack のタスクは停止しません。
4. 資格確認できた endpoint だけ、既存 control-plane の `reviewers.json` に設定します。
   このファイルは Host 管理下に置き、モデルのワークスペース・Git には置きません。
5. 新 Host の起動・既存 Bridge health、公開 LOW の通常経路、許可済み NORMAL のレビューと Canonical 記録を限定確認します。
   Sol 利用不能時の Opus は、資格確認済みの場合だけ公開試験で確認します。
   Slack 内容を試験モデルへ送らず、既存 Slack Host が未変更であることをプロセス・起動参照で確認します。

## 復旧

- 新規タスク受付を止め、所有している新 Host を正式な起動機構で終了させ、実際の終了を確認します。
- 退避した installation / Continue の Host 起動参照へ戻し、旧 Host を同じ実行ユーザーで再起動します。
- `reviewers.json` も切替前の状態へ戻します。旧 Host では新設定を利用しません。
- Canonical / Task State / DB / 承認履歴を巻き戻し・削除しません。旧・新 release と manifest、今回のレビュー証跡を保持します。
- 継続中タスクが新しいレビュー checkpoint を持つ場合、旧 Host で勝手に PASS・完了へ変更しません。
  既存状態を保全し、当該タスクだけレビュー未完了として停止します。

本番の正式接続・必須検証が未完了なら stable tag は付けません。ビルド成功、公開資料での部分結合、PC73 本番稼働を区別します。
