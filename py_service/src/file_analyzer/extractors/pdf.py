from __future__ import annotations

from pathlib import Path
from time import perf_counter

from ..errors import AnalysisError
from ..models import ExtractionResult
from ..text import normalize_text


class PdfExtractor:
    """Extract an existing PDF text layer. This class never performs OCR."""

    def __init__(self, max_pages: int = 10) -> None:
        self.max_pages = max_pages

    def extract(self, file_path: Path) -> ExtractionResult:
        started = perf_counter()
        try:
            from pypdf import PdfReader
        except ImportError as exc:
            raise AnalysisError("PDF_DEPENDENCY_MISSING", "pypdfがインストールされていません", 500) from exc

        try:
            reader = PdfReader(str(file_path))
            if reader.is_encrypted:
                raise AnalysisError("PDF_ENCRYPTED", "暗号化されたPDFは解析できません")
            page_count = len(reader.pages)
            pages = [reader.pages[index].extract_text() or "" for index in range(min(page_count, self.max_pages))]
        except AnalysisError:
            raise
        except Exception as exc:
            raise AnalysisError("PDF_EXTRACTION_FAILED", "PDFのテキスト抽出に失敗しました") from exc

        return ExtractionResult(
            text=normalize_text("\n".join(pages)),
            source="pypdf",
            confidence=None,
            page_count=page_count,
            elapsed_ms=round((perf_counter() - started) * 1_000),
        )
