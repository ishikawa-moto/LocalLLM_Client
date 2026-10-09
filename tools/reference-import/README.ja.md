# ローカルPDFの取込

ClientPCで、既存LocalBrainに登録され、通常Agentタスクの記録がある作業フォルダーと、手元のPDFを指定します。

```powershell
& "$env:LOCALAPPDATA\LocalBrain\tools\reference-import-20261008\Import-Reference.ps1" `
  -PdfPath "C:\資料\参考資料.pdf" `
  -Workspace "D:\GitHub\登録済みプロジェクト" `
  -SourceUri "https://原本の公開元/参考資料.pdf"
```

SourceUriは出典の記録だけに使い、URLからのダウンロードはしません。作業フォルダーの登録は既存LocalBrain設定を使用します。まだ通常Agentタスクの記録がないフォルダーには取込できません。取込のために既存タスクの所有者を勝手に作り替えることはありません。

原本PDF、抽出文、ページ位置、ハッシュ、取得日時をClientPCの専用保存先に保持し、Windows Hostの正本へ「未検証の参照」として登録します。同じPDFの重複取込は同じ参照を返します。最大4MiB・200ページです。

画像だけのページ、空白のページ、暗号化PDFは、読み落としを避けるためこの入口では拒否します。OCRやパスワード解除は別途、明示して行ってください。

取込だけではモデルの起動や知識採用は行いません。資料の主張を現在の実装事実として使うには、既存reference-checkで現在のrepository/runtimeの証拠を確認し、その観測をreference-learningで候補にします。既存Reviewerが根拠と二回の判定を確認して採用します。一つのPDFを複数ページに分けても、複数の独立資料にはなりません。

保存量が増える点に注意してください。原本・抽出・正本証跡を自動削除しません。製品や接続設定を更新した後は、入口が古いhashを検出して停止する場合があります。その場合は現在の製品へ再結合し、推測で設定を上書きしないでください。

この操作入口の抽出には、現在のClientPCで確認済みのPython/pypdfを使用します。Pythonを移設・削除した場合は再設定が必要です。ServerPCの主推論設定は変更しません。