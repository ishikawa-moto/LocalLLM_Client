# LocalBrain v2：A/B/C計画への切替記録

> **履歴資料。** このA/B/C計画は本試験を実行する前に撤回されました。現行の採用仕様は [GSQ_PI_SPEC.md](GSQ_PI_SPEC.md) です。以下は当時の判断経緯として保存しています。

更新日：2026-09-25（JST）

当時のA/B/C計画は [EVALUATION_PLAN_ABC.md](EVALUATION_PLAN_ABC.md) です。ユーザー添付ファイルをバイト単位でコピーしたもので、SHA-256は `179B340635B5C046AFAE481250754C70972A0285DAD9D69C7DDD8838341AF28D` です。現在の正本仕様は [GSQ_PI_SPEC.md](GSQ_PI_SPEC.md) です。

## 新しい比較対象

| 記号 | Actor | 実行harness | Fresh Critic |
| --- | --- | --- | --- |
| A | GSQ-RCO Qwen3.8-27B IQ3_XXS | Pi + SoL-Pi | GSQ-RCO |
| B | 同じGSQ-RCO | Hermes Agent | GSQ-RCO |
| C | Bonsai 2 27B + MTP | Hermes Agent | GSQ-RCO |

A対Bでharness、B対CでActorを比較します。3種類の実際のリポジトリ課題を各構成で1回ずつ、最大9回実行します。以前の「既存Continue経路対Pi」という比較や、`greeting.txt` の結果は接続・検証の予備試験として保存し、この9回には算入しません。準備済みの `duration-a` / `duration-b` は新計画の「実リポジトリの非自明な課題」に当たらないため、実行を中止します。

## 現在の位置

- Phase 0：配布元の保全と復元Git baselineは完了。既存Git履歴を発見したものではありません。配布元は直接編集しません。
- Phase 1：ClientPCのHard Rules、CPU版Laya、検証ゲート、Fresh GSQ Critic、Sol審査、件数のみのtelemetryは隔離パイロットで実装・検証済み。HermesとBonsaiを含む共通制御面としての適合は未検証です。
- Phase 2：Pi 0.85.1とSoL-Pi `1559b5c` は導入済み。Action Fusion / ObservationPackはON、OCC / EPRはOFFの保守設定を維持します。再インストールしません。
- Phase 3：Hermes AgentのClientPC隔離導入・LocalBrain接続は未着手。まず公式の配布・対応runtimeと現在のClientPC状態を調査します。
- Phase 4：ServerPCのGSQ-RCO稼働はユーザー提供のServerPC報告とClientPC Bridge経由の応答で確認済み。ただしモデル設定値の独立照合とA/B同一条件の記録は未完了です。
- Phase 5–6：Bonsai 2 + MTPの正確なモデル、runtime、ServerPC導入とGSQへの安全な逐次切替は未検証。ServerPCの管理経路・正本ソースが利用できない間、ServerPCを推測で変更しません。
- Phase 7–10：新しい3課題×3構成の本試験、評価、採用判断は未実施です。

## HermesとGSQの文脈長：本試験前の適合条件

2026-09-25に確認した[Hermes Agent公式のプロバイダー文書](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/integrations/providers.md)は、ツールを使うAgentには**実際に利用可能な64,000トークン以上**を要求し、それ未満の窓を起動時に拒否すると明記しています。カスタムOpenAI互換endpointには対応しますが、設定ファイルの `context_length` だけを64Kにしてもサーバー側の実窓は増えません。公式文書はllama.cppの `-c` と並列slotごとの文脈長を区別しています。

新計画のGSQ共通プロファイルは32Kであり、ユーザー提供のServerPC実装報告でも32,768です。32Kのままでは公式HermesでBを実行できず、同一GSQ条件のA対B比較は成立しません。ServerPC報告では32K・q4 K/Vの120分検証中のGPU空き最小値が307 MiBと小さいため、64Kが入るとは推定しません。これはServerPC側の報告値であり、このClientPCセッションで64Kの可否を実測していません。

ServerPCの管理経路が使えるようになった後、正確なモデル・runtime・KV設定で64K以上が安定動作するかを**別途検証**し、AとBを同じ検証済みGSQ設定にそろえる必要があります。検証前に本番GSQ設定を変更せず、Hermesへ偽の64K値を渡さず、別のクラウドモデルやAPI課金へ切り替えません。ClientPCではHermesの公式配布物の調査と制御面の隔離設計まで先行できますが、B/Cの本試験はこの条件が解決するまで開始しません。

公式の[ツール構成文書](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/features/tools.md)ではHermesのterminal、file、web、memoryなどが別のtoolsetです。LocalBrainのHard Rulesと検証ゲートを迂回させないため、導入時にはtoolsetの実効状態を確認し、Hermesから任意shell、Web、独立した永続知識を書き込めない隔離経路にします。

## 固定する境界

- 既存のContinue経路、ClientPC Bridge v2.1、mTLS、ServerPC Gateway、loopbackのみの推論待受、SecondBrainを保ちます。失敗した旧Bridge v2 ZIPを再適用しません。
- ServerPCにPi、SoL-Pi、Hermes、Laya、OpenCodeを追加しません。ServerPC側の新規作業は管理経路と正確な実装元が判明するまで開始しません。
- Fresh Criticは全構成でGSQ-RCOとし、Actor履歴を渡さず、書込権限を持たせません。Hard Rules、Laya、SecondBrain、検証ゲート、Sol適用条件はA/B/Cで共通にします。
- OpenCodeのChatGPT OAuthログインはユーザーが完了済みです。認証情報を表示・保存・ログ出力せず、APIキー課金に切り替えません。
- 機能変更は正本候補 `D:\GitHub\localbrain-client` でレビュー可能に保ち、自動commitやpushはしません。

## 次の独立作業

1. Hermes Agentの公式配布・互換性とClientPC導入方式を調査し、64K条件を満たせる場合だけGSQとの接続試験に進みます。ClientPCのWindows PATHとWSLには、2026-09-25の読み取り確認時点でHermesコマンドはありませんでした。
2. 既存Pi専用runnerのHard Rules、Laya、検証、Fresh Critic、SolをHermesにも共通化する差分を設計します。
3. ServerPCからGSQ 64K実現性の証跡、およびBonsai 2 + MTPと逐次モデル切替の正確な実装元・管理経路を得るまで、B/Cのモデル操作は保留します。
4. 実リポジトリから非自明なバグ修正、複数ファイル変更、長時間課題を選び、同一課題・同一検証条件で9回以内の試験を組みます。課題の準備だけで実行回数を消費しません。

実施済みの [AB_BENCHMARK.md](ab-benchmark/AB_BENCHMARK.md) は履歴として残しました。未実行のduration試験用リポジトリ3件と手順ファイルは、その後のGSQ+Pi採用決定で削除しました。

ClientPCのHermes統合に必要な実装境界とWSL loopback接続確認は [HERMES_INTEGRATION_DESIGN.md](HERMES_INTEGRATION_DESIGN.md) に記録しました。
