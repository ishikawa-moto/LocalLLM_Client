# Agent v2：タスク単位のコマンド実行

Continueは実装・ターミナル作業をWindows Hostへ渡します。Hostは速やかにタスクIDを返し、MCP接続とは独立してバックグラウンド実行を管理します。`queued`と`running`は完了を意味しません。

## 1タスクにつき1回の許可

`localbrain_agent_v2_run`は、正確な依頼、受入条件、編集範囲、必須テスト、`command_execution`を受け取ります。Windowsの確認画面にはこれらをまとめて表示します。実際のネイティブ確認で許可されるのは、その正確なタスクと検証済みの再開に属するコマンドだけです。モデルが渡す真偽値やリポジトリの文章は許可になりません。依頼・コマンド範囲・基準状態を変更する場合は、新しいタスクと許可が必要です。取消または完了で許可は終了します。記録済みの時間・ターン・コマンド数・出力量の上限は引き続き有効です。UACと再認証はOS上の操作です。

信頼できる個人プロジェクトのコードを使用してください。作業ディレクトリの制限だけで、任意プログラムのファイル・ネットワークアクセスが隔離されるわけではありません。Hostはジョブ所有権、受付条件、期限、出力量の上限を管理しますが、悪意あるスクリプトやデーモンの脱出に対する隔離を保証しません。

登録済みGitルートに対する`task`の例です。開始時の作業ツリーはクリーンである必要があります。

```json
{
  "requirement": "Run the requested Node CLI batch and retain one final CSV per ID. Preserve existing results.",
  "acceptance_criteria": ["Every requested ID has a verified final CSV; no existing result was overwritten."],
  "risk": "LOW",
  "approved_high_risk": false,
  "allowed_files": ["**"],
  "required_tests": [{"kind":"command_exit","target":"requested_batch"},{"kind":"git_diff_check"}],
  "command_execution": {
    "environments": ["windows","wsl"],
    "working_directories": ["."],
    "output_directories": ["../out"],
    "max_command_seconds": 21600,
    "max_task_minutes": 480,
    "max_actor_turns": 60,
    "max_commands": 256,
    "max_output_bytes": 2097152
  }
}
```

`**`や`src/**`の範囲指定にはコマンド契約が必要で、ネイティブの許可画面にも表示します。従来の正確なファイル単位のタスクハッシュとの互換性は維持します。ファイルツールは保護されたメタデータや秘密情報のパスを引き続き除外します。開始時のクリーンな作業ツリーと、上限付きレビューパケットの要件も維持します。受付条件を満たすために既存の未コミット変更を自動コミット・stash・削除することはありません。HEADを変更するGit操作は、通常の同一HEADタスク契約の対象外です。

## 実行と証跡

Actorは`host_command_start`、`host_command_status`、`host_command_cancel`を使います。プログラムと引数は別フィールドです。Windows Jobは所有する子孫プロセスを終了させます。WSLでは、Voltaなどの設定済み環境管理ツールを読み込むログインシェル、所有するプロセスグループ、短時間のHostリースを使います。長時間コマンドはContinueのターミナル期限に依存しません。

各操作には安定したID、変更不可の実行意図、上限付きの生ログ、終了記録、終了コード、宣言済み出力のハッシュがあります。宣言する出力は安定した最終ファイルにしてください。後続操作で名前が変わる一時的な`result.csv`は宣言しません。バッチでは、その操作内で名前変更後の最終出力をすべて検証します。期待出力の検証には、1ファイル128MiB、合計512MiB、15秒、最大256ファイルの上限があります。上限超過や結果不明の操作は成功と認定しません。

同じ依頼を繰り返し送信した場合は既存タスクを観測します。同じ操作IDを異なる引数で再利用することは拒否します。終了結果がない実行意図は`outcome_unknown`とし、別IDで自動再実行しません。取消は正確なタスクを対象とし、記録を保持します。workerの識別情報がない場合や起動結果が不明な場合は、自動再起動せず調査します。

実際の実行記録を単一のCanonical書込処理とTaskMemoryへ渡します。Layaが数えるのはHostが検証した新しい進捗であり、繰り返しの状態照会やモデルの完了宣言ではありません。workerは元の有限な予算内で、通常のActor処理分割やコンテキスト引継ぎをまたいで継続します。完了判定には通常テスト、新しいローカルCritic、既存のリスク別外部レビュー条件を引き続き使います。実際の進捗停止、不正な証跡、範囲・基準状態のずれ、レビュー不能、取消、予算枯渇は、正直に報告する停止条件です。

管理対象のモデル呼出しは、受理済みプロバイダーペイロードに結び付いた1回限りのHost実行資格を取得します。その資格がある場合だけ、Bridgeでの会話単位のLaya・TaskMemory処理の二重適用を回避できます。通常の会話では履歴管理とループ監視を維持し、どの経路でも通信期限を維持します。呼出し側が任意のHTTPラベルを付けても管理経路は選べません。

## Continueの設定

コーディング、ファイル変更、スクリプト、ビルド、テストにはAgent v2を使います。直接の読み取り専用調査と通常チャットは引き続き利用できます。Continueの公式ツール設定画面で、組込のファイル変更・ターミナルツールを`Excluded`にし、Agent v2のrun/resume/status/cancelを希望する設定にします。run/resumeを`Automatic`にするとContinue側の重複確認を省けますが、タスク単位の実際のネイティブ許可は残ります。ユーザー設定を偽装するために拡張機能の内部状態を編集してはいけません。新しいMCP実行ファイルを有効化した後はVS Codeのウィンドウを再読み込みします。

`localbrain_agent_v2_status`はバックグラウンドの所有状態とCanonicalの完了状態を返します。状態照会を繰り返すループは避けてください。Continueが現在の回答を終えてもHostは継続します。停止するときは、現在の正確なタスクIDで`localbrain_agent_v2_cancel`を使います。対応するローカルCLIは`agent-v2 managed-status <registered-root>`と`managed-cancel <registered-root> <task-id>`です。

## 実行環境のパッケージ化

先にビルドと通常回帰テストを行い、実際に試験したアセンブリとスクリプトを発行します。インストーラーは、配置済み`Confirm-TaskCommands.ps1`のSHA256を持つ`task-command-native-code.json`を書き込みます。Hostは未資格の確認コードを拒否します。非公開のClient設定はGit外に置きます。この機能を有効化するためにCanonical DBを置換・削除してはいけません。Serverのモデル・コンテキスト、ClientのTaskMemory、SecondBrainの方針を保持します。
