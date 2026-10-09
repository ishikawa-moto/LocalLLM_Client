# LocalBrain v2 context budget telemetry — ClientPC実装結果

実施日: 2026-09-26（JST）。新しいAgent v2ホストを配置し、ContinueのMCP起動先を切り替えた。VS Codeの再読み込み後に、Continue画面から新ホストの接続を確認する作業が残る。ServerPC、Bridge、Gateway、既存のContinueチャット経路は変更していない。

## 1. 変更ファイルと配置

- `scripts/context-telemetry.mjs`: Piの最終payload境界で現在のcontextを分類。
- `scripts/context-tokenizer.py`: tokenizerと公式chat templateによる数値計数worker。
- `scripts/qwen-chat-template.jinja`: SHA-256固定の公式template。
- `scripts/localbrain-provider.js`: preflight計測と数値履歴。計測失敗時もモデル要求を継続。
- `scripts/laya_worker.py`: 数値フィールドのみ受理。
- `dotnet/LocalBrain.ClientHost/AgentV2/{ContextTelemetryState,PiRpcRunner,AgentTaskRunner,SupervisorDecision,TelemetryRecorder,AgentV2McpServer}.cs`: 使用量、actual、checkpoint、Laya、status。
- `dotnet/LocalBrain.ClientHost/LocalBrain.ClientHost.csproj`: workerとtemplateを実行ファイルに同梱。
- `scripts/context-telemetry.test.mjs`: 決定的な6テスト。
- `CONTEXT_TELEMETRY.md`: 設定と復旧。

10個の実行ファイル/scriptを `C:\Users\USER\AppData\Local\LocalBrain\agent-v2-context-telemetry-20260926` に配置し、publish元とSHA-256を照合した。旧ホストを保持し、Continue MCP YAMLのバックアップを作成してから起動先を変更した。Git commit/pushは行っていない。

## 2. 計数と分類

ServerPCが報告した3つの公式ファイルのSHA-256は、ClientPC取得ファイルと一致した。現行GGUFと同一SHA-256のISTA-DASLab公開ファイルからHTTP rangeでメタデータだけを読み、埋め込みchat templateのSHA-256、基本語彙248,044件と追加token 33件の文字列・IDが一致した。照合済みtokenizerはClientPC WSLの `/home/worker/localbrain-v2/verified-tokenizer.json` にモード600で配置した。GGUFの重み本体は取得していない。

preflightでは、モデルへ送る最終payloadを公式Jinja templateで描画した全文token数を `estimated_prompt_tokens` とする。4種類のshareはこの全文token数を分母にし、32,768 tokenのcontext windowを分母にしない。`context_usage` は全文token数を32,768で割る。`tool_schema` は実際に公開した定義、`tool_result` は現在のPi文脈にある結果またはObservationPack preview、`conversation` は現行user/assistant本文、`retrieval` は採用して挿入したSecondBrain本文。system promptとtemplateによる残差は別に記録し、4種類のshareを無理に100%へ正規化しない。取得したbackendのactual input usageは別履歴に記録する。生のpromptや取得文書はtelemetryへ保存しない。

例: 隔離タスクのCritic最終preflightは668/32,768 tokens、使用率2.04%。内訳はsystem 441、conversation 176、framing 51、公開tool/result/retrieval各0。conversation shareは26.35%、otherは73.65%。MCP statusの診断提案は `none`。

Layaへは既存compact stateに `context_usage`、`tool_schema_share`、`tool_result_share`、`conversation_share`、`retrieval_share` の数値だけを加える。隔離Actorのcheckpointでは使用率0.08249、tool schema share 0.47318、tool result share 0.00185、conversation share 0.05845、retrieval share 0を渡す状態を構築した。Layaの低信頼度結果は従来どおりCONTINUEへフォールバックした。自動COMPACT・tool削減・retrieval抑制は無効。

## 3. 精度と負荷

| 隔離要求 | preflight | backend actual | 差 | 誤差率 |
| --- | ---: | ---: | ---: | ---: |
| Pi、toolなし | 499 | 487 | +12 | +2.46% |
| Pi、tool 4件 | 1589 | 1507 | +82 | +5.44% |
| Pi、tool resultあり | 1656 | 1574 | +82 | +5.21% |
| Agent Actor 初回 | 2621 | 2518 | +103 | +4.09% |
| Agent Actor 次回 | 2703 | 2600 | +103 | +3.96% |
| Agent Critic | 668 | 656 | +12 | +1.83% |

差は全件、保守的な過大計数だった。公式templateの描画とllama.cpp側のOpenAI要求前処理にはなお差がある。長い実タスクでの誤差分布は未測定のため、この値を厳密なcontext上限保証には使わない。隔離Agent実行の冷間worker計測は669～729 ms、同一Piプロセス内の温間計測は5～6 ms。Agent全体の速度差は測定していない。

## 4. 検証と失敗時の動作

決定的な6テストは、会話のみ、大きなtool result、ObservationPack置換、公開tool schema、SecondBrainと二重計上防止、全文分母、異常tokenizer応答を検証して通過した。.NET Release publishは成功した。新規の隔離Gitタスクでは `greeting.txt` が `HELLO` + LFの6 bytesで作成され、npm/Gitの必須テスト、独立Critic、完了ゲートが通過した。新バイナリのMCP statusは `eligible_for_completion=true` と数値contextを返した。`contextTelemetry.enabled=false` の状態ではstatusが計測不可に戻ることも確認した。計測worker失敗時にはAgentを停止せず `context_metrics_available=false` を記録する。

## 5. 運用、復旧、今後のしきい値

4 flagの初期値は `enabled=true`、`persist=true`、`layaEnabled=true`、`autoActionsEnabled=false`。workspaceごとに `.localbrain/contextTelemetry.json` の `enabled=false` を設定すれば、次の要求から計測を止め、従来のLaya入力形へ戻る。モデル、Bridge、Gatewayの再起動は不要。

ホスト切替を戻す場合は `C:\Users\USER\.continue\mcpServers\localbrain-agent-v2.pre-context-telemetry-20260926.bak` を `localbrain-agent-v2.yaml` に戻し、VS Codeで `Developer: Reload Window` を実行する。旧 `agent-v2-sol-policy-20260925` ホストは保持した。進行中のAgentタスクがない時に復旧する。

自動処理のしきい値は設定していない。今後、長い実タスクでpreflight/actual誤差と4種類のshareの分布を収集し、ObservationPack・会話圧縮・tool定義削減・retrieval縮小がそれぞれ適切な原因にだけ対応することを別途確認してから決める。
