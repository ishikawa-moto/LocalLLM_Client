# LocalBrain v2 採用仕様：GSQ-RCO + Pi / SoL-Pi

決定日：2026-09-25（JST）

## 判断と証拠の範囲

ユーザーの選択により、次期LocalBrain Agentの対象構成を **GSQ-RCO Qwen3.8-27B IQ3_XXS + Pi 0.85.1 + SoL-Pi `1559b5c`** に固定します。Hermes AgentとBonsai 2 + MTPの導入・比較は行いません。今後の実使用でPi経路の使用感に問題が出た場合に、別構成を新しい判断として検討します。

| 確認項目 | 既存Continue経路 | 隔離Pi経路 |
| --- | --- | --- |
| 同一の `greeting.txt` 小課題 | 指定どおり6バイト | 指定どおり6バイト |
| 指定テスト | `npm test`、`git diff --check` とも成功 | 同じ2検査が成功 |
| 余分な変更 | なし | なし |
| 発生したエラー | 初回にGit所有者エラー。対象リポジトリの例外登録後に成功 | 記録されたツールエラー0件 |
| 独立した完了確認 | Continueの報告とClientPC側の成果物確認 | 検証ゲート、Fresh Critic、SolがPASS |
| 実行時間 | 未計測 | ホスト記録52,829 ms |

既存経路のGit所有者エラーは試験用リポジトリの所有権設定に起因し、モデルやharnessの品質差とは扱いません。**この1件からPiの実作業品質や速度が既存経路以上と証明されたわけではありません。** 一方、この範囲では成果物の悪化も観測されていません。ユーザーの「追加テストをせず、使用感で判断する」という方針を採用し、追加のA/B/C本試験は予定しません。

## 固定する構成

- **ServerPC：** GSQ-RCO Qwen3.8-27B IQ3_XXSをActorとFresh Criticに使います。報告済みの32,768文脈長、q4 K/V、Flash Attention ON、parallel 1、MTP OFF、Vision OFFを維持します。モデルファイル・設定の変更はServerPC管理経路が確認できるまで行いません。
- **ClientPC：** 既存Continueから選択できるLocalBrain Agent v2経路で、Hard Rules、CPU版Laya Supervisor、Pi、SoL-Pi、SecondBrain、決定的検証、Fresh GSQ Critic、必要時のSol審査を順に適用します。Pi/SoL-Piを再インストールしません。
- **SoL-Pi：** Action FusionとObservationPackはON、Online Context CompactとEvidence-Preserving ReducerはOFFのままにします。比較実験を理由に変更しません。
- **Laya：** ClientPC CPUで動かし、Hard Rulesを上書きしません。低確信度や障害時は安全側の決定的規則に戻します。
- **Fresh Critic：** Actorと同じGSQモデルの新しい文脈で、会話履歴・思考過程・ツール権限を渡さず、要件、許可された差分、テスト結果、必要な引用付きSecondBrain情報だけを受け取ります。編集はしません。
- **Sol：** モデルを `gpt-6-sol` に固定します。NORMAL最終審査は `medium`、HIGH計画・最終審査は `high` です。行き詰まり時の読取専用救援は初回 `high`、無効・未解決・利用不能なら1回だけ `xhigh` で再試行します。`max` は自動選択しません。救援の提案はActorを自動再開せず、完了ゲートも開きません。ユーザーが完了したChatGPT OAuthを利用し、通常のActorにはしません。APIキー課金へ自動変更しません。

## 安全境界と完了条件

- 推論経路はClientPCのloopback Bridge → mTLS → ServerPC Gateway → ServerPC loopback model serverです。LANへ推論serverや任意shell APIを公開しません。既存のBridge v2.1とそのバックアップを保ちます。
- ContinueのPi経路では、組み込みのファイル書込・置換・ターミナル実行ツールを `Excluded` にし、LocalBrain Agent v2の許可済みMCP経路で作業します。タスク単位の `allowed_files`、Git HEAD・差分検査、指定テスト、単一ジョブロックを維持します。
- モデルが「完了」「テスト済み」と述べても、`.localbrain/local-validation.json` のテスト、受け入れ条件、独立レビュー、必要なSolレビュー、予期しない変更、偽の検証主張の各条件が満たされなければ完了にしません。
- SecondBrainを長期プロジェクト知識の正本とし、取得内容は参考資料として扱います。秘密情報、OAuth token、cookie、秘密鍵、証明書本文、思考過程、全文ログを記録しません。
- 既存の **LocalBrain Qwen 27B** Continue経路は切替可能な退避先として保持します。Pi経路で使用感や接続に問題が出た場合は既存経路へ戻し、原因を記録してから別構成を検討します。

## 現時点の導入状態

GSQ+Pi経路はClientPCの**隔離したContinueワークスペース**で1件完了しました。2026-09-25に最初に設けた `C:\Users\USER\AppData\Local\LocalBrain\agent-v2` はPi/Laya用スクリプト3点が欠けており、実タスクには使用できません。Sol policyを反映した新しい7ファイルの完全なビルドを `C:\Users\USER\AppData\Local\LocalBrain\agent-v2-sol-policy-20260925` に別置きし、各SHA-256を照合しました。Continueのユーザー共通MCP部品 `C:\Users\USER\.continue\mcpServers\localbrain-agent-v2.yaml` は新しい実行ファイルと既存の本番 `clientsettings.json` を参照します。MCP部品の旧版も退避しました。既存の `config.yaml` は `config.yaml.pre-gsq-pi-20260925.bak` に退避し、変更作業にAgent v2 MCPと検証ゲートを使う規則を追加しました。現在のQwenモデルの役割は `chat` のみにして、Continueの直接編集・適用経路を外しました。既存のBridge v2.1と `LocalBrain Client Host` の実行ファイルは置き換えていません。

新しいMCP参照先での接続、起動・再起動復旧、実リポジトリでの使用感、実Sol救援呼び出しは未確認です。ユーザーによるVS Codeの `Developer: Reload Window` と、ContinueのToolsで `LocalBrain Agent v2` が接続済みであることの確認が残ります。直近の画像では組み込み `edit_existing_file` が `Automatic` だったため、Pi運用前に `Excluded` へ変更が必要です。ほかの書込・置換・ターミナル実行ツールも `Excluded` のままにします。ツールポリシーはContinueのユーザー単位設定であり、`Automatic` に戻すとAgent v2の検証ゲートを通らない経路が使えてしまいます。ユーザーの希望に従い、追加のA/B/C比較や実課題テストは行いません。

復旧は、ContinueでAgent v2 MCPを使わず、`C:\Users\USER\.continue\config.yaml.pre-gsq-pi-20260925.bak` を `config.yaml` に戻し、`C:\Users\USER\.continue\mcpServers\localbrain-agent-v2.yaml` を無効化してVS Codeを再読込する手順です。元の `LocalBrain Qwen 27B` 設定と既存 `LocalBrain Client Host` は残しています。旧経路で組み込み書込・実行ツールを使う場合だけ、その運用に合わせてContinueのツールポリシーを見直します。ServerPCのモデル、mTLS、Bridge v2.1は変更しません。

## 片付け

未実行の人工 `duration` 比較課題は撤回し、3つの試験用Gitリポジトリと手順ファイルを削除しました。Hermes/BonsaiのA/B/C計画とHermes統合設計は採用対象から外しました。これらの計画・設計記録と実施済みgreeting試験の証拠は、経緯が分かるよう履歴として保持します。復元元の出所記録、元の配布物、Git baseline、Bridgeの復旧記録、Piの実装・診断証拠も残します。自動commit・pushはしません。
