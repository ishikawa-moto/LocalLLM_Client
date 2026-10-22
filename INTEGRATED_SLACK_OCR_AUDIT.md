# Slack/OCR 統合監査候補（2026-10-23）

この文書はGit上の候補と確認範囲を示します。本番切替・DB移行・認証変更・mainマージは実施していません。

## 照合結果と状態

| 対象 | ソース | 隔離検証 | 本番配備 | 本番動作 | Git統合 |
|---|---|---|---|---|---|
| 既配備の優先譲渡理由記録 | 実装済み | 公開fixture PASS | 既配備版を照合 | 既存完了Jobのmetadataを確認 | `bfae367` |
| 出力長を含む120秒上限 | 実装済み | Server公開回帰 | 現用capability=120を認証確認 | 過去の実完了receiptあり | Server候補へ統合 |
| 有限Attempt・安全な復旧 | 実装済み | 永続状態・競合・hash・slot待機stub | 未配備 | 未検証 | 開発ブランチ |
| 大画像のメモリ制御 | 実装済み | 本物のOCR、Windows Job/AppContainer | 未配備 | 未検証 | 開発ブランチ |
| 複数罫線表・結合セル | 実装済み | 公開財務表、正解セル採点 | 未配備 | 未検証 | 開発ブランチ |
| 罫線なし列推定 | 実験的 | 公開財務表で確認 | 未配備 | 未検証 | 開発ブランチ |
| JSON/HTML/Markdown/CSV | 実装済み | 実OCR投影・配送stub | 新形式は未配備 | 旧形式は実送信receiptあり | 開発ブランチ |
| 作成者限定Session | 明示設定の候補 | 公開ACL fixture | 未変更（共有を維持） | 新モード未検証 | 開発ブランチ |

ClientPCの監査開始時mainは `aaa7877c66a62c4392f8442fdd8fe96e359e0695`、ServerPCのGit基準は `ec26ab9` です。既存の開発worktreeは削除・リセットしていません。

稼働Slack DLLのSHA256は `3d1c0c454b86493ae4591623199b003650d54e633ebb8ffe665e240419cf26c8`。公開PDB identityとDLLの対応、PDB内102ソースのチェックサム一致、52配備ファイルのmanifest一致を確認しました。外部AIのパッチは改行が壊れていたため、実ビルド用ソースと全Git追跡ファイルの比較から5対象を取り込みました。再配備はしていません。新しいディレクトリでのビルドと既存バイナリのバイト同一性は別の検証です。

ServerPCは認証済みcapabilities/control statusを読み取り確認しました。今回の新しい監査について、ServerPCの現PID・稼働ソース全hash・DB全行との現地照合は未完了です。過去のチャット記録や古いreceiptを現在の配備identityの証明には使用しません。

## 問題と修正

1. 配備済みの理由記録がGitから追跡できない状態を、実ソース照合と独立コミットで解消しました。
2. 優先譲渡を単純queuedへ戻すだけでは、Serverのterminal Requestを再利用して失敗します。論理JobとAttemptを分け、初回送信前の永続フェンス、認証済みstatus、immutable入力hash、最大3Attemptを導入しました。完了応答を失った場合はstatusから同一結果を回収します。
3. 次Attemptは専用の認証済みretry契約で予約します。旧runがinterrupted、許可された優先譲渡理由、同じ本文・Session、有限な親子chain、既存inference semaphore、単一slotのidle確認を要件とします。slot情報が取得不能なら待機し、モデル起動で代替しません。
4. Host停止・通信断・期限切れ・明示取消を一括で再送しません。旧凍結要求、未対応Server、異なるhash、回復不能な理由は保留します。状態照会は異常時30秒間隔・20回まで。保留状態・監査・一度限りの利用者通知は同一トランザクションです。
5. 704MiBを増やさず、原画素の逐次タイルOCRと小さい罫線プレビューへ変更しました。ONNXメモリ確保失敗、ONNX実行失敗、画像デコード失敗などを固定コードへ分類し、Hostも固定例外カテゴリを保存します。例外本文は一般ログへ出しません。
6. 全ページを一つの格子とする制限を改め、表ごとの罫線・セル・結合を保持します。罫線なしの安定した配置は要確認の推定として別分類し、値・見出し・結合を生成しません。
7. JSONを正本とし、安全な静的HTML、実Markdown table、補助CSVへ投影します。HTMLはOCR文字列をエスケープし、外部リソースとJavaScriptを持ちません。CSVは式を無効化します。
8. 新規成果物も既存のticket/upload/complete配送と固定ID・hash・元チャンネル/スレッドを使用します。uncertainは自動再送しません。追加構造化ファイルが1MiBを超えても既存Markdownを維持し、分割依頼を案内します。
9. Attempt本文・結果を保存容量とSession削除へ含めました。OCR単体24時間とSlack Session30日の違いを維持し、進行中Job・未確定配送を保護します。

## 実測と品質の限界

公開合成のPNG/JPEG/WebP/PDF、3060×4080 JPEG、縮小、EXIF正立、複数表、結合セル、注記、罫線なし表を検証しました。CPU2・同時1・BelowNormal・ネットワークcapability0・704MiB・ページ60秒を使用しました。同じ大画像は旧workerで失敗し、候補は完了しました。最終大画像の観測peak private commitは約556MiB、処理時間は約15.7秒（Host準備を含む）でした。

通常向きと正しいEXIF正立の7ケースで173/173セルの正規化文字列と行列対応が公開正解に一致しました。罫線なしは12セルを含み、常に未検証推定として表示します。これは人工fixtureの限定結果で、実財務資料全般への精度保証ではありません。意図的に90度横向きにした表の意味的復元は未資格です。手書き、歪み、罫線欠損、小さすぎる文字、長い行のタイル継ぎ目、複雑な帳票も未資格です。

JSONにはpage/table/cell ID、行列、rowspan/colspan、polygon/bounding box、元座標への変換、認識スコア、needs_review/unreadableと候補を保持します。構造信頼度の校正値はないためnullと確認情報を使います。候補文字は表示時に `[推測]` を付け、候補がなければ判読不能を残します。

XLSXは今回追加しません。依存追加、セル型、式、結合・幅・書式の独立した資格確認が必要です。JSON/HTML/CSVを既存モデル・資源条件で先に整えています。

## 保存・権限・既存機能

- Slackから取得してローカルOCR/Qwenで処理し、依頼元の許可チャンネル・元スレッドへ返す通信は正規機能です。Cloud AI補正・外部検索・別の相手への転送は追加していません。
- Slack保存済みOCRと成果物は専用DBに複製されるため、元OCRの24時間経過後もSession内で参照できる場合があります。全OCRの一律30日保持は行いません。
- 容量圧迫時は要約キャッシュを先に解放し、7日超の非活動Sessionだけを条件付きで整理します。利用者の外部エクスポートは回収しません。
- Reviewer/人間承認/承認hash、正規Host認証、TLS、Token、AppContainer、Job、外部文書を命令にしない境界を維持します。
- Canonical全20tableおよびSlack専用14tableは現用の全行digest/schema/user_version/integrityを読み取り監査しました。候補はCanonical schemaを変更しません。新しいSlack専用tableの本番作成は承認待ちです。
- ClientPC Saluki128K/CPU6/parallel1/GPU0/KVq8、ServerPC Qwen32K/KVq4は変更していません。実モデル推論・私有画像の追加OCR・新規Slack投稿は行っていません。
- 現用共有スレッドと今回の作成者限定仕様は異なるため、`allowSharedThreads` を明示選択可能にしました。既定は互換維持のtrueで、本番のfalse化は別承認です。

## 再現手順

.NET10 SDKと既存の固定NuGet依存を使用します。通常試験は実行形式の独自harnessであり、VSTest件数として報告しません。

```powershell
dotnet restore dotnet/LocalBrain.ClientHost.Tests
dotnet build dotnet/LocalBrain.ClientHost.Tests -c Release --no-restore
dotnet dotnet/LocalBrain.ClientHost.Tests/bin/Release/net10.0/LocalBrain.ClientHost.Tests.dll
# 追加の本物OCR試験は、productionと独立したhash固定assetsを明示する。
python tools/ocr/tests/make-financial-fixtures.py PUBLIC_FIXTURE_DIR
dotnet dotnet/LocalBrain.ClientHost.Tests/bin/Release/net10.0/LocalBrain.ClientHost.Tests.dll --ocr-bounded-proof ISOLATED_ASSETS PUBLIC_FIXTURE_DIR NEW_EVIDENCE_DIR
python tools/ocr/tests/test_bounded_layout.py
```

assetsのprovisioningは別作業です。推論中にmodel/依存をダウンロードしません。再ビルド時はSDK・依存・source HEAD・PathMap・成果物hashを固定して記録してください。Server側の公開回帰と同じcanonical契約fixtureを両repoに保存しています。

## 本番反映の推奨順序（未承認・未実行）

1. ServerPCの現地source/runtime/PID/起動時刻/receipt/schemaを照合し、未知差異を解決する。
2. Server retry契約を先に固定候補として配備し、Gatewayだけを保守時間に通常再起動する。既存設定・モデル・鍵・本番知識は維持する。
3. ClientPCの新Runtimeと新workerを専用ディレクトリへstageする。現用の設定・モデル・依存hashを保持し、fixture用ACLは流用しない。
4. 全呼出元のidle、所有PID/birth、launcher/task定義、空き容量、完全なDB論理digest、旧/new成果物hash、専用ネットワーク遮断をguardする。
5. 固定した2Hostの通常停止・切替・再起動とSlack専用DDL追加、OCR設定のworker hash更新を別承認で行う。認証とCanonicalは変更しない。
6. 公開fixtureで実Gatewayの終了確認とslot API互換、優先譲渡・再開・二重返信防止、実Slack成果物を確認する。私有画像は追加の目的限定承認後だけ使う。
7. 不明・busy・hash差異では現状保持。過去の「旧版へ戻さない」決定を尊重し、自動rollbackやDB復元/削除を行わない。将来rollbackが必要なら、候補DDLと旧版互換を独立確認して別承認を得る。

現時点の完了範囲は監査・隔離候補・公開検証・Git/PR整理です。本番の改善完了とは扱いません。未確認の現地照合・実Gateway/実Slack往復・私有財務表品質・Session方針決定は、必要な追加条件を満たしてから行います。証跡、バックアップ、公開出力は作業資料側の `output/audit/integrated-localbrain-20261023` に保全し、私有監査資料をGitへ混入しません。

Reproducible packaging: on a clean checkout, use `tools/Build-ReproducibleClient.ps1 -OutputDirectory NEW_EXTERNAL_DIRECTORY` after the explicit restore step. It requires the recorded SDK, maps source paths, refuses dirty/source drift/existing output, and emits a hash manifest without production configuration. Compare two independent output manifests; qualification of deployed bytes still requires running the test harness against that exact published DLL.

追加の公開故障試験では、旧大画像経路で生じたRapidOCRのラッパー例外を実測し、`ONNX_MEMORY_ALLOCATION` / `OnnxMemory` がHost receiptまで保存されることを確認しました。例外本文・tracebackは一般ログへ出していません。再開候補の状態照会だけでなく、入場拒否の再照会も30秒間隔・20回を上限にし、推論Attemptは最大3回です。
