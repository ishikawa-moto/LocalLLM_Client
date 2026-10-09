# LocalBrain v2 A/B比較：第1組（greeting.txt）

> **履歴資料：追加比較は終了しました。** 2026-09-25にユーザーはGSQ+Pi/SoL-Piを採用仕様として選び、これ以上のベンチマークを行わないことにしました。この文書は実施済みの既存経路と隔離Pi経路の小課題の証拠です。現行仕様は [GSQ_PI_SPEC.md](../GSQ_PI_SPEC.md) を参照してください。

## 目的と比較条件

既存のContinue経路（A）と、隔離したLocalBrain Agent v2経路（B）に**同じ作業**を依頼し、結果を比べます。作業は `greeting.txt` を作り、内容を `HELLO` とLF改行1個だけにすることです。正しいファイルは6バイトで、16進表記では `48454C4C4F0A` です。

| 条件 | A：既存経路 | B：Agent v2 |
| --- | --- | --- |
| 操作画面 | Continue Agent | Continue Agent |
| 実行経路 | Continueの組み込みツール → ローカルQwen | Continue → Agent v2 MCP → Pi / Laya → ローカルQwen → Critic / Sol |
| モデル | `local-qwen38`（既存Bridge経由） | `local-qwen38`（同じBridge経由） |
| Gitの出発点 | `1298d02ad5860fb9987356befe485f84b209c575` | 同じコミット |
| 作業用リポジトリ | `work/ab-benchmark/greeting-a` | `work/continue-pilot/sample-ide-repo-4` |

AとBは別々のコピーで実行します。Bの作業結果をAにコピーしたり、完了済みのBを再実行したりしません。経路の設計上、Bにだけ独立CriticとSolレビューがあります。Aに同じ欄がない場合は「該当機能なし」と記録します。

## 現在の進捗（2026-09-25）

**AとBの第1組は完了しました。**

- B：Continue画面で `localbrain_agent_v2_run` を1回、読取専用の `status` を1回実行し、両方の成功結果が表示されました。タスクIDは `f1a79b91d51a40b38053bc135f29dfc6`。ClientPC上でもファイルが正確に6バイト、変更ファイルがその1件だけであることを確認しました。`npm_test` と `git_diff_check` は2件とも通過、CriticとSolはPASS、Actorは1ターン、ツールエラーは0件、実行時間は52,829 msです。
- A：Continueから既存経路で実行しました。`greeting.txt` は `72,69,76,76,79,10` の6バイトで、Gitの変更はこの未追跡ファイル1件だけです。`npm test` と `git diff --check` はユーザー報告とCodex側の独立再実行の両方で成功しました。commitはありません。初回の `git diff --check` は所有者の相違による `dubious ownership` で拒否され、ユーザーがこの試験リポジトリだけを `git config --global --add safe.directory` に登録してから成功しました。リポジトリのファイルはこの設定では変更されませんが、グローバルGit設定には例外が残っています。Aの所要時間は未計測です。

| 確認項目 | A：既存経路 | B：Agent v2 |
| --- | --- | --- |
| `greeting.txt` が正確に6バイト | 合格 | 合格 |
| `npm test` と `git diff --check` | 2/2 合格 | 2/2 合格 |
| 余分な変更ファイル | 0件 | 0件 |
| ツールエラー | 初回にGit所有者エラー1件、解消後成功 | 0件 |
| 独立Critic / Sol | 該当機能なし | ともにPASS |
| 完了判定 | Continueが作業完了を報告、Codex側で成果物を確認 | `eligible_for_completion=true` |
| 実行時間 | 未計測 | 52,829 ms（Agent v2ホストの記録） |

`git diff --check` は未追跡の `greeting.txt` を検査しないため、6バイトの内容は別途ファイルから直接検証しました。A/Bの違いとして、AのGit所有者エラーは試験環境の所有権設定に起因し、エージェントの生成品質の差としては扱いません。

## A側の実行手順（実施済み・同じ試験は再実行不要）

1. VS Codeで上記の `greeting-a` フォルダーを開き、`Developer: Reload Window` を実行します。Continueでは既存の **LocalBrain Qwen 27B** を選びます。`LocalBrain Agent v2 pilot` は選びません。
2. このA側試験の間だけ、Continueの組み込み `create_new_file`、ターミナル実行、`edit_existing_file`、`single_find_and_replace` を `Automatic` または `Ask First` にします。ベースライン側がファイルを作り、テストを実行するためです。`fetch_url_content`、`search_web`、`read_skill`、ルール作成・取得はこの試験では使わないため `Excluded` にします。`read_file`、`ls`、ファイル検索などの読取ツールは `Automatic` のままで構いません。
3. Continueに次の作業を**1回だけ**依頼します。

   > このGitリポジトリに `greeting.txt` を作成し、内容を `HELLO` とLF改行1個だけ（計6バイト）にしてください。他のファイルは変更しないでください。作業後に `npm test` と `git diff --check` を実行し、それぞれの成否を報告してください。commitはしないでください。

4. Continueに表示された結果、実行されたツール、エラーの有無、完了と主張したかをこのCodexタスクに知らせてください。エラーが出たら再実行せずに知らせてください。ファイル内容とテストはCodex側でも独立に確認します。
5. A側の確認が終わったら、組み込みの書込・実行ツールを **`Excluded` に戻します**。B側試験と通常の隔離設定を保つためです。

## 判定の範囲

最初の1組は接続と検証方法を確かめるための比較です。両経路とも成果物と指定テストは合格しました。1件だけで「Agent v2は既存経路より良い／悪い」や「成功率が低下しない」とは結論しません。同じ条件で追加のコード編集課題を比較してから既定経路への切替を判断します。

Aの経路にないActorターン、Critic、Sol、Layaなどの値は0ではなく「該当機能なし」または「計測不可」と記録します。実行時間もAで同じ方法の記録が取れなければ、単純な速度優劣には使いません。試験用リポジトリの準備は、実運用のContinue設定やLocalBrainホストを変更しません。

既存コードの修正課題は未実行のまま中止され、試験用ファイルも削除しました。追加の比較試験は行いません。
