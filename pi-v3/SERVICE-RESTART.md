# 最小移行によるサービス再開

## 2026-11-03 の再開状況

PC73担当から、正式Host `049f0c378f681d73dfde74cc36020f74190c79b0` と専用Pi 1.4.0の配備、
Host→Pi→Qwenの `LOCALBRAIN_PRODUCTION_PI_140_OK`（ToolErrors=0、exit=0）が報告されています。
その後、03:53の `LOCALBRAIN_CONTINUE_PROD_OK` はVS Code標準ChatのAuto/GPT-6 Lunaによる応答だったと訂正されました。
この文字列をContinue/Qwen経路の成功証跡には使いません。
12:19には実Continue拡張でPlan / LocalBrain Qwen 27Bを選択し、新Hostの
`localbrain_agent_v2_status` を1回実行、complete/eligible=true、done、tool error=0を確認したとの報告を受領しました。
これはContinue GUIから正規Host MCPツールへの確認で、固定文字列へのQwen実応答確認とは区別します。
証跡session IDは非公開の進捗記録に保存しています。いずれもPC73担当の報告で、PC70からPC73を操作した結果ではありません。

最新のPC70読み取り確認では現行Gateway `55a2069...` がHTTP応答し、active/queued=0、Qwenはアイドル停止中でした。
Browser ChatはHTTP 200、既存Development RunnerタスクはRunningです。成功済み推論は繰り返していません。
正規のBrowser Chat入口は、非公開サイト設定のホスト名とChatポートから確認しました（実値はローカル証跡に保存）。
Gateway候補 `f5df6bd...` は未配備のため、新優先順位の本番合格はまだ宣言しません。
Server手順コミット `cb9a6f1...` にGateway単独の更新・明示的復旧を追加し、限定試験とソース検査を実施済みです。
ただし、手動管理者実行と公開ネイティブAPIの双方でTCP状態値0/1の不整合があり、正規listenerの所有確認が未成立です。
fresh baseline/正式packet検証・本番反映は保留です。同じUACや手動診断は繰り返しません。
既存配備機構にも、この照合を代替する資格確認済みの経路は見つかっていません。
詳細は [配備引き継ぎ](GATEWAY-DEPLOYMENT-HANDOFF.md) を参照してください。

PC70のSlack OCR mailbox、Bridge relay、CPU worker設定はいずれも未作成で、
通常EXEの `ocr capabilities` も `enabled=false` でした。開発用OCR資産を本番稼働済みとは扱いません。
Slackの既存正常経路は維持し、新OCR経路だけを無効のままにします。
外部レビューのHost接続は引き続き未資格で、追加課金経路は使用しません。

以下は開発・切替前に確認した範囲と設計上の条件です。上記の新しい実機報告と区別してください。

新しい AgentV3 の候補転送・正式適用の完成を、既存サービス再開の条件にしません。
既存の `TaskAgentCoordinator → PiRpcRunner → SideEffectGate → Bridge → Gateway`
を維持します。Windows の認可・承認・証跡・ファイル許可と既存の停止処理は変更していません。
本資料は PC73 への配備済み・本番採用許可を意味しません。

## 今回の最小変更

- 既存 WSL `Ubuntu` の Pi が **1.4.0** でなければ実行しません。
  バージョン更新は Host/モデルの実行要求では行わず、配備時に固定 lock を使用します。
- Host の信頼済み `clientsettings.json` の `piRuntime.piExecutable` と `piRuntime.agentDirectory`
  で専用の実行先・保存先を選べます。モデルの task からは変更できません。
  未指定は従来の `/home/worker/.volta/bin/pi` と `/home/worker/localbrain-v2/pi-1.4` です。
  検証では専用の1.4.0を指定し、旧グローバル Pi を上書きしません。
- Host 管理の `agentDirectory/settings.json` に、publish 出力の
  `scripts/pi-settings.json`（既存 v3 の同一設定）を配置します。内容は標準 Compaction
  `enabled=true / reserveTokens=8192 / keepRecentTokens=12000` のみです。未配置・不一致は停止します。
- Pi 標準セッションは同ディレクトリの `sessions` に保存します。旧 `agent-v2-sessions`
  と履歴は変更・削除・自動移行しません。Critic は既存どおり非永続・ツール無効です。
- 発見された拡張・MCP・スキル・テンプレート・context ファイルと project trust を無効にし、
  Host 同梱の provider・policy・Host ツールだけ明示的にロードします。
  native bash/powershell/edit/write と codemode は有効にしません。任意シェルを追加する代替ではありません。
- `host_memory_search` が Pi policy に拒否される不整合を修正しました。検索認可・上限・原文取得は
  既存 Host のままです。未登録の旧 `obs_recall` は選択から外しました。
- Laya の呼出しを復活させず、既存 Host の反復カウント・境界での abort を再利用します。

## 開発成果の扱い

|区分|扱い|
|---|---|
|A: そのまま再利用|既存 Host の権限、Canonical、TaskMemory/Recall、SecondBrain 読取、Bridge、Git、Pi 1.4 lock、標準 Compaction 設定、Gateway 接続|
|B: 検証後に選択採用|Gateway 優先順位・状態通知、PC70 Slack OCR 接続、追加原文検索、Windows 上で未試験の新経路|
|C: 当面の追加開発を保留|新規 AgentV3 の正式候補検証/適用、隔離・アーカイブ基盤の拡張、独自 Supervisor、Runner 高度化|

既存の開発コミットと証跡は保存します。C の完成待ちで既存 Chat 等を停止しません。
PC73 ピークメモリ比較試験と全面 cleanup は再開条件から外します。既存の実行上限は維持します。

## 確認できた範囲と制約

- PC70: installed Runner `925103450...` の既存 mTLS Gateway 接続を Work から直接利用し、
  無害な単発 Qwen 応答を受領。続けて policy の小修正を1回委任し、Work が検査して取り込みました。
  新しい常駐通信機構はありません。これは Runner の全ジョブ実行や Work 無人再開の検証ではありません。
- Windows Node: 記憶検索の正常/不正入力、保護ファイルと native shell 拒否を確認しました。
- PC70 WSL: 既存 provider と Host ツールの Pi 1.4 startup、標準 Compaction、private session、
  project 拡張・設定を読まないことを確認しました。推論0回の部品試験であり Windows Host 実行ではありません。
- Windows: Host/Test project のビルドと通常 EXE による限定試験が成功しました。
  「コード整合性が原因で Windows 試験を一律実行できない」という扱いは撤回しました。
  Work 内でのファイル移動・名前付きパイプの拒否は、制約外の通常ユーザー実行で成功しています。
  過去の失敗原因と現在の実行結果は [WINDOWS-VALIDATION.md](WINDOWS-VALIDATION.md) に区別して記録しました。
- Browser Chat は既存 PC70 `/health` が HTTP 200。既存サービスを変更・再起動せず確認しました。
- Qwen 本番推論への接続は成功しましたが、Gateway 開発版の優先順位修正は本番未配備です。
  Continue ＞ Slack 解析 ＞ Work の実機確認は管理者権限による別配備後に必要です。
- Slack は会話要約なし・ローカル限定の方針を維持。PC70 AppContainer/mTLS の Windows 結合試験が
  未完了なので、新 OCR 接続を本番で有効化しません。クラウド AI への代替送信はありません。
- 外部レビューは軽微な LOW で不要。NORMAL/HIGH の契約内 Host 呼出しは未接続のまま保留です。
  既存の OpenCode 経路は使いません。PC70 の ChatGPT 認証と Work からの Sol 呼出し記録は、
  PC73 Host からの利用証明ではありません。Opus/Haiku の未確認認証・課金経路へ切り替えません。

## 切替前の最小条件

対象の既存サービス状態、現行 Host/設定/証明書の退避先と戻し方を確認し、通常の正式な EXE 実行で
最終 Host の grant、許可ファイル、取消、結果返却を PC70 で確認します。モデルの回答は権限証明にしません。
サービスごとに合格を判定し、新しい経路が不合格ならその機能だけ無効のままにします。
`v3Development.enabled` は既定無効のままです。未検証の新経路を fallback として有効にしません。
PC73 本番切替はこの開発変更だけでは実施しません。
