from __future__ import annotations

import json
import re
from datetime import date
from pathlib import Path
from threading import Lock
from typing import Any

from ..errors import InvalidModelOutput, SlmUnavailable
from ..models import DOCUMENT_TYPES, ClassificationCandidate

_EXPECTED_KEYS = {"document_type", "organization", "document_date", "confidence", "reason"}


def _first_json_object(value: str) -> dict[str, Any]:
    decoder = json.JSONDecoder()
    for match in re.finditer(r"\{", value):
        try:
            result, _ = decoder.raw_decode(value[match.start() :])
        except json.JSONDecodeError:
            continue
        if isinstance(result, dict):
            return result
    raise InvalidModelOutput("SLM応答にJSONオブジェクトがありません")


def parse_slm_json(value: str) -> ClassificationCandidate:
    if len(value) > 8_000:
        raise InvalidModelOutput("SLM応答が長すぎます")
    data = _first_json_object(value)
    missing = _EXPECTED_KEYS - set(data)
    if missing:
        raise InvalidModelOutput(f"SLM応答に必須フィールドがありません: {sorted(missing)}")
    unknown = set(data) - _EXPECTED_KEYS
    if unknown:
        raise InvalidModelOutput(f"SLM応答に未定義フィールドがあります: {sorted(unknown)}")

    document_type = data.get("document_type")
    if document_type not in DOCUMENT_TYPES:
        raise InvalidModelOutput("SLM応答の文書種別が不正です")

    organization = data.get("organization")
    if organization is not None:
        if not isinstance(organization, str) or len(organization) > 100 or any(ord(c) < 32 for c in organization):
            organization = None
        else:
            organization = organization.strip() or None

    document_date = data.get("document_date")
    if document_date is not None:
        if not isinstance(document_date, str):
            document_date = None
        else:
            try:
                document_date = date.fromisoformat(document_date).isoformat()
            except ValueError:
                document_date = None

    confidence = data.get("confidence")
    if confidence is not None:
        if isinstance(confidence, bool) or not isinstance(confidence, (int, float)) or not 0 <= confidence <= 1:
            confidence = None
        else:
            confidence = float(confidence)

    reason = data.get("reason")
    if reason is not None and (not isinstance(reason, str) or len(reason) > 200):
        reason = None

    return ClassificationCandidate(
        document_type=document_type,
        organization=organization,
        document_date=document_date,
        confidence=confidence,
        reason=reason,
        source="slm",
    )


class LlamaCppSlmClassifier:
    """Embedded GGUF classifier backed by llama-cpp-python."""

    def __init__(
        self,
        model_path: Path | None,
        *,
        context_size: int = 4_096,
        threads: int | None = None,
        max_tokens: int = 384,
        input_chars: int = 4_000,
        unload_after_inference: bool = True,
    ) -> None:
        self.model_path = model_path
        self.context_size = context_size
        self.threads = threads
        self.max_tokens = max_tokens
        self.input_chars = input_chars
        self.unload_after_inference = unload_after_inference
        self._model: Any | None = None
        self._lock = Lock()

    @property
    def available(self) -> bool:
        if self.model_path is None or not self.model_path.is_file():
            return False
        try:
            import llama_cpp  # noqa: F401
        except ImportError:
            return False
        return True

    @property
    def model_name(self) -> str | None:
        return self.model_path.name if self.model_path else None

    def _load(self) -> Any:
        if self._model is not None:
            return self._model
        if self.model_path is None or not self.model_path.is_file():
            raise SlmUnavailable("Gemma GGUFモデルが設定されていません")
        try:
            from llama_cpp import Llama
        except ImportError as exc:
            raise SlmUnavailable("llama-cpp-pythonがインストールされていません") from exc
        options: dict[str, Any] = {
            "model_path": str(self.model_path),
            "n_ctx": self.context_size,
            "chat_format": "gemma",
            "verbose": False,
        }
        if self.threads is not None:
            options["n_threads"] = self.threads
        self._model = Llama(**options)
        return self._model

    def close(self) -> None:
        model, self._model = self._model, None
        if model is not None and callable(getattr(model, "close", None)):
            model.close()

    def classify(
        self,
        text: str,
        original_file_name: str,
        baseline: ClassificationCandidate,
    ) -> ClassificationCandidate:
        prompt = self._prompt(text, original_file_name, baseline)
        with self._lock:
            try:
                response = self._load().create_chat_completion(
                    messages=[{"role": "user", "content": prompt}],
                    temperature=0.1,
                    max_tokens=self.max_tokens,
                    response_format={"type": "json_object"},
                )
                content = response["choices"][0]["message"]["content"]
                if not isinstance(content, str):
                    raise InvalidModelOutput("SLM応答本文がありません")
                return parse_slm_json(content)
            except (KeyError, IndexError, TypeError) as exc:
                raise InvalidModelOutput("SLM応答形式が不正です") from exc
            except (InvalidModelOutput, SlmUnavailable):
                raise
            except Exception as exc:
                raise SlmUnavailable("SLM推論に失敗しました") from exc
            finally:
                if self.unload_after_inference:
                    self.close()

    def warmup(self) -> None:
        baseline = ClassificationCandidate(
            document_type="invoice",
            organization="サンプル株式会社",
            document_date="2026-08-31",
        )
        self.classify(
            "請求書\n発行元: サンプル株式会社\n請求日: 2026年8月31日\n請求金額: 3,980円",
            "warmup_invoice.txt",
            baseline,
        )

    def _prompt(
        self,
        text: str,
        original_file_name: str,
        baseline: ClassificationCandidate,
    ) -> str:
        baseline_json = json.dumps(baseline.public_dict(), ensure_ascii=False)
        return f"""あなたは文書分類器です。説明文やMarkdownを付けずJSONオブジェクトだけを返してください。
次の文書を分類してください。
許可するdocument_type: {json.dumps(DOCUMENT_TYPES, ensure_ascii=False)}
document_dateは実在する日付のYYYY-MM-DDまたはnullにしてください。不明な日付を空文字や「不明」で返さずnullにしてください。
organizationは100文字以下またはnull、confidenceは0から1またはnull、reasonは200文字以下またはnullにしてください。
出力キーは document_type, organization, document_date, confidence, reason の5個だけです。

元ファイル名: {Path(original_file_name).name}
規則ベース候補: {baseline_json}
<document>
{text[: self.input_chars]}
</document>"""
