from __future__ import annotations

from pathlib import Path

from ..errors import AnalysisError
from ..models import ExtractionResult, WarningItem
from ..text import meaningful_character_count
from .pdf import PdfExtractor
from .rapidocr import RapidOcrExtractor


class TextExtractionRouter:
    IMAGE_SUFFIXES = {".png", ".jpg", ".jpeg"}

    def __init__(
        self,
        pdf_extractor: PdfExtractor,
        ocr_extractor: RapidOcrExtractor,
        *,
        min_pdf_text_chars: int = 50,
        max_ocr_pdf_pages: int = 3,
        pdf_render_scale: float = 2.5,
    ) -> None:
        self.pdf_extractor = pdf_extractor
        self.ocr_extractor = ocr_extractor
        self.min_pdf_text_chars = min_pdf_text_chars
        self.max_ocr_pdf_pages = max_ocr_pdf_pages
        self.pdf_render_scale = pdf_render_scale

    def extract(self, file_path: Path) -> ExtractionResult:
        suffix = file_path.suffix.lower()
        if suffix in self.IMAGE_SUFFIXES:
            return self.ocr_extractor.extract_image(file_path)
        if suffix != ".pdf":
            raise AnalysisError("UNSUPPORTED_TYPE", "対応形式はPDF、PNG、JPG、JPEGです")

        # pypdf is deliberately always attempted first. OCR is only the fallback
        # for PDFs whose text layer is absent or too small to be useful.
        try:
            pdf_text = self.pdf_extractor.extract(file_path)
        except AnalysisError as exc:
            if exc.code != "PDF_EXTRACTION_FAILED":
                raise
            ocr_result = self._ocr_pdf(file_path)
            ocr_result.warnings.append(
                WarningItem(
                    code="PDF_TEXT_EXTRACTION_FAILED",
                    message="PDF本文を抽出できなかったためPDF画像へOCRを適用しました",
                )
            )
            return ocr_result
        if meaningful_character_count(pdf_text.text) >= self.min_pdf_text_chars:
            return pdf_text

        ocr_result = self._ocr_pdf(file_path)
        ocr_result.warnings.append(
            WarningItem(
                code="PDF_TEXT_LAYER_INSUFFICIENT",
                message="有効なテキスト層がないためPDF画像へOCRを適用しました",
            )
        )
        return ocr_result

    def _ocr_pdf(self, file_path: Path) -> ExtractionResult:
        return self.ocr_extractor.extract_pdf(
            file_path,
            max_pages=self.max_ocr_pdf_pages,
            render_scale=self.pdf_render_scale,
        )
