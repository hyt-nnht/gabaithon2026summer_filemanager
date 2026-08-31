from __future__ import annotations

import unittest
from pathlib import Path
from unittest.mock import patch

from file_analyzer.classifiers.rules import RuleBasedClassifier
from file_analyzer.classifiers.slm import LlamaCppSlmClassifier, parse_slm_json
from file_analyzer.errors import InvalidModelOutput
from file_analyzer.models import ClassificationCandidate
from file_analyzer.pipeline import AnalysisCoordinator


class ClassificationTests(unittest.TestCase):
    def test_rules_extract_invoice_metadata(self) -> None:
        result = RuleBasedClassifier().classify(
            "請求書\n発行元: サンプル株式会社\n請求日 2026年8月31日\n請求金額 3,980円"
        )
        self.assertEqual("invoice", result.document_type)
        self.assertEqual("サンプル株式会社", result.organization)
        self.assertEqual("2026-08-31", result.document_date)

    def test_slm_json_can_be_cut_out_of_extra_text(self) -> None:
        result = parse_slm_json(
            '結果です: {"document_type":"receipt","organization":"ABC",'
            '"document_date":"2026-08-31","confidence":0.8,"reason":"領収書表記"}'
        )
        self.assertEqual("receipt", result.document_type)
        self.assertEqual(0.8, result.confidence)

    def test_slm_rejects_unknown_document_type(self) -> None:
        with self.assertRaises(InvalidModelOutput):
            parse_slm_json(
                '{"document_type":"memo","organization":null,"document_date":null,'
                '"confidence":null,"reason":null}'
            )

    def test_slm_rejects_unknown_fields(self) -> None:
        with self.assertRaises(InvalidModelOutput):
            parse_slm_json(
                '{"document_type":"other","organization":null,"document_date":null,'
                '"confidence":null,"reason":null,"destination":"C:/unsafe"}'
            )

    def test_slm_rejects_missing_fields(self) -> None:
        with self.assertRaises(InvalidModelOutput):
            parse_slm_json('{"document_type":"other"}')

    def test_slm_invalid_optional_values_fall_back_to_none(self) -> None:
        result = parse_slm_json(
            '{"document_type":"invoice","organization":123,'
            '"document_date":"不明","confidence":2,"reason":null}'
        )

        self.assertIsNone(result.organization)
        self.assertIsNone(result.document_date)
        self.assertIsNone(result.confidence)

    def test_llama_cpp_classifier_uses_gemma_chat_format(self) -> None:
        created_options: list[dict[str, object]] = []
        completion_options: list[dict[str, object]] = []

        class FakeLlama:
            def __init__(self, **options: object) -> None:
                created_options.append(options)

            def create_chat_completion(self, **kwargs: object) -> dict[str, object]:
                completion_options.append(kwargs)
                return {
                    "choices": [
                        {
                            "message": {
                                "content": '{"document_type":"invoice","organization":null,'
                                '"document_date":null,"confidence":0.8,"reason":"請求書表記"}'
                            }
                        }
                    ]
                }

            def close(self) -> None:
                return None

        classifier = LlamaCppSlmClassifier(Path(__file__), unload_after_inference=True)
        baseline = ClassificationCandidate("other")

        with patch.dict("sys.modules", {"llama_cpp": type("FakeModule", (), {"Llama": FakeLlama})()}):
            result = classifier.classify("請求書", "invoice.pdf", baseline)

        self.assertEqual("invoice", result.document_type)
        self.assertEqual("gemma", created_options[0]["chat_format"])
        self.assertEqual(str(Path(__file__)), created_options[0]["model_path"])
        response_format = completion_options[0]["response_format"]
        self.assertEqual("json_object", response_format["type"])

    def test_merge_fills_missing_slm_fields_from_rules(self) -> None:
        baseline = ClassificationCandidate("invoice", "規則株式会社", "2026-08-30")
        ai = ClassificationCandidate("receipt", None, None, source="slm")

        result = AnalysisCoordinator._merge(baseline, ai)

        self.assertEqual("slm", result.decision_source)
        self.assertEqual("receipt", result.document_type)
        self.assertEqual("規則株式会社", result.organization)
        self.assertEqual("2026-08-30", result.document_date)


if __name__ == "__main__":
    unittest.main()
