from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path
from threading import Lock
from time import perf_counter
from typing import Any, Literal

from .classifiers.rules import RuleBasedClassifier
from .classifiers.slm import LlamaCppSlmClassifier
from .config import Settings
from .errors import AnalysisError, SlmError
from .extractors.router import TextExtractionRouter
from .models import ClassificationCandidate, FinalDecision, WarningItem
from .naming import suggest_base_name


class AnalysisCoordinator:
    def __init__(
        self,
        settings: Settings,
        extraction_router: TextExtractionRouter,
        rules: RuleBasedClassifier,
        slm: LlamaCppSlmClassifier,
    ) -> None:
        self.settings = settings
        self.extraction_router = extraction_router
        self.rules = rules
        self.slm = slm
        self._analysis_lock = Lock()

    def analyze(
        self,
        *,
        job_id: str,
        file_path: str,
        expected_size: int | None,
        expected_last_write_utc: datetime | None,
        analysis_mode: Literal["rules_only", "slm_with_rules_fallback"],
    ) -> dict[str, Any]:
        started = perf_counter()
        path = self._validate_file(file_path, expected_size, expected_last_write_utc)
        with self._analysis_lock:
            extraction = self.extraction_router.extract(path)
            baseline = self.rules.classify(extraction.text, path.name)
            ai_suggestion: ClassificationCandidate | None = None
            warnings = list(extraction.warnings)
            fallback_used = False

            if analysis_mode == "slm_with_rules_fallback":
                try:
                    ai_suggestion = self.slm.classify(extraction.text, path.name, baseline)
                except SlmError as exc:
                    fallback_used = True
                    warnings.append(WarningItem(exc.code, self._slm_warning_message(exc.code)))

            decision = self._merge(baseline, ai_suggestion)
            decision.suggested_base_name = suggest_base_name(decision)
            return {
                "schema_version": "1.0",
                "job_id": job_id,
                "status": "partial" if fallback_used else "success",
                "extraction": extraction.public_dict(self.settings.text_preview_chars),
                "baseline": baseline.public_dict(),
                "ai_suggestion": ai_suggestion.public_dict(include_details=True) if ai_suggestion else None,
                "final_decision": decision.to_dict(),
                "fallback_used": fallback_used,
                "warnings": [warning.to_dict() for warning in warnings],
                "error": None,
                "elapsed_ms": round((perf_counter() - started) * 1_000),
            }

    @staticmethod
    def _merge(
        baseline: ClassificationCandidate,
        ai_suggestion: ClassificationCandidate | None,
    ) -> FinalDecision:
        if ai_suggestion is None:
            return FinalDecision(
                decision_source="rules",
                document_type=baseline.document_type,
                organization=baseline.organization,
                document_date=baseline.document_date,
                destination_key=baseline.document_type,
            )
        return FinalDecision(
            decision_source="slm",
            document_type=ai_suggestion.document_type,
            organization=ai_suggestion.organization or baseline.organization,
            document_date=ai_suggestion.document_date or baseline.document_date,
            destination_key=ai_suggestion.document_type,
        )

    def _validate_file(
        self,
        file_path: str,
        expected_size: int | None,
        expected_last_write_utc: datetime | None,
    ) -> Path:
        requested = Path(file_path)
        try:
            path = requested.resolve(strict=True)
        except FileNotFoundError as exc:
            raise AnalysisError("FILE_NOT_FOUND", "指定ファイルが存在しません", 404) from exc
        if not path.is_file():
            raise AnalysisError("FILE_NOT_FOUND", "指定パスはファイルではありません", 404)
        try:
            path.relative_to(self.settings.allowed_root)
        except ValueError as exc:
            raise AnalysisError("PATH_NOT_ALLOWED", "許可ルート外のファイルは解析できません") from exc

        stat = path.stat()
        if stat.st_size > self.settings.max_file_bytes:
            raise AnalysisError("FILE_TOO_LARGE", "ファイルサイズが上限を超えています")
        if expected_size is not None and stat.st_size != expected_size:
            raise AnalysisError("FILE_CHANGED", "解析開始前にファイルサイズが変わりました", retryable=True)
        if expected_last_write_utc is not None:
            expected = expected_last_write_utc
            if expected.tzinfo is None:
                expected = expected.replace(tzinfo=timezone.utc)
            actual = datetime.fromtimestamp(stat.st_mtime, timezone.utc)
            if abs((actual - expected.astimezone(timezone.utc)).total_seconds()) > 1:
                raise AnalysisError("FILE_CHANGED", "解析開始前にファイル更新日時が変わりました", retryable=True)
        return path

    @staticmethod
    def _slm_warning_message(code: str) -> str:
        if code == "INVALID_MODEL_OUTPUT":
            return "SLM出力が不正なため規則分類を使用しました"
        return "SLMを利用できないため規則分類を使用しました"
