# LocalBrain v2 context telemetry (ClientPC)

この機能はPiがQwenへ送る直前の `Context` と最終的なcompletions payloadを計測します。ServerPC、モデル、Gateway、Bridge、Continue UIは変更しません。自動的なCOMPACT、ツール削減、retrieval抑制は初期状態で無効です。

## 計数と分類

- `context_usage = estimated_prompt_tokens / 32768`。実際の入力token数がPiの応答usageに含まれる場合は別に `context-actual.jsonl` へ記録します。
- 4種類のshareは **推定prompt token数** を分母にします。context window全体を分母にしません。
- `tool_schema`: そのリクエストの最終API payloadに含まれるツール定義のみ。インストール済みツールは数えません。
- `tool_result`: Piが現在のモデル文脈に渡す `toolResult` のtext。ObservationPackが原文をpreviewへ置換した場合はpreviewのみ数えます。
- `conversation`: 現在のuser/assistantメッセージ。tool resultを重複計上しません。
- `retrieval`: Orchestratorが現在のpromptへ採用したSecondBrain packet。採用されなかった検索結果は数えません。Skill本文を将来明示的に追加する場合もこの区分へ入れます。Piの固定Skill説明はsystem promptに分類します。
- `system_prompt`: Piのsystem promptとSecondBrain区分用の境界タグ。
- `framing`: 公式chat templateで描画した全文token数から上記の分類済みtoken数を引いた残差。role marker、固定ツール呼び出し説明、segment境界の差などを含みます。

segmentと公式chat templateで描画した全文は、照合済み `tokenizer.json` を使ってClientPC WSLのPython `tokenizers` でtokenizeします。全文のtoken数を `context_usage` と4種類のshareの分母にします。単純な文字数/4近似は使いません。immutableなモデル要求が作られた時だけ計測し、イベントごとの全履歴再tokenizeは行いません。同一Piプロセス内ではtokenizer workerを再利用します。tokenizerかtemplateが壊れている、またはタイムアウトした場合は `context_metrics_available=false` とし、Agent実行は継続します。

**実測上の制約:** llama.cppがOpenAI要求へ適用する前処理と、公式JinjaをClientPCで描画した結果には、短い実要求で約1.8～5.4%のtoken差が残ります。現計測は診断と傾向観察用であり、厳密なcontext上限判定や自動削減のしきい値にはまだ使いません。

### tokenizer照合の手掛かり

ServerPC実装報告の現行GGUF SHA-256 `fdfcb6a29b11188956dfbfd904223588a6c1b77eb250c3e8a36e1bd269df91f7` は、[ISTA-DASLab公開のIQ3_XXS GGUF](https://huggingface.co/ISTA-DASLab/Qwen3.8-27B-GSQ-RCO-GGUF/blob/main/Qwen3.8-27B-GSQ-RCO-IQ3_XXS.gguf)のSHA-256と一致する。同じ公開元の[model card](https://huggingface.co/ISTA-DASLab/Qwen3.8-27B-GSQ-RCO-GGUF/blob/main/README.md)は基底モデルを `Qwen/Qwen3.8-27B` と記載する。[Qwen公式のtokenizer.json](https://huggingface.co/Qwen/Qwen3.8-27B/blob/main/tokenizer.json)と[ISTA-DASLabの3Bit-GSQ版tokenizer.json](https://huggingface.co/ISTA-DASLab/Qwen3.8-27B-3Bit-GSQ/blob/main/tokenizer.json)は公開SHA-256 `0997f410c57a1f4e53b09e4be8f4a172d90edd9564368fb0847030937229b9f3` が一致する。

ServerPCから報告された `tokenizer.json`、`chat_template.jinja`、`tokenizer_config.json` のSHA-256は、ClientPCで公式配布元から取得した3ファイルのSHA-256とそれぞれ一致した。ClientPCは上記公開GGUFの先頭32MiBだけをHTTP rangeで読み、メタデータ10,944,483 bytesを解析した。GGUFに埋め込まれたchat templateは公式ファイルとSHA-256が一致し、基本語彙248,044件と追加token 33件の文字列とIDがすべて一致した。モデルの重み本体、秘密鍵、認証情報は移動していない。

ClientPCの取得記録は `C:\Users\USER\Documents\Codex\2026-09-24\te\work\context-tokenizer-candidate\PROVENANCE.md`。照合済みtokenizerをClientPC WSLの `/home/worker/localbrain-v2/verified-tokenizer.json` にモード600で配置し、再度SHA-256を確認した。実運用のContinueホストは別途切替が必要です。

## 設定と復旧

各登録済みGit workspaceの `.localbrain/contextTelemetry.json` は任意です。省略時は下の4 flagと照合済みtokenizerパスが適用されます。設定はリクエストごとに再読込されます。

```json
{
  "contextTelemetry": {
    "enabled": true,
    "persist": true,
    "layaEnabled": true,
    "autoActionsEnabled": false,
    "tokenizerJson": "/home/worker/localbrain-v2/verified-tokenizer.json"
  }
}
```

`tokenizerJson` は照合済みの実ファイルだけを指定します。全体を即時無効化するには `enabled` を `false` に変更します。モデル/Gateway/Bridgeの再起動は不要で、次のモデルリクエストから従来のLaya入力形に戻ります。`persist=false` は履歴JSONLへの追記を止めますが、Layaとの数値連携用の最新状態ファイルは残ります。`autoActionsEnabled` は将来の検証済みpolicy用に予約され、現実装ではtrueにしても自動COMPACT等は始まりません。

2026-09-26に、版を分けた `C:\Users\USER\AppData\Local\LocalBrain\agent-v2-context-telemetry-20260926` に実行ファイルとscriptを配置し、Continueの `C:\Users\USER\.continue\mcpServers\localbrain-agent-v2.yaml` のAgent v2起動先を更新した。旧ホスト `agent-v2-sol-policy-20260925` は保持した。切替前のYAMLは同じディレクトリの `localbrain-agent-v2.pre-context-telemetry-20260926.bak` に保存しており、SHA-256は `7398C7C684E2EA77D75F96924B2CEB96793B3354B37E38B913B0420401164186`。VS Codeの `Developer: Reload Window` 後にContinueが新しい設定を読み込む。Bridge/Gateway/ServerPCの再起動は不要。

ホストごと戻す場合は、上記YAMLバックアップを `localbrain-agent-v2.yaml` へコピーし、VS Codeを再読み込みする。新旧ホストを混在実行せず、進行中Agentタスクがない時に実施する。新ホストは後で削除可能だが、復旧確認前は保持する。

`.localbrain/context-current.json` は最新preflightの数値のみ、`context-telemetry.jsonl` は最大約2MBの直近履歴、`context-actual.jsonl` は取得できたbackend usageと推定誤差の数値のみ、`context-decisions.jsonl` はcheckpoint時の数値状態とSupervisor判断のみです。`localbrain_agent_v2_status` は最新の使用率、4種類のshare、other、増加量、最大区分と診断上の `suggested_action=none` を返します。Layaには数値と既存compact stateだけを渡し、prompt、tool出力、SecondBrain本文は渡しません。

## 検証と自動化の条件

合成テスト6件は会話のみ、大きなtool出力、ObservationPack置換、多数の公開schema、SecondBrain挿入、二重計上防止、全文分母を検査して通過しました。隔離したPi実要求ではpreflight/actualがツールなし499/487、ツール4件1589/1507、tool resultを含む要求1656/1574でした。新規の隔離Agentタスクは `greeting.txt` を正確に6 bytesで作成し、npm/Git/独立Criticが通過、MCP statusが `eligible_for_completion=true` と数値contextを返しました。タスク中のActorは2621/2518と2703/2600、Criticは668/656でした。冷間worker計測は約669～729ms、温間は約5～6msです。Layaは数値状態を受け取る経路を通り、低信頼度時には従来どおりCONTINUEへフォールバックしました。`autoActionsEnabled=false` を維持し、0.69や各shareの例示値をしきい値として採用しません。
