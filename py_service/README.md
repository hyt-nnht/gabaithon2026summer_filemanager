# File Analyzer（最小構成）

ファイル整理アプリ向けの、読み取り専用Python解析サービスです。PDF・画像から文字を取り出し、規則分類を必ず作ったうえで、設定されていれば`llama-cpp-python`とGemma-2-2BのGGUFモデルでSLM分類します。Python側はファイルの移動・改名・削除をしません。

機能の詳細、APIの入出力、現在の制限については[機能説明書](./機能説明書.md)を参照してください。

## PDFの抽出ルール

```text
PDF
 ├─ pypdfの本文が50文字以上 ──> pypdf結果を採用（OCRしない）
 └─ 本文が50文字未満／抽出不能 ─> 先頭3ページを画像化してRapidOCR

PNG / JPG / JPEG ───────────────> RapidOCR
```

文字数の判定では空白と制御文字を除きます。閾値とOCRページ数は環境変数で変更できます。暗号化PDFはOCRへ迂回させず拒否します。

## セットアップ（Linux / WSL）

Python 3.11以上を使用します。リポジトリのルートで次を実行すると、OCR・SLM・テスト依存がすべて入ります。CPU版`llama-cpp-python`のビルド済みwheelを優先するため、専用インデックスを追加しています。

```bash
python3 -m venv py_service/.venv
./py_service/.venv/bin/python -m pip install --upgrade pip
./py_service/.venv/bin/python -m pip install -e 'py_service[ocr,slm,dev]' \
  --extra-index-url https://abetlen.github.io/llama-cpp-python/whl/cpu
```

すでに`[ocr,dev]`を導入済みなら、SLMだけを追加できます。

```bash
./py_service/.venv/bin/python -m pip install -e 'py_service[slm]' \
  --extra-index-url https://abetlen.github.io/llama-cpp-python/whl/cpu
```

## Gemmaモデルの取得

仕様書v3.6で指定された`Gemma-2-2B-Q4_K_M`を`py_service/models`へ取得します。モデル本体は依存パッケージではないため、`pip install`とは別に1回だけ実行します。

```bash
mkdir -p py_service/models
./py_service/.venv/bin/hf download \
  bartowski/gemma-2-2b-it-GGUF \
  gemma-2-2b-it-Q4_K_M.gguf \
  --local-dir py_service/models
```

## 起動

```bash
export ANALYZER_SLM_MODEL=/home/rdoki/projects/gabaithon26sm/py_service/models/gemma-2-2b-it-Q4_K_M.gguf
export ANALYZER_ALLOWED_ROOT=/home/rdoki/projects/gabaithon26sm
./py_service/.venv/bin/python -m file_analyzer --host 127.0.0.1 --port 8765
```

Windows PowerShellでは`ANALYZER_SLM_MODEL`と`ANALYZER_ALLOWED_ROOT`をWindows形式の絶対パスで設定してください。動的ポートを使う場合は`--port 0`を指定し、標準出力の`PORT=<実ポート>`をC#側で読み取ります。

## API

- `GET /v1/health`: PDF、OCR、SLMの利用可否
- `POST /v1/warmup`: OCRとSLMの事前ロード・疎通
- `POST /v1/analyze`: テキスト抽出、規則分類、SLM推論、最終候補の生成
- Swagger UI: `http://127.0.0.1:8765/docs`

`ANALYZER_BEARER_TOKEN`を設定した場合は、全APIへ`Authorization: Bearer <token>`が必要です。

解析例:

```json
{
  "schema_version": "1.0",
  "job_id": "demo-001",
  "file_path": "C:\\Demo\\Inbox\\invoice.pdf",
  "analysis_mode": "slm_with_rules_fallback",
  "language": "ja"
}
```

SLMが未設定、推論失敗、JSON不正の場合もHTTP 200の`partial`として規則結果を返します。`rules_only`を指定すればSLMを呼びません。抽出全文はAPIレスポンスやログに出さず、レスポンスのプレビューは先頭1,000文字だけです。

## 主な環境変数

| 変数 | 既定値 | 用途 |
|---|---:|---|
| `ANALYZER_ALLOWED_ROOT` | 起動ディレクトリ | 読み取りを許可するルート |
| `ANALYZER_BEARER_TOKEN` | 未設定 | localhost APIの認証トークン |
| `ANALYZER_MIN_PDF_TEXT_CHARS` | `50` | テキストPDFと判断する最小文字数 |
| `ANALYZER_MAX_PDF_PAGES` | `10` | pypdfで読む最大ページ数 |
| `ANALYZER_MAX_OCR_PDF_PAGES` | `3` | スキャンPDFをOCRする最大ページ数 |
| `ANALYZER_PDF_RENDER_SCALE` | `2.5` | PDF画像化の倍率（約180 DPI） |
| `ANALYZER_SLM_MODEL` | 未設定 | Gemma GGUFモデルの絶対パス |
| `ANALYZER_SLM_CONTEXT_SIZE` | `4096` | SLMコンテキスト長 |
| `ANALYZER_SLM_THREADS` | 自動 | CPU推論スレッド数 |
| `ANALYZER_SLM_MAX_TOKENS` | `384` | SLM出力トークン上限 |
| `ANALYZER_SLM_UNLOAD` | `true` | 推論後にモデルを解放するか |

デモPCで動作確認後に再現用バージョンを記録する場合は、同じ仮想環境で次を実行します。

```bash
./py_service/.venv/bin/python -m pip freeze > py_service/requirements-lock.txt
```

## テスト

外部ライブラリを使わない単体テストは次のコマンドで実行できます。

```bash
./py_service/.venv/bin/python -m pytest -q py_service/tests
```
