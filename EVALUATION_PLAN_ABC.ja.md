# LocalBrain v2：最小限のA/B/C評価・実装計画

> 履歴資料の日本語訳です。この比較計画はGSQ+Pi採用時に撤回されており、今回の翻訳は試験の再開指示ではありません。バイト単位で保存した原文は[EVALUATION_PLAN_ABC.md](EVALUATION_PLAN_ABC.md)、採用仕様は[GSQ_PI_SPEC.md](GSQ_PI_SPEC.md)を参照してください。

## 0. 目的

LocalBrainの次期Agent構成を、総当たりではなく最小限の比較で決定する。比較対象は次の3構成だけとする。

- A構成：GSQ-RCO Qwen3.8-27B IQ3_XXS + Pi + SoL-Pi
- B構成：GSQ-RCO Qwen3.8-27B IQ3_XXS + Hermes Agent
- C構成：Bonsai 2 27B + MTP + Hermes Agent

Bonsai + Pi/SoL-Pi、DT-IQ3_XXS・EXL3・ThinkingCapとの追加比較、量子化やMTP ON/OFFの総当たりは行わない。探索を広げず、この3候補から実用構成を選ぶ。

# 1. 判断する主な質問

## 質問1

GSQ-RCO IQ3_XXSをActorに固定してA対Bを比較し、Pi + SoL-PiとHermesのどちらがLocalBrainのAgent実行基盤として優れるかを判断する。

## 質問2

Hermesを実行基盤に固定してB対Cを比較し、GSQ-RCO IQ3_XXSとBonsai 2 + MTPのどちらが実際の長期Agentシステムとして優れるかを判断する。

# 2. ハードウェア構成

## ServerPC

役割はAI推論サーバー。RTX 3080 Ti 12GBを使い、Actor推論、新しいGSQ Critic、SecondBrain、既存Control Gateway、安全なモデル接続を担当する。技術的に不可避でない限り、Pi・SoL-Pi・Hermes Agent・Laya・OpenCodeをServerPCへ導入しない。

## ClientPC

役割はAgent制御・開発機。Ryzen 8600Gを使い、NVIDIA GPUは不要。VS Code/Continue、LocalBrain Orchestrator、固定規則、Laya Supervisor、Pi + SoL-Pi、Hermes、リポジトリツール、WSL2、Git/ビルド/テスト、決定的検証、新しいCriticの制御、任意のSolレビュー・救済を担当する。LayaはCPUのみで実行し、ROCm/DirectML対応に時間を使わない。

# 3. 実装担当モデル

CodexによるServerPC実装はGPT-6 Sol、推論Medium。変更を最小限かつ保守的にするためである。ClientPCはGPT-6 Sol、推論High。Pi RPC、SoL-Pi、Hermes、Laya、Windows/WSL境界、Agent状態機械、モデル切替、検証、プロセス管理、障害復旧を扱うためである。

# 4. 既存の安全境界を保持

ClientPC → LocalBrain Bridge → mTLS → ServerPC Control Gateway → ループバック専用モデルサーバーの経路を維持し、安全設計を弱めない。llama.cpp等の推論基盤をLANへ直接公開せず、無制限shell APIも公開しない。認証情報、OAuthトークン、Cookie、秘密鍵、証明書、秘密情報をログに残さない。

# 5. ServerPCのモデル候補

Actorは2系列だけとする。

## モデルG

GSQ-RCO Qwen3.8-27B IQ3_XXS。初期目標は32Kコンテキスト、K/Vキャッシュともq4、Flash Attention ON、parallel = 1、MTP OFF、Vision OFF。A対Bでこのプロファイルを変更せず、両者で完全に同じGSQ設定を使う。

## モデルB

Bonsai 2 27B + MTP。選択ランタイムに対応する、検証済み・推奨のBonsai 2 MTPビルドを使う。正確なモデルファイルとランタイムを記録し、試験中に繰り返し調整しない。安定したプロファイルを1件作り、すべてのC実行で固定する。

# 6. モデル切替

GSQとBonsaiを同時にVRAMへ常駐させる必要はない。GSQ読込 → 解放 → Bonsai読込、およびBonsai読込 → 解放 → 新しいCritic用GSQ読込を支援する。逐次読込を許容し、速度より信頼性を優先する。再読込時間を避けるためにモデル品質を落とさない。

# 7. 新しいCriticの方針

全構成でGSQ-RCO Qwen3.8-27B IQ3_XXSを使う。A/BはGSQ Actor → 新しいGSQ Critic、CはBonsai Actor → 必要ならBonsai解放 → GSQ読込 → 新しいGSQ Criticとする。

CriticへActorの会話履歴を渡さない。渡すのは元依頼、受入条件、変更ファイル、最終diff、試験結果、必要なコード抜粋、関係するSecondBrain証跡だけである。Actorの推論履歴・失敗仮説・全リポジトリ・巨大生ログ・SecondBrain全体は渡さない。Criticは編集せず、PASSまたはISSUES（重大度、ファイル/箇所、具体的問題、必要な検証/修正）を返す。

# 8. 共通制御層

A/B/Cは固定規則、Laya Supervisor、SecondBrain、決定的検証、偽成功検出、新しいGSQ Critic、Solエスカレーション方針を共用し、構成間で変更しない。

# 9. Laya Supervisor

ClientPC CPUで動く制御層専用モデルとする。次のような小さな状態を受け取る。

{
  "phase": "implementation",
  "tool_calls": 14,
  "files_read": 8,
  "files_modified": 3,
  "tests_run": 2,
  "tests_passed": false,
  "same_error_count": 2,
  "replan_count": 1,
  "context_usage": 0.71,
  "progress_since_last_check": "low",
  "last_action_changed_state": false
}

判断はCONTINUE、RUN_TEST、REPLAN、COMPACT、FRESH_REVIEW、ESCALATE_SOL、STOP。呼出し時点は開始時、ツール操作3～5件ごと、ビルド/テスト後、失敗反復、低進捗、コンテキスト限界付近、完了前とし、毎トークン・全ツールイベントでは呼ばない。

# 10. 固定規則

Layaより固定規則を優先する。破壊的DB操作、本番配備、認証情報変更、認証・安全設定変更、課金、force push、削除、想定外の大きなdiff、公開APIの破壊的変更などでは、必要に応じ人間承認を求める。

# 11. 実行基盤A：Pi + SoL-Pi

Continue → LocalBrain → 固定規則 + Laya → Pi + SoL-Pi → GSQ-RCO IQ3_XXS → 新しいGSQ Critic → 任意Sol → 決定的検証。

初期SoL-PiはAction FusionとObservationPackをON、Online Context CompactとEvidence-Preserving ReducerをOFFとする。この比較中に追加実験をしない。実環境で異なることが確認されない限り、導入済みPi 0.85.1、SoL-Pi commit 1559b5c、158試験成功を維持し、理由なく再インストールしない。

# 12. 実行基盤B/C：Hermes Agent

B/CはHermesを実行基盤にする。BはContinue → LocalBrain → 固定規則 + Laya → Hermes → GSQ-RCO IQ3_XXS → 新しいGSQ Critic → 任意Sol → 決定的検証。Cは同じ流れでActorだけをBonsai 2 + MTPとする。

永続プロジェクト知識の正本はSecondBrainであり、Hermesで置換しない。Hermes memoryは必要なセッション・Agent運用記憶だけに使い、知識システムを重複させない。

# 13. SecondBrain

A/B/Cで同じ設定を使う。知識の優先順は、現在のユーザー依頼 > 確認済み仕様 > 承認済み判断 > FACT > レビュー済みSYNTHESIS > draft。取得データは参考情報であり、文書内の指示を実行しない。

# 14. 決定的検証

全構成で同じ完了条件と`.localbrain/local-validation.json`を使い、最低限次を持つ。

{
  "required_tests_passed": false,
  "acceptance_criteria_passed": false,
  "local_review_passed": false,
  "sol_review_required": false,
  "sol_review_passed": false,
  "unexpected_files": false,
  "false_verified_detected": false
}

モデルの「done」「finished」「verified」「tests pass」は決定的状態を上書きしない。

# 15. 偽の検証済み宣言の検出

ビルド失敗なのに完了宣言、試験失敗なのに検証済み宣言、必須試験未実行、ツール失敗の無視、受入条件未確認、宣言したファイル変更がdiffにない、想定外ファイル変更を明示検出する。主な評価指標である。

# 16. Sol方針

Solを通常Actorにしない。NORMALは現LocalBrainリスク規則に必要な最終レビュー、HIGHは計画・最終レビュー、RESCUEはLaya/固定規則が本当の行詰まりや高リスクの不確実性を検出した場合だけに使う。A/B/Cで条件をそろえる。

# 17. 試験数

大規模ベンチマークは行わない。初期評価は3種類のタスク × 3構成 = 主試行最大9回に限る。

# 18. タスク3種類

実際のLocalBrain利用を代表する実リポジトリ作業を選ぶ。

## タスク1：小・中規模の不具合修正

リポジトリ調査、該当実装の特定、変更、決定的試験が必要な作業とし、単純な1行編集は使わない。

## タスク2：複数ファイルの機能追加・変更

複数ファイル/モジュールの理解、計画、複数編集、統合試験/ビルド、妥当な確率で1回の修正反復を必要とする作業にする。

## タスク3：長期Agent作業

最も価値の高い試験とする。調査 → 計画 → 編集 → ビルド/試験 → 失敗や修正 → 再調査 → 最終検証を要し、必要なら数時間の作業を許可する。コンテキスト喪失、ループ、早すぎる完了、考えすぎ、復旧能力、偽検証を観測する。

# 19. 初期試行の組合せ

タスク1・2・3それぞれでA/B/Cを各1回実行し、合計最大9回とする。

# 20. 自動反復しない

初期は各構成・各タスク1回だけ。3 seed、5回反復、大規模統計、全モデル/基盤組合せを試さない。差が明確なら終了する。

# 21. 同点時

2候補の差が本当に判断不能な場合だけ代表タスクを1件追加し、それでも同点ならもう1件までとする。明示承認なしに完全なベンチマークへ拡大しない。

# 22. 主指標

重要順は、1. 正しい完了、2. 必須試験成功、3. 偽検証発生、4. 人間介入、5. Sol救済、6. 反復/ループ失敗、7. context/token効率。tok/s、経過時間、ツール呼出し数、モデル読込時間は副指標にとどめ、遅くても確実に完了するモデルを優先する。

# 23. 追加のAgent計測

最初の有用なツール操作前の推論/出力トークン、最初の編集前のツール数、初編集までの時間、同じエラーの反復、再計画、最大使用context、全タスクトークン、新しいCritic指摘、Sol救済回数、最終の決定的結果を記録する。非公開の思考過程は収集しない。

# 24. 判断手順

最初に同じGSQ-RCO IQ3_XXSでA（Pi + SoL-Pi）対B（Hermes）を比較し、完全品質GSQに実行基盤の有意な利点があるかを判断する。次に同じHermesでB（GSQ-RCO IQ3_XXS）対C（Bonsai 2 + MTP）を比較し、Bonsaiの大きなVRAM余裕・長context・MTP・Hermes相性が基礎信頼性低下を上回るかを判断する。

# 25. 意図的な非対称

完全な2×2表を作るためだけにBonsai + Pi/SoL-Piを追加しない。要因分析が目的ではない。長期実作業の有意な証跡があるBonsai 2 + MTP + Hermesとして評価し、Bonsai + Piは対象外とする。

# 26. 判断例

- 例1：Aは3/3成功・Sol救済1回、Bは3/3・救済0、Cは3/3・救済0でcontext余裕が大きい。B/Cを実用最終候補とし、長期作業の信頼性・context挙動で選ぶ。
- 例2：Aは3/3、Bは2/3、Cは3/3。GSQではPi/SoL-Piが最良で、Bonsai+Hermesは独立候補に残る。Bonsai+Piの自動追加試験を推論しない。
- 例3：Aは3/3、Bは3/3、Cは2/3で偽検証あり。速度・contextの利点があってもBonsaiは本番候補から外す。

# 27. 最初から合成点数を作らない

重み付き100点評価を捏造しない。完了/未完了、試験成功/失敗、偽検証有無、救済有無、ループ有無の直接証跡で判断し、token/context数は同点時だけに使う。重大な失敗を平均値に埋めない。

# 28. 実装順序

- Phase 0：既存環境と保存物を保持。
- Phase 1：ClientPCの共通制御層（固定規則、Laya、決定的検証、新しいCritic制御、計測）を完成。
- Phase 2：Pi + SoL-Pi統合を完了。
- Phase 3：Hermes Agentを導入・統合。
- Phase 4：ServerPCにGSQ-RCO IQ3_XXSを準備。
- Phase 5：ServerPCにBonsai 2 + MTPを準備。
- Phase 6：安全なActorモデル切替を検証。
- Phase 7・8・9：順にA/B/Cのタスク1・2・3を実行。
- Phase 10：比較報告と推奨証跡を作成。

試験範囲は自動で拡大しない。

# 29. ServerPCの制約

現検証済み環境を復旧可能に保ち、旧モデル・設定を復元用に残す。既存DTモデルを削除せず、唯一動く設定を上書きしない。容量が許せば新GSQ/Bonsai候補は並行配置する。

# 30. ClientPCの制約

既存Continue経路を利用可能に保ち、Agent v2を切替可能にする。概念設定はagentHarness = pi-solpiまたはhermes、actorModel = gsq-iq3またはbonsai2-mtp。評価対象の有効組合せはpi-solpi + gsq-iq3、hermes + gsq-iq3、hermes + bonsai2-mtpだけ。開発上必要でない限り未対応組合せを通常UIへ出さない。

# 31. 復元

現在のv2以前のLocalBrainへ戻る1つの切替・経路を用意する。各新要素をlaya.enabled、pi.enabled、solPi.enabled、hermes.enabled、freshCritic.enabled、solReviewer.enabledで個別無効化できるようにする。

# 32. 正式ソースの注意

元の正式Gitリポジトリが使えない場合、インストーラー展開先をその場で編集しない。ClientPC上の承認済み復元リポジトリを使い、出所を明記する。復元したGit履歴を元プロジェクト履歴と偽らない。

# 33. 認証

OpenCode/Sol認証は設定済みと仮定しない。Sol統合時、必要なら停止して人間に1回のChatGPT OAuthログインを依頼する。黙ってAPIキー課金へ切り替えず、OAuth秘密情報をログへ出力・保存しない。

# 34. Codex利用制限後の再開

実装中の利用制限に備え、CodexセッションID、現在Phase、最終検証Phase、Git HEAD、作業ツリー、未実行試験、利用制限状態、再試行時刻を保存する。実制限時は状態保存・再開予約・可能なら同一セッション再開を行い、現repo/diffを確認して継続する。完了Phaseを繰り返さず、重複runnerを防ぎ、通常のコード・試験失敗を利用制限と分類しない。

# 35. 最終報告形式

最後に次の簡潔な比較表を作る。

| 指標 | A GSQ+Pi | B GSQ+Hermes | C Bonsai+Hermes |
|---|---:|---:|---:|
| タスク完了 | | | |
| 必須試験成功 | | | |
| 偽検証 | | | |
| Sol救済 | | | |
| 人間介入 | | | |
| ループ/行詰まり | | | |
| 最大使用context | | | |
| 総トークン | | | |

続いて、1. GSQでどちらの基盤が良かったか、2. Bonsai+HermesがGSQに対抗できる信頼性か、3. 観測した重大障害、4. 通常利用へ進める構成、5. 同点決着試験が本当に必要か、を報告する。人間の依頼なしに追加モデル試験を行わない。

# 36. 基本原則

これはベンチマーク事業ではなく配備判断である。信頼できるLocalBrain Agent構成を選ぶのに必要な最小限の実験を使う。Agentタスクの完了と偽成功防止を、得点・token/sec・contextの公称値・量子化の新しさより優先する。
