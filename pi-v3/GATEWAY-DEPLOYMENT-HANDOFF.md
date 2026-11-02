# PC70 Gateway 正式配備前の引き継ぎ

## 2026-11-03 の追加確認

最初のUAC承認は**読み取りのみ**で、実施済みです。繰り返し実行しません。
その後、PC73担当からユーザーの段階的本番配備許可と、Continue画面での実応答成功が通知されました。
したがって、現在の保留理由を「一般的な配備許可がない」「Continue未確認」とは扱いません。
今回の固定packetに対する保守時間の確認と、人間による正規UACは実行時に別途必要です。

過去に実配備した `slack-admission-deploy-20261026-55a2069` のForward packetも確認しました。
保存済みspecのhashは現行観測値と一致し、旧GO/適用receiptも整合しています。
ただし、旧GO・launcher・保守承認・PIDは当時の証跡であり、再使用しません。
ForwardはChat/Gateway双方の停止・更新と新プロセス2個を要求し、検査はSlackを含む全DB行・DB/WALを読みます。
ペア停止を許しても今回のSlack内容非参照条件や新payloadへの束縛は解決しません。

既存OS操作を再利用するGateway専用main、Chatの不変確認、本文を読まないDB identity/schema検査、
今回の146ファイル・新spec・fresh baselineに束縛したpacketと明示的な戻し手順が未完成です。
新しい大規模配備機構を作らない方針に従い、この更新だけを保留しました。
`Gateway-Priority-Draft.ps1` のApply/Restoreは実装されておらず、実行可能手順として提示しません。
本番タスク操作・配備ファイル変更は0です。現行GatewayとBrowser Chatは稼働を維持しています。

以下の現行bindingと切替条件は維持します。実配備前には必ず最新状態を再照合してください。

## 現行と候補

|対象|確認値|
|---|---|
|実機|PC70 REI|
|タスク|`LocalBrain Control Gateway`、SYSTEM、Running|
|現行ソース|`55a206943b43cf2041742049b7265333dff91ae8`|
|固定起動仕様|`C:\ProgramData\LocalBrainServer\chat-ocr-activations\slack-admission-20261026-55a206943b43-v1\startup-spec.json`|
|現行spec SHA-256|`d5fd6df8f64b67b11d3ec49a6ff7c41b902d94499d5a75c099d292a14826cd90`|
|配備候補ソース|Server `f5df6bd12e9a87333f06948966296f80b1167d35`|
|実行コード差分|`src/control_gateway.py`、`src/chat_ocr.py`、`src/slack_gateway.py`、合計+147/-33行|

PC70作業領域の`gateway-priority-proposal-20261102`に、現行リリースと同じ146個のソースパスを
候補コミットから読み出したpayload、SHA-256付きmanifest、3ファイルの差分を準備しました。
モデル・鍵・本番設定・DBはコピーしていません。これは**ソース確認用候補**であり、そのまま適用できる完成済みpacketではありません。

候補manifest SHA-256:
`deb7fd7cd050fa96931188c4d960949ee5df26a1ca2fc62099f3e5915209660b`

## 既存手順を無条件に使わない理由

現在は旧来のPython直接起動ではなく、固定spec・コードmanifest・既存DB identity・activation gateを使っています。
`Prepare-GatewayRelease.ps1`は既存ProgramDataルートがあると停止し、`Apply-ChatOcr.ps1`はChat/Gatewayのペアを対象とします。
このため、Gatewayだけの更新にどちらかを無条件で実行したり、保護された現行コードを直接書き換えたりしません。
既存のentry・gate・Git manifestを再利用し、適用と戻し先を現行bindingに固定する必要があります。新しい管理基盤は不要です。

## 次に人間承認が必要な操作と合否

1. 現行spec/タスクXML/DB identityと候補hashを再照合し、Gatewayだけを対象とする適用・復旧操作を確定します。
   旧release、旧activation、元タスクXML、データrootを保持します。設定値、認証、DB schema、履歴は変更しません。
2. 利用者がContinue等の依頼を止めた時間に、Gatewayのactive/queuedが0であることを確認します。
   許可する操作は新しい保護release/specの配置とGatewayタスクの停止1回・起動1回です。
   Browser Chat、Qwenのモデル設定、SecondBrain、PC73の本番設定は変更対象に含めません。
3. 停止後はタスクがReady、旧プロセス終了、Gatewayポート解放を確認します。PID消失だけで終了とせず、
   不明なら強制killや自動再試行をせず保留します。現行releaseへのin-place変更は禁止です。
4. 新しい固定specで起動し、既存gateを保持してmTLS `/health`、DB identity/既存schema、
   元のBrowser Chatの健康状態を確認します。未確認のactivation検証値を合格として作成しません。
5. 有効化後、最小の通信と、Continue ＞ Slack解析 ＞ Work、同順位FIFO、実行中推論の非割込みを確認します。
   PC73由来の優先クラスは実際の認証済みPC73から確認します。ヘッダーやループバックでPC73を偽装しません。
   未試験の優先クラスは未確認として残します。
6. 不合格時は旧タスク定義・旧固定spec/releaseへ戻す別操作を承認範囲に明示します。
   データや履歴を巻き戻さず、結果不明時は自動切替を繰り返しません。

正式配備は、上記を実行する固定操作が完成してからです。最新の段階的配備許可とは別に、
今回packetへのfresh maintenance束縛と人間のUACを確認します。旧読み取り承認を配備承認に流用しません。
