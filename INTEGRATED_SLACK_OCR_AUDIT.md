# Slack/OCR 統合・本番検証の到達点（2026-10-24）

本番切替、必要な追加DB移行、GitHub/ローカルmain統合と、確認済みの不要実行環境の整理を実施しました。下部の「監査候補時点の記録」は履歴であり、現在の未配備・未承認を意味しません。

| 機能 | ソース・隔離検証 | 本番反映・実動確認 | Git |
|---|---|---|---|
| 優先譲渡の固定原因コード | 実装・公開fixture検証済み | 既存配備版からソースへ正式統合 | 原因記録bfae367、Client PR8以降 |
| 120秒上限・有限Attempt・安全な復旧 | 状態/hash/終端/排他・通常回帰PASS | 認証済みcapability120、実Qwenで前景割込み→旧Attempt終端確認→認証retry→完了を確認 | Server PR4/6/7/8、Client PR8/9 |
| 大画像の逐次タイルOCR・固定エラー分類 | 本物のOCR/Windows Job/AppContainer、公開高解像度fixtureで完了 | 新worker配備済み。小JPEGは本番完了。高解像度の本番低負荷完了は未資格 | Client core c4aa6a58f9e9 |
| 表構造・結合セル・安全な成果物 | JSON正本からHTML/Markdown/CSV、公開セル/座標検証 | 実Slack公開圧縮画像で2表と結合を保持。数値12/12、全セル21/23一致。日本語2セルの誤読あり | Client PR8/9/13 |
| Slackのファイル配送 | MD/JSON/HTML/CSVのticket→upload→complete、再起動/重複/応答喪失fixture | 同じ元スレッドへMD/JSON/HTMLを各1回送信確認。新形式の古いファイル名validator拒否を実試験で発見し修正・本番反映 | PR13 source92db181、main統合1cd02e3 |
| データと起動経路 | Canonical全20表、Slack schema・非対象行、task/source/hashを照合 | 必要な追加schemaだけを適用。旧DB復元・初期化なし。削除後も両DB論理hash・現用104製品hash不変 | 両repository main統合済み |
| 機能を保った整理 | 現用の確認UI・採用operator・SQLite依存への代替を検証 | 旧モデル/重複asset約11.9GBと旧runtime38件1454ファイル約1.47GBを削除。現用モデル/認証/DB/小さい監査証跡は保持 | マージ済み作業branch/worktreeも整理 |

## 実行版とGitの関係

`git pull`はソースを更新します。稼働プロセスはmanifest付きの不変releaseから起動するため、マージやpullだけで置き換わりません。配備されたコードのcommit、Git main、後から変更した配備ツールや文書のcommitは分けて記録します。

2026-10-24の確認時点で、Server application releaseはdcc046a、Server mainはdbe7343です。後者は配備・前進復旧helperの追加を含み、applicationコードの不一致を隠すものではありません。Client Bridge/Agent/OCRはc4aa6a58f9e9、Slack受付だけは新形式の配送修正92db18114464です。installation receiptのrole_releasesとSlack receiver receiptに両者を明示し、同じ製品DLLへの一括切替を装いません。Slack修正のために正常なBridgeやモデルを再起動していません。

## 確認できた範囲と制限

- 旧実行DLLを隔離ロードして、新形式2件がHTTP前のローカル検証で拒否されたことを直接確認しました。この公開ジョブの固定2成果物だけを排他内で復旧し、送信済みMD・無関係の依頼・真に通信結果不明の配送は再送していません。
- 既存配送28チェック、追加の全形式配送41チェック、限定DB復旧11公開fixture、Client通常実行harness全体、Server通常239と復旧/配備guard20/37がPASSです。独自harnessをVSTest件数と誤記しません。
- 最初の通常向き/EXIF正立7公開fixtureは173/173セル一致でした。実Slackの圧縮版では21/23セルに低下しました。全ての実資料への精度保証はできません。needs_reviewと未検証表示、判読不能/推測の区別を維持します。
- 高解像度3060x4080 JPEGはCPU2/704MiB/通信0で隔離完了しましたが、この負荷試験のadmission50と本番25は異なります。本番の重要処理優先・30秒backoffを緩めず、高解像度の本番完了を偽って報告しません。
- 横倒し90度、手書き、縦書き、歪み、複雑な帳票、極小文字、全てのタイル境界は未資格です。XLSXは今回追加していません。
- 実複数人Slack往復・新しい私有財務画像・実際の私有SecondBrain採用を、この公開検証の結果に混同しません。現在の明示設定では共有スレッドを維持し、作成者限定は別設定として残します。
- OCR単体の既定24時間とSlack Sessionの30日は別です。Slackの保存済み添付・成果物がSessionに残る場合は元OCRの期限後も参照可能ですが、全OCRを30日保存する変更はしていません。

Host認証/TLS、Slack Token、パスワード方式、OCR AppContainer/通信遮断/CPU2/704MiB/60秒、本番CPU25、Qwen32768/KVq4、Saluki131072/CPU6/parallel1/GPU0/KVq8を保持しました。許可済み元Slackへの結果返却は正規機能です。Slack/OCR/私有知識をCloud AIや外部検索へ送信する経路は追加していません。

再現手順は`tools/Build-ReproducibleClient.ps1`と`tools/deployment/README.md`を参照してください。正式配備・公開動作・整理・全hashの機械可読証跡は作業資料側の`output/audit/production-integration-20261023`、元の隔離検証は`output/audit/integrated-localbrain-20261023`に保持し、私有証跡や設定を公開Gitへ持ち込みません。

---

## 監査候補時点の記録（2026-10-23、以下は履歴）

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

ServerPCは認証済みcapabilities/control statusを読み取り確認しました。承認済みの新しい現地監査では、ServerPCのGateway PID/birthと配備receipt、追跡103ファイル・設定23ファイル・設定上の依存59ファイルを照合しました。出力長を考慮した4ファイルの未コミット差分を保全しています。ただし通常権限でSYSTEMの実行image/command lineは読めず、新規loaded-image照合やServer全DB全行照合、実機統合は未確認です。cryptographyの宣言50.0.1とインストール50.0.2の差も報告し、本番依存は変更しません。

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

## 現地監査後の実行状態契約の統合

Serverの取消受理と終端確定を直列化し、終端の上書きを禁止しました。登録は終端保存まで保持し、処理中または所有不明の予約をrunning/unresolvedとして返します。制御statusのrestricted_activityは件数だけの観測で、新規受付を止める保守ロックではありません。既存のquiescent-attempt-v1とソケット取消保護を維持したServer全187公開回帰が成功しました。

Clientは直接応答のrunning/unresolvedも失敗扱いせず、既存の永続progressにserver_nonterminalを固定記録し、同じrequest/hash/Sessionのstatus照会へ移ります。新規ID再送は行いません。公開インメモリGatewayの26確認で、保留・再照会・一度だけの送信・完了結果回収と保存済み結果の再利用を検証しました。実Gateway/実Slackの新契約は未資格です。
追加の実行状態契約を含むClient通常harness全体も終了コード0で成功しました。独自harnessのためVSTest件数とは扱いません。
