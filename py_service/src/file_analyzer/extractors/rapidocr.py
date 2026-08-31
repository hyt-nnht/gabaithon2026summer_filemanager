from __future__ import annotations

from pathlib import Path
from statistics import fmean
from threading import Lock
from time import perf_counter
from typing import Any

from ..errors import AnalysisError
from ..models import ExtractionResult
from ..text import normalize_text


class RapidOcrExtractor:
    """Lazy, singleton-style RapidOCR adapter for images and rendered PDF pages."""

    def __init__(self) -> None:
        self._engine: Any | None = None
        self._engine_lock = Lock()

    @property
    def available(self) -> bool:
        try:
            import rapidocr  # noqa: F401
        except ImportError:
            return False
        return True

    def warmup(self) -> None:
        self._get_engine()

    def _get_engine(self) -> Any:
        if self._engine is not None:
            return self._engine
        with self._engine_lock:
            if self._engine is None:
                try:
                    from rapidocr import RapidOCR
                except ImportError as exc:
                    raise AnalysisError(
                        "OCR_DEPENDENCY_MISSING",
                        "OCR依存関係（rapidocr/onnxruntime）がインストールされていません",
                        500,
                    ) from exc
                self._engine = RapidOCR()
        return self._engine

    @staticmethod
    def _output_lines(output: Any) -> tuple[list[str], list[float]]:
        if output is None:
            return [], []

        txts = getattr(output, "txts", None)
        scores = getattr(output, "scores", None)
        if txts is not None:
            return [str(value) for value in txts], [float(value) for value in (scores or [])]

        # Compatibility with RapidOCR versions that return (result, elapsed).
        raw = output[0] if isinstance(output, tuple) else output
        if not raw:
            return [], []
        lines: list[str] = []
        confidences: list[float] = []
        for item in raw:
            if len(item) >= 2:
                lines.append(str(item[1]))
            if len(item) >= 3:
                confidences.append(float(item[2]))
        return lines, confidences

    def _recognize(self, image: Any) -> tuple[str, list[float]]:
        output = self._get_engine()(image)
        lines, scores = self._output_lines(output)
        return normalize_text("\n".join(lines)), scores

    def extract_image(self, file_path: Path) -> ExtractionResult:
        started = perf_counter()
        try:
            text, scores = self._recognize(str(file_path))
        except AnalysisError:
            raise
        except Exception as exc:
            raise AnalysisError("OCR_FAILED", "画像のOCRに失敗しました") from exc
        if not text:
            raise AnalysisError("OCR_FAILED", "画像から文字を抽出できませんでした")
        return ExtractionResult(
            text=text,
            source="rapidocr",
            confidence=fmean(scores) if scores else None,
            page_count=1,
            elapsed_ms=round((perf_counter() - started) * 1_000),
        )

    def extract_pdf(self, file_path: Path, *, max_pages: int, render_scale: float) -> ExtractionResult:
        started = perf_counter()
        try:
            import pypdfium2 as pdfium
        except ImportError as exc:
            raise AnalysisError(
                "PDF_OCR_DEPENDENCY_MISSING",
                "スキャンPDFのOCRにはpypdfium2が必要です",
                500,
            ) from exc

        texts: list[str] = []
        scores: list[float] = []
        document = None
        try:
            document = pdfium.PdfDocument(str(file_path))
            page_count = len(document)
            for index in range(min(page_count, max_pages)):
                page = document[index]
                bitmap = None
                try:
                    bitmap = page.render(scale=render_scale)
                    # to_numpy() is a view over the bitmap, so OCR must finish
                    # before the bitmap is closed.
                    page_text, page_scores = self._recognize(bitmap.to_numpy())
                    if page_text:
                        texts.append(page_text)
                        scores.extend(page_scores)
                finally:
                    if bitmap is not None:
                        bitmap.close()
                    page.close()
        except AnalysisError:
            raise
        except Exception as exc:
            raise AnalysisError("OCR_FAILED", "スキャンPDFのOCRに失敗しました") from exc
        finally:
            if document is not None:
                document.close()

        text = normalize_text("\n".join(texts))
        if not text:
            raise AnalysisError("OCR_FAILED", "スキャンPDFから文字を抽出できませんでした")
        return ExtractionResult(
            text=text,
            source="rapidocr_pdf",
            confidence=fmean(scores) if scores else None,
            page_count=page_count,
            elapsed_ms=round((perf_counter() - started) * 1_000),
        )
