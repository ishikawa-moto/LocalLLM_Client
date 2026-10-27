# LocalBrain v2 ClientPC実装の確認記録

> 記録した時点の候補・検証・配備状況を保持した日本語版です。過去の未確認事項や手順を、現在の残作業・実行指示と混同しないでください。現在の状態は実配置の記録・設定・プロセスと照合します。

現在のSol方針（2026-10-14）：ユーザーはNORMAL最終レビュー、HIGH計画・最終レビュー、読み取り専用救済の2回とも`openai/gpt-6.1-sol`と`high`を選択しました。以下の旧gpt-6-sol/medium/xhighの記録は過去の資格確認です。LOWのCloud禁止、レビュー条件、試行上限、OAuth経路、Copilot/Astra方針は維持します。

更新日：2026-09-25（JST）

## 採用判断（2026-09-25）

ユーザーはGSQ-RCO Qwen3.8-27B IQ3_XXS + Pi 0.85.1 / SoL-Pi `1559b5c`を目標構成に選びました。追加ベンチマークは求めず、使用感が悪ければ実利用に基づき後で別方式を判断します。GSQ+Pi / GSQ+Hermes / Bonsai+HermesのA/B/C案は主試行前に撤回し、未使用の`duration` Git fixture 3件と指示を削除しました。完了試験の証跡や計画・設計記録を含む広い削除は、ユーザー指定範囲外として自動審査に拒否されたため履歴として保持しました。`work/GSQ_PI_SPEC.md`が固定構成、既存経路との挨拶比較、安全境界、展開制限を定めます。

両経路を比較したのは小さな挨拶タスク1件だけです。どちらも正確な6バイトのファイルだけを作り、指定テストが成功しました。既存経路は隔離所有fixtureでGit所有権エラーとなり、正確なパス例外の追加後に成功しました。Piは決定的検証・新しいCritic・Sol PASSで52,829ms、既存経路は未計測です。実作業品質・速度の比較には不十分で、この限界を伴うユーザー選好として扱います。当時、実Continueと配置Hostは不変でPiは隔離試行、ServerPCと元配布物も不変でした。OpenCode OAuthはユーザーが完了し、APIキー課金は使いません。

2026-09-25、GSQ+Pi運用設定の依頼後、最初に4ファイルを`C:\Users\USER\AppData\Local\LocalBrain\agent-v2`へコピーしましたが、必要なPi/Layaスクリプト3件の不足が判明したため実タスクに使ってはいけません。要求Sol方針の隔離Releaseを`C:\Users\USER\AppData\Local\LocalBrain\agent-v2-sol-policy-20260925`へ7件すべて配置し、ビルドとのハッシュ一致を確認しました。全体Continue MCPはこの完全版と本番`clientsettings.json`を参照し、登録Gitルート10件は不変です。`config.yaml`を`config.yaml.pre-gsq-pi-20260925.bak`へ保存し、範囲付き変更をAgent v2へ渡すルールとchat専用QwenでEdit/Apply迂回を防ぎました。`LocalBrain Client Host`本体とBridge v2.1は未置換です。最新Tools画像では`edit_existing_file`がAutomaticのためExcludedへの変更が必要でした。IDE再読込後の接続、起動復旧、実案件利用は未観測で、追加ベンチマークはしていません。

変更前は全計画・最終レビューで`openai/gpt-6-sol`/`high`、`ESCALATE_SOL`は救済相談なしに停止でした。この時点の新ソースはNORMAL最終`medium`、HIGH計画・最終`high`、読み取り救済の初回`high`と、不正・未解決・利用不能時の1回限り`xhigh`です。`max`は自動選択しません。助言は有限で呼出し側だけへ返し、計測には保存せずActor再開・完了認定はできません。隔離Release発行は成功し、実OpenCode救済は呼んでいません。

配備した版付き実行ファイルで、本番Client設定による読み取り専用MCP `initialize`/`tools/list`が成功し、ツールは正確に3件でした。Pi・Sol・Git作業・ベンチマークは実行していません。新パスとの接続確認にはContinueの再読込が残っています。

## ソースと境界

- 正本候補：`D:\GitHub\localbrain-client`
- 配布物基準コミット：`a9ffa4bc8e80123ebbbba9573ca97eb1a49a558a`
- 元インストーラー配布物は読み取り専用です。このClientPC実装でServerPCは変更していません。
- v2機能はレビュー用の未コミット状態です。自動コミットしません。
- 毎時Codex再開設定を採用済みGSQ+Pi範囲へ更新中です。Hermes/Bonsaiや取消済み試験を復活させず、必要な操作がなければ静かにし、重複Jobを防ぎます。

## 検証した事項

- ソース前後一覧は43パス、663,328バイトで、サイズとSHA-256が一致しました。全一覧は`SOURCE_PROVENANCE.md`です。
- 基準コミットは復元した元配布物のファイルだけを含みます。
- SoL-Pi `1559b5c`を維持し、ActionFusion/ObservationPack ON、OCC/EPR OFFです。この時点以前に公式158試験が成功しました。
- 許可WSL GitルートでPi RPCが起動し、`localbrain/local-qwen38`を選択しました。
- PiプロバイダーはWindows標準入出力中継から既存ループバックBridgeを呼び、ServerPC mTLS経路で`PONG`を返しました。待受は追加していません。
- 読み取り専用Pi RPCと、独立したセッション・ツールなしの新しいCriticが同プロバイダーで`PONG`を返して終了しました。
- Piは`read`で`D:\GitHub\tekutekulife\.gitattributes`を読み、ツール1回・エラー0で`READ_OK`を返しました。workspace逸脱・保護ファイル・shell拒否の方針試験も成功しました。
- Pi RPCイベントからツール数・エラー・変更ファイル・反復読取/コマンド/エラー・進捗なし回数を数え、引数・出力は保存しません。実読取では1回の呼出しで誤った進捗停止はありませんでした。
- 指定検証ルーターはWindowsでGit/.NET、WSLでnpmを実行します。許可プロジェクトで`git diff --check`が成功し、範囲外.NET対象は実行前に拒否しました。出力本文を残さずバイト数・SHA-256だけにしました。
- 隔離設定のGit fixtureでActor編集・Laya監視・WSL npm・Windows Git diff・新しいツールなしCritic・決定的検証が完了しました。変更は`greeting.txt`だけ、両試験とCriticが成功し、`local-validation.json`のLOW完了条件が成立しました。本番のプロジェクト許可一覧は不変です。
- 同fixtureのnpmを意図的に失敗させると、変更は`greeting.txt`だけのままnpm終了1、Critic未実行、検証フラグfalseでした。本番許可一覧のハッシュも保存時と一致しました。
- Pi write/editは拡張へ渡す正確なタスクファイル許可一覧を必須にしました。指定ファイルだけを許可し、別ファイル・逸脱・保護ファイル・shellを拒否する確認が成功しました。
- worker・テスト起動・Reviewer失敗時は未完了の`.localbrain/local-validation.json`と、PiセッションIDを含む秘匿済み`task-state.json`を保存します。欠落.NET対象で`phase=failed`、セッションID、`last_error=UnauthorizedAccessException`、`required_tests_passed=false`を確認しました。
- 診断Criticの回答にQwen推論マークアップが含まれたためRPC応答前に除去しました。後続呼出しでは最終JSONだけが返りました。
- .NET監視からCPU PyTorchのLaya 0.3.5が起動し型付き判断を返しました。CPUスレッド2/4/6の1回読込中央値は761/517/452msでした。1秒未満のイベント応答で十分なため、ビルド用CPUを残す2スレッドを維持します。
- 読込済みLaya workerを複数判断で再利用し、2判断バッチ全体34.3秒、推論1.29/1.11秒でした。障害時はHostが破棄・再起動できます。
- Laya監視の重複・並行Disposeで他呼出し中の同期ゲートを破棄しないよう修正しました。隔離Releaseは警告・エラー0、配置と正本ソースのSHA-256も一致しました。
- 人間承認のない高リスク操作は固定規則で止めます。Laya信頼度0.1287はCONTINUEへ代替し、権限と扱いません。
- Laya相談前に非有限のcontext値、負の読取/試験数、矛盾した試験フラグを拒否します。隔離Releaseと配置/正本ハッシュ一致を確認しました。
- Actorは同Piセッションで最大3ターンとし、開始時に未完了状態を記録し、試験後にHEAD・作業ツリーを再確認してターン・再計画・失敗反復数を保存します。隔離fixtureはツールエラー1件から2ターンで回復しnpm/Git/Criticが成功、LOW完了となりました。Sol計画レビュー未統合の当時、HIGHはActor前に拒否しました。
- 終了0を返しながら対象ファイルを書き換える隔離npm試験で`testMutatedWorktree=true`を検出し、Criticを省略して完了不可を保持しました。本番許可一覧は不変です。
- 既存読み取りmTLS Gatewayから小さな出典付きSecondBrainパケットを最大1件取得し、Actorと独立Criticへ信頼しない資料として渡します。件数だけの確認では3チャンク・1,021 Markdownバイトでした。本文は出力・保存せず、古い・超過・不正・利用不能なパケットを除外します。
- LocalBrain ClientPC要件の隔離タスクで上限内SecondBrainチャンク1件を使い、`status.txt`だけを変更し、npm/Git/Critic成功で完了可能となりました。本文は出力・計測保存していません。
- 実ツールの無進捗閾値を超えるとPi公式`abort`を送ります。6回同じ読取を要求した読取試験は5回目で`sameFileReadCount=4`、`noProgress=true`となり、回答空・編集なし・未完了で終了しました。
- `agent-v2 run`はプロジェクトごとの`.localbrain/active.lock`を排他保持します。意図的にdirtyなfixtureへ2回連続実行すると両方ともclean-tree事前エラーとなり、失敗時の解放を確認しました。同時重複は起動していません。
- .NET検証は既存offline restore情報を新しい`.localbrain/artifacts/`へコピーしSDK `--artifacts-path`を使います。試験ビルドが成功し、元`project.assets.json`不変、元`bin`なし、Git cleanを確認しました。この時点ではsolutionとproject-reference graphは拒否し、不正`.sln`はActor前に拒否します。
- テスト0件でも`dotnet test`が終了0となる例を確認し、メモリ内要約の実行テスト数1件以上を必須化しました。同fixtureは終了0でも`passed=false`となり、生出力は保存・表示していません。
- 完了タスクごとに件数・Laya型付き判断・Critic・知識数・偽成功・経過時間をJSONLへ1件追記します。初期の複数行整形を修正し、成功した試験の2イベントが各1行で解析でき、prompt・生出力・path・token・cookie・取得本文がないことを確認しました。
- SoL-Piの読み取り専用`obs_recall`を、固定`obs_[a-f0-9]{24}` IDと非負offsetで許可し、shell・未知ツールは拒否します。方針試験と17,492バイトの合成読取で台帳イベント`full`、`full`、`placeholder`、`recall`を確認しました。再読取・編集なし、ツール1回で正確なマーカーを返しました。イベント名だけを確認し、本文は見ていません。検証用の一時セッション再開診断は製品コマンドから除去しました。
- Piターン内4ツールごとにLayaへ小さな進捗を渡します。4ファイルfixtureではCONTINUE/RUN_TEST、2ターン・13ツール、npm/Git成功でしたが、Criticは再試行後もINVALIDで、`needs_work`と`false_verified_detected=true`を保持しました。否定的な条件確認で、完了ではありません。
- `agent-v2 resume`は要求・HEAD・変更一覧・review-diff SHA-256の一致を必須とし、テスト改変・無進捗を拒否、全体6ターンへ制限します。旧fixtureを同セッションで追加3ターン実行しnpm/Git成功でしたが、未追跡ファイル抜粋で末尾改行を証明できずCritic ISSUESで停止しました。
- 未追跡レビューに1回の有限UTF-8読取から正確なバイト数と末尾改行有無を加えました。最終1ターンの再開でnpm/Git/Critic PASS、`falseVerified=false`、`eligibleForCompletion=true`、総6ターンとなりました。配置Hostは不変です。
- 新規隔離タスクが1ターンでnpm/Git/Critic成功し、要求・diffハッシュを保存しました。同じ許可パスの内容だけを変更した否定試験ではPi前に拒否し、fixtureと状態を復元しました。隔離Releaseは警告・エラー0です。
- 復旧可能失敗でも許可一覧・diff・要求ハッシュ・消費ターンを保存し、完了条件を閉じます。合成`failed`から同Piセッションで試験/Critic成功により完了しました。別の欠落.NET参照試験ではActorが許可1ファイルを作成後`UnauthorizedAccessException`、`phase=failed`・1ターン・変更1件・有効ハッシュ2件を残し、意図的に未完了です。
- Host試験出力は有限メモリだけに置き、修復には固定診断コードを抽出します。npm assertionは`assertion_failed`、.NETコンパイル失敗は`CS1040`と`CS1002`だけとなり、結果に生出力はありません。変更した2ファイルを戻し、Release警告・エラー0を確認しました。
- 既存SecondBrain MCPを変えず、Continue用の独立stdio MCPでrun/resume/読み取りstatusを公開しました。隔離の完了・失敗fixtureでinitialize/list/statusが成功し、別rootのMCP run/statusも`complete`・Critic PASS・eligible trueでした。返却は件数と判定だけです。`continue-agent-v2-template.yaml`は別Qwenプロファイルで、実Continue未導入です。
- .NET検証を最大20のルート内静的project参照へ拡張し、別名・既存offline restoreを必須にし、情報コピーを50MBまでにしました。2プロジェクトのビルド成功、元assetsハッシュ2件不変、元binなし、隔離出力2件、Git cleanを確認しました。範囲外参照は事前拒否してfixtureを戻し、動的参照・solutionは未対応です。 （原記録の識別子：`project.assets.json`。）
- `obs_recall`を引数・内容なしで計数します。追加後Releaseは警告・エラー0です。
- WSLの`/home/worker/localbrain-v2/opencode`へOpenCode `1.18.32`を導入しました。人間のChatGPT OAuth完了後、`openai/gpt-6-sol`/highがツールなしで短い応答を返しました。SoL-Pi `1559b5c`と保守設定は不変、APIキー課金は未設定です。
- 隔離OpenCode/Sol Reviewerを完了条件へ結び付け、事前確認済みの有限証跡をstdinで渡し全ツールを拒否、prompt・診断は記録しません。配置前Hostの`agent-v2 sol-health`はreadyでした。NORMALは1ターンでnpm/Git/Critic/Sol最終PASS、合成HIGHはActor前計画PASSと試験・Critic後最終PASSで完了しました。両方`greeting.txt`だけを変更し、本番Host・Continueは不変です。
- HIGHは対話`approve-high`から発行した、要求ハッシュ・HEADに結び付く30分・1回限りの証明が必要です。リダイレクト入出力は拒否します。`approved_high_risk=true`だけの合成HIGHはSol/Actor前に拒否し0ターン・Git cleanでした。実ユーザー承認は未試験で、先の合成HIGH成功はこの追加条件前です。
- Sol最終通信失敗は閉じたまま`awaiting_sol_review`へ移ります。レビューだけの`resume`は要求/Gitを確認し、試験・新しいCritic・SolをActor追加なしで行います。合成保留から同ID・同1ターン・`toolCalls=0`で完了しました。非重複経路の確認で、実プロバイダー障害は未試験です。
- 明示`agent-v2 recover`は6ターン枯渇に1回の新Piセッション、全9ターン上限で対応します。同要求・HEAD・ファイル・diffを保持し、有限な現diffだけを作業履歴に渡して試験/Critic/Solを再実行します。合成例は同IDの7ターン目、`recovery_count=1`、全PASS、許可diff不変で完了し、2回目のrecoverを拒否しました。無進捗・テスト改変を自動復旧しません。
- 計測は件数だけでなく上限付きSecondBrainチャンクIDを記録し、本文・prompt・生ツール出力・パスを除外します。Release警告・エラー0です。
- 隔離ContinueワークスペースにMCP設定と1プロジェクト許可一覧を置きました。initialize・3ツール・`not_started`が成功し、サンプルの全MCP runは1ターン、npm/Git/Critic/Sol最終PASS、`eligible=true`でした。IDEのツール方針や長時間期限を証明するものではなく、本番Continue/Hostは不変です。
- 新しいcleanな`work/continue-pilot/sample-ide-repo`だけを試行許可し、前のMCP実行fixtureを証跡として残しました。statusは`not_started`、手動IDE方針・長時間呼出し手順は`work/continue-pilot/README.md`に記録し、新fixtureは未実行です。
- 事前配置.NETビルドは警告・エラー0です。
- Agent v2ソースと`D:\GitHub\localbrain-client`のコピーをSHA-256比較し最終同期時の不一致0、元配布43ファイルも不変でした。

## ServerPC報告とClientPC Bridge更新（2026-09-25）

- 人間提供のServerPC報告ではGSQ-RCO IQ3_XXS稼働、Gatewayキュー・永続Job・独立Critic経路・メタデータendpointの試験が成功しています。この時点ではClientPCから独立確認した結果ではありません。ServerPCは本実装の変更範囲外です。
- ClientPC Bridge v2 ZIPを保存付き試行で適用しました。`/v1/models`は200でも固定長`POST /v1/chat/completions`は400 `transfer encoding unsupported`、旧バイナリ復元後に同要求が200でした。`C:\Users\USER\Documents\Codex\2026-09-24\te\work\bridge-v2-update-20260925\RESULT.md`とハッシュ一覧を参照し、このZIPを再適用しないでください。
- 明示依頼により同script/ZIPを2回目実行し、配置はZIP一致・script完了でもChatは再び400でした。新しい保存物から戻して旧4ハッシュ一致、タスクRunning、Chat 200を確認しました。`C:\Users\USER\Documents\Codex\2026-09-24\te\work\bridge-v2-update-20260925\RERUN-RESULT.md`を参照してください。
- 隔離Continue IDE試行は`InvalidDataException`2件、`greeting.txt`未作成でした。元MCP引数がなく原因は未確定です。許可一覧には正確な`sample-ide-repo`があり、実行前ファイル未作成は意図どおりです。誤った`sample-repo`を指す`README.txt`を修正し、schemaに正確な登録root・新規ファイル可・`npm_test`/`git_diff_check`では`target`不要と追記しました。固定された非機密エラー理由を返す新Hostが警告・エラー0でビルドされ、status/不正targetで3ツール・`not_started`・省略targetエラーを確認しました。YAMLを新版へ向け、IDE再読込後の妥当な実runは未確認です。
- 復元ソースは`HttpListenerRequest.ContentLength64`を`Bridge.cs`経由で`GatewayClient.SendAsync`へ渡し、mTLS前に`request.Content.Headers.ContentLength`を設定します。修正パッケージもこの動作とメタデータ転送を保持し実Chatを通す必要があります。提供v2バイナリ内の正確な故障箇所は独立逆解析していません。
- 提供Bridge v2.1 script/ZIPは終了0で自身の実Chat試験に成功しました。Client独立確認でも新4ファイルとZIPの一致、旧4ファイルのハッシュ一致保存、`/v1/models` 200、固定長Chat 200と完了choice 1件、タスクRunning、TCP __BRIDGE_PORT__がloopback専用を確認しました。`C:\Users\USER\Documents\Codex\2026-09-24\te\work\bridge-v2.1-update-20260925\RESULT.md`を参照。v2.1を維持し、失敗v2は再適用しません。
- 実Continueは以前の正常経路のまま、分離Agent v2は未配備です。新Serverメタデータ経路とContinue Agent v2全体は配置v2.1経由で未独立確認です。対応v2.1ソースは復元repoになく、提供バイナリしかありませんでした。
- 配置v2.1とmTLS経由の`ask`と独立`critic`は正確な`PONG`、ツール・エラー・無進捗0でした。隔離`sample-ide-repo`はcleanで`greeting.txt`・`.localbrain`なしです。Pi Actor/Criticのモデル経路を確認しましたが、実IDE・メタデータ経路は未確認です。
- IDEの後続2試行で`sample-ide-repo`に`IOException`・2ターン・完了falseが残りました。手動編集は`HELLO`+LF 2件の7バイトとなり、`work/continue-pilot/ide-failure-20260925`へ元ファイル・状態を保存して6バイト`48454C4C4F0A`に直しました。WSL `npm_test`とWindows `git_diff_check`は成功しましたが、失敗状態を完了へ書き換えていません。 （原記録の識別子：`greeting.txt`。）
- 同LOW作業の別clean Gitでは1ターンで完了し、IDEのI/O例外は再現せず原因不明です。元状態を保持して再試行するため、新しいcleanな`work/continue-pilot/sample-ide-repo-2`だけを許可・案内し、status `not_started`、ファイルなしとしました。実IDEの方針・長時間呼出しは未確認です。 （原記録の識別子：`IOException`、`greeting.txt`。）
- Continueが接続失敗とclosedを報告しました。IDE作成の`.continue/mcpServers/new-mcp-server.yaml`が`npx -y <your-mcp-server>`を呼び、LocalBrainではなく空テンプレートへ接続していました。削除して既知正常`localbrain-agent-v2.yaml`の同一コピーだけを置き、fixtureの`.git/info/exclude`で`.continue/`を除外し、コミットせずcleanを維持しました。initialize/list/statusは3ツール・not_started、挨拶ファイルなしです。IDE再読込・pilot選択と実長時間試験が残っています。 （原記録の識別子：`Failed to connect to "New MCP server"`、`greeting.txt`、`LocalBrain Agent v2 pilot`。）
- 後の画像でLocalBrain Agent v2 pilotは緑・3ツール、New MCP serverは赤・0ツールでした。再作成された空npxテンプレートを内容確認して再削除し、workspace MCPはLocalBrainだけになりました。直接確認は3ツール・not_started・cleanです。接続の視覚証跡ですが、組込ツール除外や長いAgent完了はまだ証明しません。 （原記録の識別子：`npx -y <your-mcp-server>`。）

## 次の作業

1. `work/GSQ_PI_SPEC.md`だけを現採用仕様にし、A/B/C・duration・完了挨拶試験を再開せずHermes/Bonsaiを導入しません。Action Fusion/ObservationPack ON、OCC/EPR OFFを維持します。
2. Bridge v2.1・確認済み保存物・Continue LocalBrain Qwen 27B・本番Hostを保持します。完全なAgent v2/MCPは設定済みですが、新パスのIDE実接続は未確認です。失敗Bridge v2 ZIPを再試行しません。
3. Pi前のContinue保存設定と既存モデル/SecondBrain経路を復元用に保持します。Piは版付きsol-policy側へ隔離し、不完全な旧agent-v2は使いません。次はVS Code再読込、新MCP接続確認、edit_existing_fileをExcludedへ変更する手操作です。完了済み試験は再起動しません。 （原記録の識別子：`LocalBrain\agent-v2-sol-policy-20260925`、`LocalBrain Agent v2`。）
4. 起動/再起動復旧、実案件、Sol障害/否定レビュー、本当に枯渇したタスク、dirty tree、大diff、成功.NETテスト数解析、矛盾するSecondBrain参照は未確認です。ユーザーは追加試験を辞退しており背景実行しません。
5. 入手可能なら対応するBridge v2.1の正確なClientソースを復元repoへ取り込みます。正確なsourceと管理経路なしにServer Gateway/モデルを変更しません。

## 運用記録

- 実Continue試行は約53秒のMCP呼出しを完了しIDEに結果が表示され、残る書込ツール除外は人間確認です。試行の方針・長時間呼出しを確認しました。本番Hostは不変で、Continueルールと別の全体MCPを保存付き追加しました。その配置後の接続・起動・復元・実案件は未確認です。
- ビルド出力は隔離します。復元基準は元のbin/objを意図的に追跡しているため、repo内ビルドで変更し得ます。
- モデル経路はPi → Windows stdio relay → __BRIDGE_HOST__:__BRIDGE_PORT__ → 既存mTLS Gateway → ServerPCです。失敗したWSL mirrored-network変更は適用していません。 （原記録の識別子：`Pi → Windows stdio relay → __BRIDGE_HOST__:__BRIDGE_PORT__ → existing mTLS Gateway → ServerPC`。）
- askは読み取り専用、runは明示許可ファイル・clean tree・3ターン・指定試験・実Laya進捗・新Critic・必要なSol・完了条件を持つ有限試行です。resumeは同状態を保存Piセッションで全6ターンまで、recoverは新セッション1件で追加3ターンまでです。隔離IDE統合は確認済み、本番配備は未実施です。 （原記録の識別子：`agent-v2 ask`、`agent-v2 run`、`agent-v2 resume`、`needs_work`、`failed`、`agent-v2 recover`。）
- 2026-09-25の`sample-ide-repo-2`タスク`91e7543e2a6248c396d38c659d27816b`は必須試験証跡前に4回IOExceptionで失敗しました。変更は正確な6バイトHELLO+LFだけで、直接npm/Git・Pi/Laya正常性は成功しました。Hostが例外型だけを保存したため、元のI/O操作は未特定です。 （原記録の識別子：`greeting.txt`、`npm_test`、`git_diff_check`。）
- 同じ許可diffの失敗LOW/NORMALは、Actor追加前に新規レビューする候補になりました。必須試験を再実行しdiff不変を確認、Criticと必要Solを実行します。失敗は未完了のまま、HIGH承認は不変です。別cloneの合成NORMAL失敗は新Actor 0回・両レビュー成功で完了しました。
- 元IDE状態を`work/continue-pilot/ide-failure-2-20260925`へ保存し、正確な同要求を新隔離buildで再開しました。同ID・4ターンを保持し、2試験/Critic/Sol PASS、`phase=complete`、`eligible_for_completion=true`となりました。6バイトと`?? greeting.txt`だけを維持し、pilot YAML 2件は`work/v2/pilot-review-build/localbrain.exe`へ向けました。実拡張Hostには再読込が必要な場合があります。この操作だけではIDE除外・長時間応答を証明せず、本番設定は不変です。
- IDE専用の次試験`work/continue-pilot/sample-ide-repo-3`を基準clone・pilot-review-build・唯一許可rootで準備し、3ツール/not_started/clean/ファイルなしを確認しました。`work/continue-pilot/README.md`に再読込・Excluded・1回だけの手順を記載し、完了済みsample-ide-repo-2は保持、実IDE以外で新fixtureを実行しません。 （原記録の識別子：`greeting.txt`。）
- sample-ide-repo-3の実試行は2026-09-25 00:33:34 UTC、1ターン・1ツール・2試験/Critic/Sol PASS・完了trueでした。ファイルは`48454C4C4F0A`だけですが、ユーザーは後の`MCP error -32603: InvalidOperationException`を見ました。失敗計測はなく、完了後run反復ならclean-tree条件で計測前に拒否されます。UI配送順は未証明で、その表示原因は断言しません。 （原記録の識別子：`eligible_for_completion=true`、`greeting.txt`、`localbrain_agent_v2_run`。）
- MCP runは要求ハッシュ・ID・HEAD・有限diffハッシュ・許可変更一覧・永続完了条件を比較し、同完了要求にWSL/Actorなしの`already_complete=true`を返します。隔離Release警告/エラー0、sample-ide-repo-3への同要求は2試験成功・状態不変、別要求も状態不変で拒否しました。pilot YAML 2件はpilot-idempotent-buildを参照し、本番設定不変。除外方針と成功長時間結果のIDE表示は未確認です。
- 人間のDeveloper: Reload Window後、Continueがpilot-idempotent-build/localbrain.exe agent-v2-mcp（確認時PID6532）を実行することを確認しました。sample-ide-repo-3はcomplete、試験・local/Sol review true、変更は`?? greeting.txt`だけです。新Host読込は確認できましたがIDE方針・長時間結果の確認は残ります。 （原記録の識別子：`phase=complete`。）
- 再読込後のTools画像でcreate_new_file・terminal・edit_existing_file・rule作成はExcludedですが、single_find_and_replaceがAutomaticで書込経路が残りました。読み取りツールはAutomaticで構いません。READMEへ具体設定を追記し、完了fixture再試行・本番変更はしていません。
- ユーザーがsingle_find_and_replaceをExcludedにしたと確認しました。画像で唯一残った書込経路は人間申告上解消しましたがUI再検査は未実施です。長時間結果確認用にsample-ide-repo-4を基準からcloneし、唯一root・pilot-idempotent-build・not_started・clean・ファイルなしを確認しました。READMEには1回の貼付指示とエラー後再送禁止を書き、実IDE外では実行しません。 （原記録の識別子：`work/continue-pilot/sample-ide-repo-4`、`greeting.txt`、`work/continue-pilot/README.md`。）
- sample-ide-repo-4の2回目実IDE試行はrun 1回と読み取りstatus 1回の両結果が表示されました。同ID`f1a79b91d51a40b38053bc135f29dfc6`、complete・完了true、1ターン・2/2試験・Critic/Sol PASS・偽検証なしです。独立確認でも6バイト`48454C4C4F0A`だけ、必要条件true、NORMAL成功1件・52,829ms・ツールエラー0が一致しました。人間の除外確認後の約53秒MCP結果表示を証明しますが、隔離試行であり起動・再起動・本番展開は未確認です。 （原記録の識別子：`localbrain_agent_v2_run`、`localbrain_agent_v2_status`、`phase=complete`、`eligible_for_completion=true`、`greeting.txt`。）
- 実IDEの既存対Pi挨拶比較は、`work/ab-benchmark/greeting-a`とsample-ide-repo-4を同tracked HEADから始め、6バイト挨拶ファイルのみ、npm testとgit diff --check成功でした。Aを独立再確認し、初期の所有権問題は人間の正確なpathのglobal safe.directory例外で解消しました。A時間未計測、Pi 52,829msのため速度比較はできません。GSQ_PI_SPECに限界を記録し、ユーザーは追加試験を行わないと選びました。 （原記録の識別子：`work/continue-pilot/sample-ide-repo-4`、`greeting.txt`、`48454C4C4F0A`、`work/GSQ_PI_SPEC.md`。）
- 比較後に人間がContinueの書込・terminalをExcludedへ戻したと確認しました。準備した2つ目のduration比較は未実行で、追加試験なしのGSQ+Pi採用で取消し、一時fixtureと指示を整理対象にしました。本番Continue/Hostは変更していません。

## コンテキスト予算の計測（2026-09-26）

- Piプロバイダーペイロード境界で、公開ツール定義・ObservationPack後の有効結果・会話・タグ付きSecondBrain取得を別数値で計測しました。4比率の分母は推定有効prompt tokenです。自動context操作はOFFで、モデル/Bridge/Gateway/ServerPC/Continueは不変です。
- 任意の同モデルtokenizer worker、workspace別contextTelemetryフラグ、事前/実数値記録、有限な最近履歴と増加、Laya数値状態、読み取りstatusを追加しました。計測失敗でもAgentを止めずcontext_metrics_available=false、enabled=false 1件で次要求から旧Laya状態へ戻せます。
- D:\GitHub\localbrain-clientへcommit/pushなしで同期しました。隔離Release build/publishは警告/エラー0、MCP initialize/list成功、決定的分類5/5（6要件のB/CとE/Fを結合）成功、分類のみ1000回平均0.021msでした。実tokenizer・端から端までの追加負荷は未計測です。
- 配備GSQ-RCO Qwen3.8用の検証済みtokenizer.jsonがClientになく、事前構成・実backend誤差校正・実Laya数値は未確認でした。Agent v2本体とMCPは不変です。CONTEXT_TELEMETRYに出所・設定・復元要件を記載しました。
- 使い捨てdummy設定と1rootで完了sample-ide-repo-4の読み取りMCP statusを確認し、元完了条件とcontext_metrics_available=false、context=nullを得ました。run/resume/モデル要求は0です。ユーザーはServerから対応tokenizerを得る方法を確認し、未検証品を代入しません。
- 公式Qwen tokenizer候補をGit外work\context-tokenizer-candidate\tokenizer.jsonへ配置しました。12,809,320バイト、SHA-256 0997f410c57a1f4e53b09e4be8f4a172d90edd9564368fb0847030937229b9f3でQwenとISTA-DASLab公開記録に一致しました。GGUF SHAもISTA-DASLab IQ3_XXSに一致しますが、埋込tokenizer/tokenize挙動・usage校正にはServer確認が必要で、本番Agent未設定です。
- 2026-09-26完了記録：Server報告の公式tokenizer/template/config SHA-256が取得物に一致しました。正確な公開GGUFの有限HTTP rangeで埋込templateハッシュと基本248,044+追加33 IDを照合し、WSLへmode600で導入しました。決定的試験6件と隔離Agentのnpm/Git/Critic・MCP数値statusが成功しました。Actor事前/実誤差は103/2518、103/2600 token、Critic 12/656。準備済み計測6ms、初回worker669～729msです。自動context操作はOFF。版付きHost10件のSHA-256を照合してMCPを変更し、旧Host/YAMLを保持。VS Code再読込後の実観測は未確認です。

## ClientPCの外部Reviewer振分け（2026-09-28）

- Laya copilot/sol/both、独立Copilotと既存Sol、provider障害時代替、共通完了条件、任意の有限Astraメタ比較を追加しました。Astraは既存指摘を比較するだけでコードReviewer・完了条件ではありません。HIGHの対話承認/計画/最終Sol、LOWのローカル限定を維持し、詳細・復元境界はREVIEWER_ROUTINGに記載しました。
- Releaseと決定的経路/条件確認が成功しました。非対話合成CopilotはPASS、合成gpt-6-astraは最初UNAVAILABLE、後に成功し、タスクソースや認証情報は渡していません。Layaのboth・信頼度0.0146は低信頼度両者規則を正しく発火しますが、似た入力でAstra重みにより二者利用をまだ減らせません。
- 新規隔離Gitタスクが1ターンで指定2試験・Critic・Sol・CopilotすべてPASS、Astra記録、eligible_for_completion=trueとなり、完了済み旧試行は再実行しませんでした。
- フレームワーク依存publishをC:\Users\USER\AppData\Local\LocalBrain\agent-v2-reviewer-routing-20260928へ12件同ハッシュ・サイズで配置しました。自己完結win-x64はruntime pack 10.0.11をローカル解決できませんでしたが、必要な共有10.0.11はClientにあります。MCP YAMLをlocalbrain-agent-v2.yaml.reviewer-rollbackへ保存し実行先だけ変更、新Host initialize/listの3ツールと人間の再読込後の実プロセスを確認しました。旧buildは復元用に保持し、Server・Bridge・Client設定は不変、commit/pushなしです。
