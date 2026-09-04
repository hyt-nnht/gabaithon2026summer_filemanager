# FileOrganizer.UI

仕様書 v3.6 に沿った .NET 10 / WPF フロントエンドです。現在は `ProductionBackendGateway` を使用し、既存のCore/Infrastructureへ接続済みです。監視、Dry Run後のファイル整理、SQLite履歴、Undo、C# OCR、ローカルPython解析を実際に呼び出します。

> 注意: 以前の画面確認版と違い、Dry Runで「実行」を押すとファイルが実際に移動・コピー・改名されます。上書きは行わず、実行直前にプレビュー内容を再検証します。

## 実装済み画面

- ホーム: 監視状態、監視先、当日件数、最近の履歴、処理パイプライン
- 整理ルール: 有効/無効、優先順位変更、条件・アクション編集、複製・追加・削除
- 実行履歴: 検索、状態フィルター、操作状態、Undo導線
- Dry Run: 対象の個別選択、移動前/移動後、ルール、競合警告、承認導線
- 設定: 監視フォルダ、安定確認、再走査、Quick Look、通知、SQLite WAL、Python/SLM
- Widgets: タスクトレイ、Drop Zone、SpaceキーQuick Look

## バックエンド統合ポイント

ViewModelからの呼び出しはすべて `Services/IFrontendBackendGateway.cs` に集約し、`ProductionBackendGateway.cs` が次の接続を担当します。

| UI操作 | 既存の接続先 | 実行時に考慮すること |
|---|---|---|
| 初期表示 | `ISettingsRepository`, `IHistoryRepository` | UIスレッドを塞がず、読込失敗は画面通知へ変換 |
| ルール/設定保存 | `ISettingsRepository` | ViewModelの編集用DTOから `RuleModel` / `AppSettings` へ変換後に保存 |
| 監視開始/停止 | `IWatcherService` | `WatchFolderSetting` を渡し、二重開始を防止 |
| 今すぐ整理のプレビュー | `DryRunSimulator` | 実I/Oなし。隠し/システム/ReparsePointを除外し、同名衝突を予測 |
| Dry Run承認後の実行 | `ProcessingCoordinator` | 全ファイルのサイズ・更新日時・ルール結果を先に再検証してから2フェーズ実行 |
| Undo | `IUndoManager` | `RequiresConfirmation` は自動実行せず画面で案内。Recycleは対象外 |
| OCR/AI状態 | `IContentTextExtractor`, `IPythonApiClient` | 本文抽出失敗時はルールベースへフォールバック。本文は永続化しない |
| 診断ログ | `LogMasker` + ZIP出力 | パスをSHA-256化し、個人情報をマスク。抽出本文は出力しない |

アプリ起動時は監視を開始しますが、その時点ですでに存在するファイルを自動投入しません。既存ファイルは「今すぐ整理」のDry Runで確認した場合だけ処理します。新しく作成・変更されたファイルは、デバウンスと2回の静止確認を通過してからルール評価されます。

Production版の起動順は `DatabaseInitializer` → `StartupRecoveryService` → `PythonProcessManager` → stdoutの `PORT:` を使った `IPythonApiClient.Configure` → `IWatcherService.StartAsync` とします。終了時は監視を止めてからPythonのJob Objectを破棄します。この順序にすることで、未復旧履歴が残ったまま新しい監視イベントを処理したり、IPC接続前にAI解析を始めたりすることを防ぎます。

## 本文抽出とPythonの境界

ファイルを開く担当はC#側で、TXTとDOCXは本文を直接読み、PDFと画像は `WindowsMediaOcrService` でOCRします。通常IPCでは、Pythonへ渡す `file_path` は表示名・拡張子を知るためのメタデータにすぎません。Pythonの `/api/v1/analyze` はこのパスを解決・検査・読込せず、必須の `ocr_text`（抽出本文）だけを解析します。抽出本文は履歴DBや診断ログへ保存しません。Pythonを起動できない場合も、拡張子・名前・サイズ・経過日数などの基本ルールは動作します。

## 初心者向け: ボタンを押した後の流れ

1. ViewはCommandを通してViewModelを呼びます。
2. ViewModelはファイルを直接操作せず、`IFrontendBackendGateway`へ依頼します。
3. Gatewayが設定・ルールを読み、`DryRunSimulator`で変更予定を作ります。
4. ユーザーが承認すると、Gatewayが予定をもう一度計算して同じか確認します。
5. `ProcessingCoordinator`がSQLiteへ`Planned`を記録し、`Executing`、成功時`Completed`へ更新します。
6. `FileOperationService`がWindowsの安全なファイル操作を実行します。移動先に同名があれば上書きせず連番を付けます。

## ビルド

Windows上で .NET 10 SDK を使います。

```powershell
dotnet build FileOrganizer.slnx
dotnet run --project FileOrganizer.UI/FileOrganizer.UI.csproj
```

ローカルAIも使う場合は、Windowsから実行できるPython環境に `py_service` の依存パッケージが必要です。起動に失敗してもUI全体は停止せず、基本ルールへ自動的に退避します。

## 操作履歴DBの確認

通常はUIの「実行履歴」画面で確認できます。SQLiteの全項目を直接確認する場合は、リポジトリルートから読み取り専用ツールを実行します。

```powershell
python .\tools\show_operation_history.py
python .\tools\show_operation_history.py --limit 100 --state Failed
python .\tools\show_operation_history.py --format json
```

既定では `%LocalAppData%\FileOrganizer\organizer.db` を読みます。ツールはSQLiteを `mode=ro` で開くため、履歴を変更しません。
