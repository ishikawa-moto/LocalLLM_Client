# Windows 試験の訂正と最小確認

## 原因の確認結果

「Windows のコード整合性のため .NET 試験を一律に実行できず、新規署名が必要」という以前の扱いは撤回します。
PC73 のポリシーが原因という証拠もありません。保護設定・証明書・署名基盤は変更していません。

- 過去の `terra-v3-canonical-fix2-execution.log` に、`dotnet run --no-restore --project dotnet/LocalBrain.ClientHost.Tests -- --pi-canonical-proof`
  がビルド後、`LocalBrain.ClientHost.Tests.exe` の起動で終了1となり、
  「アプリケーション制御ポリシーによってこのファイルがブロックされました」と返った原文があります。
- 2026-10-29 09:54:09 JST に取得された保存出力には、同日09:52:58の3077と上記EXE、
  `dotnet.exe`、Policy ID `{0283ac0f-fff1-49ae-ada1-8a933130cad6}` が残っています。
  この出力は Kaspersky の DLL を指したものではありません。
  ただし元のイベント XML・MachineName・RecordId・HRESULT は保存されていません。
  パスはPC70の作業領域と一致しますが、当時の Work/CLI の実行制約と実機の適用元を確定できません。
  過去の正確な原因は未確定とし、この記録から PC70/PC73 の現在の動作を推定しません。
- 現在の実行先はPC70 **REI**。直近7日分の3033/3076/3077を読み取り、
  LocalBrainテストに該当する記録は0件でした。PC73のログは今回こちらでは取得していません。
- 通常の EXE 起動では Canonical 試験が終了0。管理タスク試験は Work 内で
  `ManagedRelayPermit.Issue → File.Move` の書込み拒否、許可ファイル試験は名前付きパイプ接続拒否でした。
  どちらもEXEは起動済みで、終了コードは `-532462766`（CLR未処理例外）。内側のHRESULTは未取得です。
  Work の制約外で、同じEXEを**通常ユーザー・UACなし**で実行すると両方が終了0となりました。
  この2件は Work の実行制約との相互作用として切り分け、SAC/WDAC障害に分類しません。

元のログは上書きせず、作業領域 `windows-host-investigation-20261102` にコマンド・時刻・結果を保存しています。

## 合格した限定試験

|既存試験の指定|確認した範囲|
|---|---|
|`--pi-canonical-proof`|原文証跡、重複・中断・不正な完了の拒否|
|`--managed-agent-proof`|タスク受付、正確な取消、重複実行防止、Canonicalによる完了判定|
|`--task-command-proof`|本物の所有Windows子プロセス、取消、期限、出力上限・証跡|
|`--host-sidefx-proof`|許可外ファイル拒否、実Host relayでの日本語編集、承認binding、異常終了後の同一タスク状態|
|`--pi-runtime-options-proof`|専用Pi実行先の設定保存、不正・相対・改行入りパスの拒否|

全体試験は実行していません。承認bindingの合成試験を、人間の実承認として扱いません。
Pi 1.4部品起動と Gateway Qwen応答は確認済みですが、Windows Host→Pi→Bridge→Qwen の
実承認付き一連の試験は未完了です。新AgentV3の完成はこの確認の条件ではありません。

## PC73で行う実承認付き確認（自動実行しない）

PC70には対象Clientの設定・CurrentUser証明書と既存`Ubuntu`のruntimeが揃っていません。
PC73の既存資格情報をPC70へコピーしたり、新証明書で代用せず、必要ならPC73の利用者が以下を行います。

1. PC70の検証済みClientコミットを別の開発用フォルダーに用意します。本番cloneのcheckoutや
   稼働Hostのバイナリは変更しません。`.NET 10`の通常ビルド出力を使用し、上の限定EXE試験を必要な範囲で確認します。
   ブロックされたらDLL実行へ切替せず、環境名・時刻・EXE/DLL・原文・終了コードを保存して止めます。
   実承認を使う前に、同梱の既存`tools/deployment/Initialize-NativeConfirmationBindings.ps1 -PublishDirectory <試験Host出力先>`
   で確認スクリプトのハッシュbindingを準備します。これは証明書発行・署名・人間承認の代行ではありません。
2. WSL `Ubuntu`、既存のqualified Qwen tokenizer、既存Bridgeの設定・稼働を読み取り確認します。
   Pi 1.4.0は固定済みlockから専用ディレクトリへ用意し、`--version`を確認します。
   旧グローバルPi、旧セッション、既存証明書は変更しません。
3. 既存`clientsettings.json`をローカルの試験フォルダーへ複製し、`projectsFile`と`auditLog`を試験専用へ、
   `piRuntime.piExecutable`と`piRuntime.agentDirectory`を専用Pi/保存先へ向けます。
   `agentDirectory/settings.json`には同梱` scripts/pi-settings.json`を配置します。
   `v3Development`は無効、Gateway/Bridge/証明書の設定は元の値を保持します。この設定はGitに登録しません。
4. 新しい空のGitリポジトリを試験専用に作り、READMEと`.localbrain/`を除外する`.gitignore`をコミットします。
   `LOCALBRAIN_CLIENT_CONFIG`をそのPowerShellセッションだけ試験設定へ向け、
   `localbrain.exe register-project <試験repo>`で試験用allowlistだけへ登録します。
5. まず `"Reply only LOCALBRAIN_PI_140_OK" | localbrain.exe agent-v2 critic <試験repo>` を実行します。
   これはツール無効の読み取り試験です。応答、ToolErrors=0、終了0を確認し、承認や編集の証明とは扱いません。
6. LOWの小タスクを `localbrain.exe agent-v2 run <試験repo>` の標準入力JSONから投入します。
   許可ファイルは`result.txt`だけ、期待内容は`LOCALBRAIN_PI_140_OK`とLF、必須試験は`git_diff_check`、
   command_executionは試験repo/専用outputだけ・最大5分/3actor turns/1command/出力4096bytesに限定します。
   Qwenにはshellを使わず`host_write`だけで作成させます。既存のネイティブ承認画面は**人間が内容を確認**します。
   `queued`を成功とせず、`agent-v2 managed-status <試験repo>`のCanonical完了とファイル内容・許可外変更なしを確認します。
   JSON例は以下です。`__OUTPUT_ABSOLUTE_PATH__`は人間が作成した試験専用outputディレクトリへ置換します。

   ```json
   {"requirement":"Use host_write once to create result.txt containing LOCALBRAIN_PI_140_OK and one LF. Do not use shell or commands. Leave verification to the Host.","acceptance_criteria":["result.txt contains exactly LOCALBRAIN_PI_140_OK followed by LF; no other tracked file changes."],"risk":"LOW","approved_high_risk":false,"allowed_files":["result.txt"],"required_tests":[{"kind":"git_diff_check"}],"command_execution":{"environments":["windows"],"working_directories":["."],"output_directories":["__OUTPUT_ABSOLUTE_PATH__"],"max_command_seconds":5,"max_task_minutes":5,"max_actor_turns":3,"max_commands":1,"max_output_bytes":4096}}
   ```

   実Hostの既存CLIを使います。旧テスト補助の`--managed-agent-native-prepare`はこの実機確認には使用しません。
7. 必要な取消確認は、その試験タスクの正確なIDに対し
   `localbrain.exe agent-v2 managed-cancel <試験repo> <task_id>`を使います。
   他のタスクやプロセスは止めません。不明な停止結果は成功とせず、証跡を残して保留します。

実行後は環境変数を元に戻します。この経路は既存Canonicalへ試験タスクの正規証跡を追加するため、
既存作業のない時間を人間が選びます。既存Canonical・承認履歴は移動、置換、削除しません。
本番Continueの設定切替はこの結果を確認した後の別操作です。
