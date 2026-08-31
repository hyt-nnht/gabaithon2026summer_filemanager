from __future__ import annotations

import unittest
from pathlib import Path
from unittest.mock import patch

from file_analyzer.errors import AnalysisError
from file_analyzer.extractors.rapidocr import RapidOcrExtractor
from file_analyzer.extractors.router import TextExtractionRouter
from file_analyzer.models import ExtractionResult
from file_analyzer.text import meaningful_character_count, normalize_text


class FakePdfExtractor:
    def __init__(self, text: str) -> None:
        self.text = text
        self.calls = 0

    def extract(self, file_path: Path) -> ExtractionResult:
        self.calls += 1
        return ExtractionResult(self.text, "pypdf", None, 2, 1)


class FakeOcrExtractor:
    def __init__(self) -> None:
        self.pdf_calls = 0
        self.image_calls = 0

    def extract_pdf(self, file_path: Path, *, max_pages: int, render_scale: float) -> ExtractionResult:
        self.pdf_calls += 1
        return ExtractionResult("OCRで取得した本文", "rapidocr_pdf", 0.9, 2, 2)

    def extract_image(self, file_path: Path) -> ExtractionResult:
        self.image_calls += 1
        return ExtractionResult("画像本文", "rapidocr", 0.8, 1, 2)


class TextRoutingTests(unittest.TestCase):
    def test_normalizes_unicode_whitespace_and_controls(self) -> None:
        self.assertEqual("ABC 123\n請求書", normalize_text("ＡＢＣ\t１２３\x00\n\n 請求書 "))

    def test_meaningful_count_ignores_whitespace(self) -> None:
        self.assertEqual(3, meaningful_character_count(" A \n B\tC "))

    def test_text_pdf_does_not_invoke_ocr(self) -> None:
        pdf = FakePdfExtractor("請求書" * 20)
        ocr = FakeOcrExtractor()
        router = TextExtractionRouter(pdf, ocr, min_pdf_text_chars=50)

        result = router.extract(Path("invoice.pdf"))

        self.assertEqual("pypdf", result.source)
        self.assertEqual(1, pdf.calls)
        self.assertEqual(0, ocr.pdf_calls)

    def test_pdf_without_text_layer_invokes_ocr(self) -> None:
        pdf = FakePdfExtractor("短い")
        ocr = FakeOcrExtractor()
        router = TextExtractionRouter(pdf, ocr, min_pdf_text_chars=50)

        result = router.extract(Path("scan.pdf"))

        self.assertEqual("rapidocr_pdf", result.source)
        self.assertEqual(1, ocr.pdf_calls)
        self.assertEqual("PDF_TEXT_LAYER_INSUFFICIENT", result.warnings[0].code)

    def test_image_uses_ocr_directly(self) -> None:
        pdf = FakePdfExtractor("unused")
        ocr = FakeOcrExtractor()
        router = TextExtractionRouter(pdf, ocr)

        result = router.extract(Path("photo.jpg"))

        self.assertEqual("rapidocr", result.source)
        self.assertEqual(0, pdf.calls)
        self.assertEqual(1, ocr.image_calls)

    def test_image_ocr_library_error_is_normalized(self) -> None:
        extractor = RapidOcrExtractor()

        with patch.object(extractor, "_recognize", side_effect=RuntimeError("broken")):
            with self.assertRaises(AnalysisError) as raised:
                extractor.extract_image(Path("broken.png"))

        self.assertEqual("OCR_FAILED", raised.exception.code)


if __name__ == "__main__":
    unittest.main()
