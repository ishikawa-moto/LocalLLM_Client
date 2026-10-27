# ClientPC Agent v2のReviewer振り分け

> 記録した時点の候補・検証・配備状況を保持した日本語版です。過去の未確認事項や手順を、現在の残作業・実行指示と混同しないでください。現在の状態は実配置の記録・設定・プロセスと照合します。

このソース実装は、既存の指定テストと新しいローカルQwen Criticの後にAgent v2を拡張します。ServerPC、Bridge v2.1、Pi/SoL-Pi、Continueのツール方針、既存Solのモデル・推論設定は変更しません。このリポジトリをビルドしても、配置済みContinue MCP Hostは変更されません。

## 経路と完了規則

- `reviewer-routing.json`で低信頼度の閾値（現在0.65）とAstra比較の最大回数（現在6回）を定めます。方針が不正または存在しない場合は安全側で停止します。
- Layaは小さなメタデータだけの状態を受け取り、`copilot`、`sol`、`both`を提案します。LOWタスクでは従来どおり外部レビューを使いません。信頼度が低い場合やLayaの予測を利用できない場合は両方を選択します。HIGHでは常にSolが必要で、十分な信頼度で`both`が選ばれた場合はCopilotも呼びます。
- SolとCopilotには独立した証跡を渡し、互いの回答は渡しません。選択されたどちらかが妥当な問題を示せば完了を止めます。NORMALでは利用不能なプロバイダーから他方へ切替可能ですが、HIGHはSolなしで通過できません。プロバイダーのエラーをコード品質のPASSやISSUESとして扱いません。
- 既存のHIGH承認、Sol計画レビュー、指定テスト、新しいCritic、Actorターン上限、チェックポイント確認は維持します。旧`awaiting_sol_review`チェックポイントも読み取れます。

## プロバイダーの境界

- Solは既存のChatGPT OAuth認証を使うOpenCode経路で`openai/gpt-6.1-sol`を維持します。NORMAL最終レビュー、HIGHの計画・最終レビュー、2回までの救済相談はいずれもhighです。
- Copilot CLIは`claude-opus-5.5`と`--reasoning-effort high`に固定します（2026-10-18）。このReviewerでモデルの自動選択は使いません。既存のHostジャーナル用アダプターの制約は維持します。Copilot CLIは上限付きレビューパケットだけを置いた一時ディレクトリを使います。`--available-tools=view`を指定し、書込・shell・memoryを拒否し、組込MCP・リモートセッション・カスタム指示を無効にします。ツール許可をユーザーに求めません。作業ディレクトリ経由で元のGitワークスペースを見られません。回答は上限付きPASS/ISSUESオブジェクトとして解析できる必要があり、プロバイダー障害はUNAVAILABLEです。
- 共通の証跡構築処理は、CopilotまたはAstraを呼ぶ前に、大きすぎるパケットと判別可能な認証情報を拒否します。どのReviewerにも実装担当の会話は渡しません。プロバイダーの標準エラーは記録せず破棄します。
- Astraはメタ評価専用です。自然に発生した妥当な二者レビューを最大6回比較し、既存Sol・Copilotの指摘から上限付きのローカル重みを0.05だけ動かせます。新しいコード上の指摘を追加したり、タスクを承認したり、完了条件を解除したりできません。結果は`%LOCALAPPDATA%\LocalBrain\reviewer-calibration.json`に回数と重みとして保存し、レビューパケットは保存しません。公式Codex CLIの`gpt-6-astra`、読み取り専用の一時セッション、既存ChatGPT認証を使います。APIキーへの代替経路はありません。

## 検証と現在の制限

.NET Releaseビルドとローカルの振り分け・完了条件の確認は成功しています。合成データによるCopilot CLIレビューは非対話モードで成功しました。既存ChatGPT認証のCodex CLIを使う`gpt-6-astra`の合成メタ比較も完了しましたが、最初はUNAVAILABLEとなり後の呼出しで成功したため、一時的な障害は起こり得ます。Layaの振り分け確認では信頼度0.0146で`both`が返り、保守的な設定により両プロバイダーへ振り分けられます。Astra比較で低信頼度の規則は上書きできず、有効なサンプルが十分に集まった後の境界付近のNORMAL選択だけを調整します。新しい隔離GitタスクはActor 1ターンで完了し、指定テスト、新しいCritic、Sol、Copilot、Astra比較はすべて成功しました。完了済みの旧試行タスクは再実行していません。

フレームワーク依存Hostは`C:\Users\USER\AppData\Local\LocalBrain\agent-v2-reviewer-routing-20260928`へ配備しました。発行した12ファイルすべてのSHA-256とサイズが事前配置と一致しました。ContinueのMCP YAMLは新しい実行ファイルを参照し、元の内容は`C:\Users\USER\.continue\mcpServers\localbrain-agent-v2.yaml.reviewer-rollback`へ保存しました。新しい実行ファイルでMCPの`initialize`と`tools/list`（3ツール）が成功し、ユーザーのVS Code再読み込み後に実MCPプロセスが新版を使いました。

この版の復元手順は、`localbrain-agent-v2.yaml.reviewer-rollback`を`localbrain-agent-v2.yaml`へコピーしてVS Codeを再読み込みするものです。以前の実行ファイルは`C:\Users\USER\AppData\Local\LocalBrain\agent-v2-context-telemetry-20260926\localbrain.exe`にあります。既存BridgeとClient設定は変更しません。既存チェックポイントを保持し、完了済みタスクを再実行・再開しません。
