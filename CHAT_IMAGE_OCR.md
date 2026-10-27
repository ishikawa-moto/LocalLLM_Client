# ブラウザーChatの画像OCR中継（候補）

> 記録した時点の候補・検証・配備状況を保持した日本語版です。過去の未確認事項や手順を、現在の残作業・実行指示と混同しないでください。現在の状態は実配置の記録・設定・プロセスと照合します。

ブラウザー向けのServer UIと、認証済みGatewayのメールボックスを、このClient中継と組み合わせて実装・資格確認する必要があります。このClient候補だけでは稼働中Chatにアップロードボタンは追加されません。本番は変更せず、設定ファイルがなければ機能は無効です。

## 再利用する処理と既定の出力

画像・PDFは、既存のOCRジョブ保存・スケジューラー、日本語モデル、CPU2/704MiB、AppContainer、通信禁止worker、優先度・チェックポイント機構を使います。モデル、新しいOCRエンジン、受信ポート、証明書、Cloud AIは追加しません。既定の出力はMarkdownで、表の結果には正本のJSONも含めます。HTML/CSVは明示指定です。Slackと同じ決定的なレンダラーを使います。OCRだけの処理はQwenを呼びません。解釈を依頼された場合はServerのローカル限定Chat経路で扱います。

この段落の既定値はブラウザーChatの契約です。Slackの返却形式は別に、HTML優先・困難時Markdown、構造化JSONは明示依頼時だけへ変更しました。決定的なOCRレンダラーは共用し、今回ブラウザー側の設定・API既定値は変更しません。

任意の専用設定`control-plane/chat-ocr/config.json`は、`enabled`と、Git外の非公開領域を指す絶対パス`root`を持ちます。この変更で本番ファイルは作成しません。設定がない場合や無効の場合、バックグラウンドタスクによるGateway/OCR呼出しは0回です。

## 提案する通信契約

PC73側で所有権を持つBridgeは、認証済み`POST /v1/chat-ocr/next`と`/v1/chat-ocr/update`だけを使います。設定済みの既存Host証明書・所有者ヘッダー、通常のプライベートLANのTLS検証を使い、プロキシ・リダイレクトは使いません。通常のBridge転送は、エスケープ表現・大文字小文字違いを含め、これらのパスを拒否します。Gatewayは固定したHost/IP/所有者を強制し、受付・状態・取消・成果物用には別の認証済みループバックChatロールを要求する必要があります。

`next`は`{"schema":"localbrain.chat-ocr.poll.v1"}`を受け取り、`{"job":null}`または`{"job":{...}}`を返します。ジョブスキーマ`localbrain.chat-ocr.job.v1`のフィールドは、`schema`、小文字32桁16進の`job_id`、UUID-D形式の`conversation_id`、小文字SHA256の`input_sha256`、`extension`、`formats`、任意のbase64 `bytes`、`cancel_requested`だけです。拡張子は`.png/.jpg/.webp/.pdf`に対応し、マジックバイト・ハッシュを検証します。デコード後の上限は4MiBです。`formats`には`md`を含み、`json/html/csv`も含められます。要求側はURL、入力・出力パス、実行ファイル、ツールを指定できません。未知のフィールドは拒否します。

`update`のスキーマ`localbrain.chat-ocr.update.v1`は、`job_id`/`input_sha256`と既知のOCR状態を結び付けます。進捗にはページ数と待機理由を含めます。最終完了には正本OCRの`result`、決定的な`artifacts`（形式・ファイル名・MIME・SHA256・base64bytes）、警告、external_ai=deny、automatic_knowledge_adoption=falseを含めます。各成果物は最大1MiB、報告全体は最大16MiBです。失敗・取消・期限切れ状態には結果ファイルを含めません。Serverは公開前に、参照元・会話・Hostの結び付き、ハッシュ、最終状態の冪等性、取消・期限切れ、許可出力形式を再検証する必要があります。取消ACKだけでOCR子プロセスの停止完了と判断してはいけません。

## 再起動とプライバシー

論理ジョブから、origin=chatの既存OCR要求を決定的に1件選択します。結果照会前に非公開の記録で会話・入力・形式を結び付けます。重複照会や最終応答の喪失時は固定済み報告を返し、OCRを再実行しません。取消要求は所有するOCRジョブを対象とし、取消状態を待ちます。ローカル完了後の取消で、固定した結果証跡を上書きしません。不明・破損した記録は調査用に保持します。

OCRが所有するスナップショットの登録後に入力の一時配置を削除します。既存スケジューラーがチェックポイント・再開、リソース上限、期限切れを扱います。中継の最終結果キャッシュには24時間の整理期間と256MiBの容量上限があります。Serverメールボックス側も、期限切れIDを再受付しないための有限の保持・削除済み記録の規則を持つ必要があります。メタデータログは固定コードだけです。画像内の文章をHostコマンドや知識採用の承認として扱いません。

## 資格確認

`dotnet run --project dotnet/LocalBrain.ClientHost.Tests -c Release -- --chat-ocr-proof`は、公開fixtureと偽HTTPを使い、入力形式・ハッシュ・マジックバイト、未知フィールド・パスフィールド、入力の権限、外部送信・Agent経路の拒否、キュー再利用、出力既定値、全形式、取消・再起動・応答喪失、破損時の保持、固定経路・認証、期限切れを確認します。既存のOCR保存・成果物・Slackファイル配送の検証も行います。これらは隔離した契約試験であり、実Gatewayとの相互運用、ブラウザーアップロード、配備、実OCR性能の資格確認ではありません。

## 言語をまたぐ契約の検証

テストハーネスは実際のClient DTO、処理系、成果物レンダラー、Gateway HTTP直列化と、実際のServer Pythonメールボックスを使います。`dotnet run --project dotnet/LocalBrain.ClientHost.Tests -c Release -- --chat-ocr-interop SERVER_SRC PYTHON NEW_PRIVATE_ROOT`で実行します。1/26/200ページの進捗・完了、既定と明示指定の成果物のハッシュ・取得、固定ACKの再取得、取消後の停止完了を確認します。`SERVER_SRC`はレビュー済みの対応Serverソース、または正確に取り出した`chat_ocr.py`、`PYTHON`は導入済みの実行環境を指定します。Git外の新しい保存ルートが必要です。

`--chat-ocr-interop-preserved SERVER_SRC PYTHON NEW_PRIVATE_ROOT PUBLIC_PNG PUBLIC_PAGE_JSON`では、入力ハッシュがPNGと一致する既存の公開OCRページも再利用します。OCR自体は再実行しません。fixtureの相互証明書・OCRページは合成データまたは保持済みの公開証跡であり、通信は非公開のサブプロセスパイプとHTTPハンドラーを使います。実mTLS、新しいOCR推論、配備済みプロセス、実ブラウザー通信を確認するものではありません。本番設定・データ・サービスは変更しません。
