from __future__ import annotations

import os
import asyncio
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path
from unittest.mock import patch

import uvicorn
from fastapi import HTTPException

from file_analyzer.__main__ import IpcServer
from file_analyzer.api.app import build_ipc_response, create_app
from file_analyzer.api.contracts import AnalyzeRequest
from file_analyzer.classifiers.rules import RuleBasedClassifier
from file_analyzer.config import Settings
from file_analyzer.models import ExtractionResult
from file_analyzer.pipeline import AnalysisCoordinator


class FailingExtractionRouter:
    def extract(self, file_path: Path) -> object:
        raise AssertionError("provided ocr_text must bypass Python extraction")


class UnusedSlm:
    pass


class RecordingExtractionRouter:
    def __init__(self, result: ExtractionResult) -> None:
        self.result = result
        self.received_path: Path | None = None

    def extract(self, file_path: Path) -> ExtractionResult:
        self.received_path = file_path
        return self.result


class IpcContractTests(unittest.TestCase):
    def test_request_matches_csharp_snake_case_contract(self) -> None:
        request = AnalyzeRequest.model_validate(
            {
                "file_path": "/documents/invoice.pdf",
                "ocr_text": "請求書",
                "extract_fields": ["date", "company", "document_type", "category"],
            }
        )

        self.assertEqual("/documents/invoice.pdf", request.file_path)
        self.assertEqual(["date", "company", "document_type", "category"], request.extract_fields)

    def test_response_matches_csharp_contract_and_requested_fields(self) -> None:
        result = {
            "final_decision": {
                "decision_source": "slm",
                "document_type": "invoice",
                "organization": "合同会社テックサプライ",
                "document_date": "2026-08-25",
                "suggested_base_name": "2026-08-25_合同会社テックサプライ_請求書",
            },
            "ai_suggestion": {"confidence": 0.95},
        }

        response = build_ipc_response(result, ["date", "company", "document_type", "category"])

        self.assertTrue(response.success)
        self.assertEqual("請求書", response.category)
        self.assertEqual(
            {
                "date": "2026-08-25",
                "company": "合同会社テックサプライ",
                "document_type": "請求書",
                "category": "請求書",
            },
            response.metadata,
        )
        self.assertEqual(0.95, response.confidence)

    def test_provided_ocr_text_bypasses_python_text_extraction(self) -> None:
        coordinator = AnalysisCoordinator(
            Settings(allowed_root=Path.cwd()),
            FailingExtractionRouter(),  # type: ignore[arg-type]
            RuleBasedClassifier(),
            UnusedSlm(),  # type: ignore[arg-type]
        )

        with patch.object(coordinator, "_validate_file", return_value=Path("invoice.pdf")):
            result = coordinator.analyze(
                job_id="ipc",
                file_path="invoice.pdf",
                expected_size=None,
                expected_last_write_utc=None,
                analysis_mode="rules_only",
                provided_ocr_text="請求書\n発行元: サンプル株式会社\n2026年8月25日",
            )

        self.assertEqual("provided_ocr_text", result["extraction"]["source"])
        self.assertEqual("invoice", result["final_decision"]["document_type"])

    def test_ocr_text_extracts_all_supported_document_types(self) -> None:
        coordinator = AnalysisCoordinator(
            Settings(allowed_root=Path.cwd()),
            FailingExtractionRouter(),  # type: ignore[arg-type]
            RuleBasedClassifier(),
            UnusedSlm(),  # type: ignore[arg-type]
        )
        samples = {
            "receipt": "領収書\n発行元: デモ商店株式会社\n2026年9月1日\n合計 3,980円\n税込",
            "invoice": "請求書\n発行元: デモソリューション株式会社\n2026年9月1日\n請求金額 55,000円\n支払期限 2026年9月30日",
            "meeting_minutes": "会議議事録\n発行元: ガバイソン開発チーム\n開催日 2026年9月1日\n出席者 山田、佐藤\n議題 デモ準備\n決定事項 分類機能を完成させる",
            "contract": "業務委託契約書\n発行元: デモ株式会社\n契約日 2026年9月1日\n甲 デモ株式会社\n乙 サンプル合同会社\n契約期間 2026年9月1日から\n署名",
        }
        expected_labels = {
            "receipt": "領収書",
            "invoice": "請求書",
            "meeting_minutes": "議事録",
            "contract": "契約書",
        }

        with patch.object(coordinator, "_validate_file", return_value=Path("sample.txt")):
            for document_type, text in samples.items():
                with self.subTest(document_type=document_type):
                    result = coordinator.analyze(
                        job_id=document_type,
                        file_path="sample.txt",
                        expected_size=None,
                        expected_last_write_utc=None,
                        analysis_mode="rules_only",
                        provided_ocr_text=text,
                    )
                    response = build_ipc_response(result, ["date", "company", "document_type", "category"])

                    self.assertEqual(document_type, result["final_decision"]["document_type"])
                    self.assertEqual("2026-09-01", result["final_decision"]["document_date"])
                    self.assertIsNotNone(result["final_decision"]["organization"])
                    self.assertTrue(response.success)
                    self.assertEqual(expected_labels[document_type], response.category)

    def test_missing_ocr_fields_are_omitted_from_contract_metadata(self) -> None:
        coordinator = AnalysisCoordinator(
            Settings(allowed_root=Path.cwd()),
            FailingExtractionRouter(),  # type: ignore[arg-type]
            RuleBasedClassifier(),
            UnusedSlm(),  # type: ignore[arg-type]
        )

        with patch.object(coordinator, "_validate_file", return_value=Path("invoice.txt")):
            result = coordinator.analyze(
                job_id="missing-fields",
                file_path="invoice.txt",
                expected_size=None,
                expected_last_write_utc=None,
                analysis_mode="rules_only",
                provided_ocr_text="請求書\n請求金額 1,000円",
            )

        response = build_ipc_response(result, ["date", "company", "document_type", "category"])
        self.assertEqual("invoice", result["final_decision"]["document_type"])
        self.assertIsNone(result["final_decision"]["document_date"])
        self.assertIsNone(result["final_decision"]["organization"])
        self.assertEqual({"document_type": "請求書", "category": "請求書"}, response.metadata)

    def test_null_ocr_text_uses_python_extraction_before_contract_conversion(self) -> None:
        router = RecordingExtractionRouter(
            ExtractionResult(
                text="請求書\n発行元: テスト株式会社\n2026年9月1日\n請求金額 10,000円",
                source="pypdf",
                confidence=None,
                page_count=1,
                elapsed_ms=1,
            )
        )
        coordinator = AnalysisCoordinator(
            Settings(allowed_root=Path.cwd()),
            router,  # type: ignore[arg-type]
            RuleBasedClassifier(),
            UnusedSlm(),  # type: ignore[arg-type]
        )

        with patch.object(coordinator, "_validate_file", return_value=Path("invoice.pdf")):
            result = coordinator.analyze(
                job_id="no-ocr-text",
                file_path="invoice.pdf",
                expected_size=None,
                expected_last_write_utc=None,
                analysis_mode="rules_only",
                provided_ocr_text=None,
            )

        response = build_ipc_response(result, ["date", "company", "document_type"])
        self.assertEqual(Path("invoice.pdf"), router.received_path)
        self.assertEqual("pypdf", result["extraction"]["source"])
        self.assertEqual(
            {"date": "2026-09-01", "company": "テスト株式会社", "document_type": "請求書"},
            response.metadata,
        )

    def test_organizer_token_takes_precedence_over_legacy_token(self) -> None:
        environment = {
            "ORGANIZER_IPC_TOKEN": "organizer-token",
            "ANALYZER_BEARER_TOKEN": "legacy-token",
        }
        with patch.dict(os.environ, environment, clear=True):
            settings = Settings.from_env()

        self.assertEqual("organizer-token", settings.bearer_token)

    def test_csharp_api_routes_are_registered(self) -> None:
        app = create_app(Settings(allowed_root=Path.cwd()))
        paths = {route.path for route in app.routes}

        self.assertIn("/api/v1/health", paths)
        self.assertIn("/api/v1/warmup", paths)
        self.assertIn("/api/v1/analyze", paths)

    def test_ipc_token_is_validated_as_bearer_auth(self) -> None:
        app = create_app(Settings(allowed_root=Path.cwd(), bearer_token="secret"))
        route = next(route for route in app.routes if route.path == "/api/v1/analyze")
        authorize = route.dependant.dependencies[0].call

        with self.assertRaises(HTTPException) as raised:
            authorize("Bearer wrong")
        self.assertEqual(401, raised.exception.status_code)
        authorize("Bearer secret")

    def test_dynamic_port_is_announced_after_server_startup(self) -> None:
        server = IpcServer(uvicorn.Config("file_analyzer.api.app:app"), announced_port=54321)

        async def fake_startup(instance: uvicorn.Server, sockets: object = None) -> None:
            instance.started = True

        output = StringIO()
        with patch.object(uvicorn.Server, "startup", fake_startup), redirect_stdout(output):
            asyncio.run(server.startup())

        self.assertEqual("PORT: 54321", output.getvalue().strip())


if __name__ == "__main__":
    unittest.main()
