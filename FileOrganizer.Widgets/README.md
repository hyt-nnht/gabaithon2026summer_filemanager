# FileOrganizer.Widgets

仕様書 v3.6 のフロントエンドB領域です。`FileOrganizer.UI/App.xaml.cs`から本番処理へ接続されています。

- `TrayIconManager`: トレイ表示とメニューイベント。監視やDry Run自体は実行しません。
- `DropShelfWindow`: ファイルドロップと一覧表示。送信後はファイル単位のDry Runを開きます。
- `KeyboardHook`: SpaceのKeyDown以外をフック内で即座に次へ流します。
- `ExplorerSelectionProvider`: 前面Explorer、DirectUIHWND/SysListView32、Edit、IME変換、全画面、単一選択を確認します。
- `QuickLookPreviewProvider`: 許可したテキスト形式の先頭64K文字、またはファイル情報を読みます。
- `QuickLookController` / `QuickLookWindow`: 上記を接続してプレビューを表示します。
- `QuickLookActivationPolicy`: Space、IME、Explorer、Edit、ファイルリスト、全画面、選択パスをこの順に判定する副作用のないガードです。

トレイの監視切替は本番GatewayのWatcher、通知は処理完了イベントへ接続済みです。Drop Zoneは直接ファイルを変更せず、必ずDry Runを経由します。
