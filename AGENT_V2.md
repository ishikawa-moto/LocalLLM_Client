# LocalBrain Agent v2のClientPC試行

> 記録した時点の候補・検証・配備状況を保持した日本語版です。過去の未確認事項や手順を、現在の残作業・実行指示と混同しないでください。現在の状態は実配置の記録・設定・プロセスと照合します。

> **選択済み構成（2026-09-25）：GSQ-RCO + Pi/SoL-Pi。** ユーザーは追加ベンチマークを行わずこの経路を選び、今後の代替案は実際の使用で判断します。固定構成・既知の証跡・残る展開上の制限は`GSQ_PI_SPEC.md`を参照してください。Hermes/Bonsaiと未使用の`duration` fixtureは有効な範囲から外しました。

このソースは復元した配布物を基準とする未コミット作業です。配置済みLocalBrain Hostと既存Continue Qwenモデルを使い続けます。Sol方針のビルドは、Pi/Laya支援スクリプト3件を含めて`C:\Users\USER\AppData\Local\LocalBrain\agent-v2-sol-policy-20260925`へ分離配置し、全体のContinue MCP設定が既存の本番Client設定とともに参照します。以前の`agent-v2`フォルダーにはそのスクリプトがないため、タスクに使ってはいけません。有効なContinueルールは範囲付き変更をAgent v2 MCPへ渡し、Qwenモデルを`chat`ロールに限定して直接Edit/ApplyによるHost制御の迂回を防ぎます。元設定は保存済みです。配置済みHost実行ファイルとBridge v2.1は置換していません。実IDEが版付きの新MCPパスを認識するには再読み込みが必要です。

## プロセス経路

`agent-v2 run`は個別登録したGitルートを確認し、WSLでPi RPCを起動して`localbrain/local-qwen38`を選びます。プロバイダーは標準入出力でWindowsの`model-relay`を呼びます。そこから既存の`__BRIDGE_HOST__:__BRIDGE_PORT__` Bridgeへ接続し、ServerPCへのmTLS経路を維持します。新しい待受ポートは作りません。

Actorは`read`、`grep`、`find`、`ls`、`edit`、`write`を使えます。ポリシー拡張はshell、Gitルート外のパス、保護ファイル、タスクの正確な`allowed_files`一覧外への書込を拒否します。Hostが実行する検証コマンドは名前を指定したものだけです。新しいCriticは同じQwenプロバイダーを、Piセッション・ツールなしで使います。モデルの文章で検証用真偽値は設定できません。

タスク開始時に、Hostは既存のmTLS Gatewayから出典付きSecondBrainコンテキストを1パケット取得できます。検索は240文字、要求予算は1,024バイトに制限し、古いものや大きすぎるものは捨てます。パケットはActorと新しいCriticの参考資料であり、取得失敗が許可の付与や検証完了につながることはありません。`agent-v2 knowledge-health`は標準入力から検索を読み、件数だけを報告します。

## 試行コマンド

`localbrain.exe agent-v2 run <registered-git-root>`へ渡す標準入力JSONは次のフィールドを持ちます。

```json
{
  "requirement": "Create a scoped change",
  "acceptance_criteria": ["Describe the observable result"],
  "risk": "LOW",
  "approved_high_risk": false,
  "allowed_files": ["relative/path.txt"],
  "required_tests": [{"kind": "git_diff_check"}]
}
```

対応テスト種別は`git_diff_check`、`dotnet_build`、`dotnet_test`、`npm_test`です。.NET操作は選択Gitルート内の相対`.csproj`と、そのプロジェクトおよび静的に記述された参照先の既存オフラインrestoreメタデータを必要とします。異なる名前の最大20プロジェクトを許容し、動的・外部参照やソリューションには対応しません。.NET生成物と一時ファイルは`.localbrain/artifacts/`へ置き、ソース側の`bin`・`obj`は変更しません。`npm_test`はルートで実行します。標準出力・エラーは本文を保存せず、バイト数とハッシュを記録します。`dotnet_test`はコンソール要約に実行テスト数が1件以上あることも要求し、テスト0件の終了コード0は失敗です。新規`run`にはクリーンな作業ツリーが必要で、同じPiセッションで最大3 Actorターンを許可します。毎ターン、テストとGitツリーを再確認し、想定外ファイルやテストによるソース変更で停止します。LOW完了前には新しいCriticを使います。終了コードが0以外、または`eligibleForCompletion: false`なら、完了条件は満たされていません。

`agent-v2 resume <registered-git-root>`は同じ要求JSONを標準入力で受け取ります。同じ要求ハッシュ、Git HEAD、変更ファイル一覧、レビューディフハッシュを持つ`needs_work`または復旧可能な`failed`チェックポイントを再開します。想定外ファイル、テストによるソース変更、進捗のないチェックポイント、完了タスクは拒否します。保存済みPiセッションIDを再利用し、1呼出しにつき追加3ターン、合計6ターンまで許可してテストと新しいCriticを再実行します。ハッシュのない旧チェックポイントは自動再開できません。範囲内ファイル変更前の失敗でも、新たなクリーンツリーのrunが必要です。

選択した外部Reviewerが使えない場合は`awaiting_external_review`へ移ります。旧チェックポイントの`awaiting_sol_review`も読めます。`resume`は同じ要求・HEAD・ファイル・ディフハッシュを検証し、Actorを追加実行せず、指定テスト・新しいCritic・外部レビューを再実行します。否定的なレビューは`needs_work`へ戻し、完了条件を解除しません。認証失敗やプロバイダー診断はタスク状態へ保存しません。

Actor 6ターン後は通常の`resume`を停止します。同一要求を使った明示的な`agent-v2 recover <registered-git-root>`では、上限付きの現在のディフを作業履歴として、新しいPiセッションを1回だけ開けます。要求・HEAD・ファイル一覧・ディフハッシュが不変である必要があり、進捗なしやテスト改変のチェックポイントを拒否します。追加3ターン、総計9ターンまで許可し、テストと独立レビューを再実行します。復旧回数をチェックポイントへ保存し、さらに別の新セッションでの復旧を拒否します。変更のコミットや検証の迂回はしません。

Pi RPCのツールイベントを決定的な反復作業閾値で確認します。発火時はPiの公式`abort`を送り、セッションIDを保持して検証未完了とします。Layaにもターン内4ツール呼出しごとに小さな進捗状態を渡します。検証・再計画のためActorを止められますが、固定規則が優先です。

SoL-Pi ObservationPackは保守的プロファイルで有効です。Piのツール許可リストでは範囲付きファイルツールに加え、読み取り専用の`obs_recall`拡張だけを許可します。LocalBrain方針は`obs_` ID形式と非負のoffsetを検証し、SoL-Piは各取得ページを制限します。Host実行の検証出力は上限付きメモリ内だけに保持し、タスク結果にはバイト数・ハッシュ・短い固定診断コード一覧を含めます。修復プロンプトは診断コードと終了状態を利用できます。生のテスト出力をActorへ渡したり永続保存したりしません。

実行状態は選択プロジェクトの`.localbrain/local-validation.json`と`.localbrain/task-state.json`へ書き込みます。v2方針ではPiによるそのディレクトリの編集を許可しません。NORMALではLayaがCopilot、Sol、両方から選び、信頼度が閾値未満なら両方を選択します。HIGHでは必ずSolを使い、Copilotも使う場合があります。HIGHでは、明示承認フラグ、対話端末からの対応する1回限りの承認、Actor前のSol計画レビュー1回、Sol最終レビュー1回も必要です。必要なレビューがすべて成功するまで完了条件は閉じたままです。プロバイダー障害はコード指摘と分けます。`agent-v2 sol-health`は固定の読み取り専用プロンプトでOAuth経由Reviewerを確認します。

正確なHIGHタスクを承認するには、ユーザーが対話端末で`localbrain.exe agent-v2 approve-high <registered-git-root> <request-json-file>`を実行し、要求JSONと表示範囲を確認して完全な要求SHA-256を入力します。承認は現在のHEADと要求に結び付き、30分で期限切れとなり、Sol計画レビュー前に消費します。入出力のリダイレクトでは承認できません。無人MCP呼出しが`approved_high_risk: true`を送るだけでは承認になりません。

Sol方針のモデルは`openai/gpt-6.1-sol`に固定し、NORMAL最終レビューとHIGH計画・最終レビューは`high`を使います。実際に進捗が止まった場合、Sol救済は`high`の読み取り専用相談です。最初の回答が不正・未解決・利用不能なら追加の`high`再試行を1回だけ許可します。上限付きの助言を呼出し側へ返しますが、ファイル編集・Actor再開・完了条件の解除はしません。`max`の自動選択はなく、初回と再試行1回を超える呼出しは拒否します。ユーザーが完了したChatGPT OAuthログインを使い、WSL専用ディレクトリから実行し、全OpenCodeツールを拒否し、上限付きパケットをコマンドラインではなく標準入力で送ります。明らかな認証情報パターンがあれば送信を阻止します。生の診断・プロンプトは記録せず、認証・レビュー失敗で完了にはしません。

完了・失敗した実行は`.localbrain/telemetry.jsonl`へ小さな件数・判断メタデータを追記します。使用した出典は上限付きSecondBrainチャンクIDで記録し、取得本文・プロンプト・ソースパス・生ツール出力は除きます。記録失敗で検証結果を上書きしません。

選択プロジェクトは`.localbrain/`をGit除外する必要があります。`agent-v2 run`は実行中、`.localbrain/active.lock`を排他的に保持し、同じプロジェクトでの別runを2つ目のActor起動前に拒否します。

`agent-v2 ask`は読み取り専用、`agent-v2 critic`は新しいツールなしセッション、`agent-v2 decide`と`decide-batch`はLaya診断、`agent-v2 path`、`pi-health`、`laya-health`は正常性確認です。すべてのコマンドに既存`clientsettings.json`または`LOCALBRAIN_CLIENT_CONFIG`のパスが必要です。認証情報をプロンプト・タスクJSON・ログへ入れないでください。

## Continueの独立した入口

`localbrain.exe agent-v2-mcp`は標準入出力で`localbrain_agent_v2_run`、`localbrain_agent_v2_resume`、読み取り専用`localbrain_agent_v2_status`を公開します。書込ツールは構造化タスクと登録Gitルートを受け取り、CLIと同じHost方針・Piセッション・テスト・Critic・完了条件を使います。応答は生モデル出力・テスト出力ではなく件数と判定状態です。停止タスクは最大3件の上限付きSol救済助言を返せます。別の`continue-agent-v2-template.yaml`は同じループバックQwenとこのMCPサーバーを使います。有効なユーザーContinue設定には、保存済みのPiルールとchat専用モデルロールを反映しました。

テンプレートは実Continueモデルを置換していません。隔離ワークスペース試行では、事前配置実行ファイルと登録済みサンプルGitルート1件を使いました。Continueの組込ツール方針はYAML外のユーザー設定です。ユーザーは組込create/edit/replace/terminalをExcludedへ設定したと報告しており、Pi使用中もHost方針を迂回しないよう維持します。実IDE試行ではNORMALタスクが`localbrain_agent_v2_run`で約53秒で完了し、run結果と読み取り専用status結果の両方で完了条件trueを確認しました。追加した全体MCP設定は既存本番登録のGitルート10件を使い、ルートの追加・変更はしていません。

隔離試行ワークスペース`work/continue-pilot`には、ワークスペース限定MCP設定、事前配置v2のパス、サンプルGitリポジトリ1件だけの許可リストがあります。MCP initialize/list/statusの確認は成功しました。実Continue IDEの`sample-ide-repo-4`ではnpm/Git 2/2、新しいCritic PASS、Sol最終PASS 1回、読み取り専用statusの表示を伴って完了可能と返しました。同じ完了要求は、要求ハッシュ・Git証跡・永続化済み完了条件の検証後、Actorを再実行せず`already_complete=true`を返せます。重複配送を防ぎつつ、異なる要求は拒否します。

## 現在の制限

- Continue MCP経路は隔離IDEワークスペースで動作しました。全体Agent v2 MCPと独立実行ファイルは設定済みですが、VS Code再読み込みと接続確認は残っています。挨拶タスクの比較1件では既存・Pi両経路で同じ正しいファイルとテスト成功を得ましたが、実作業での使いやすさや速度を証明しません。ユーザー方針で追加ベンチマークは予定していません。起動・再起動復旧と本番復元の検証は未確認です。OAuth経由Solレビューは隔離NORMALと合成HIGHで動作しました。6ターン後の復旧は実装・合成試験済みですが、実際に枯渇したタスクでは未試験です。対話型HIGH承認コマンドも実ユーザーでは未試験です。
- ServerPC Gatewayやモデルサーバーは変更しません。
- LayaはCPUの2スレッドを使います。現チェックポイントの信頼度は未校正・低値であり、決定的な規則を優先し、低信頼度判断は代替規則へ戻します。
- SoL-Piは保守的プロファイルを維持します。ActionFusionとObservationPackは有効、OCCとEPRは無効です。
