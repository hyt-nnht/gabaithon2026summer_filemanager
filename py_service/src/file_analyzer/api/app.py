from __future__ import annotations

import hmac
import importlib.util
from asyncio import to_thread
from time import perf_counter
from typing import Annotated, Any

from fastapi import Depends, FastAPI, Header, HTTPException
from fastapi.responses import JSONResponse

from .. import __version__
from ..classifiers.rules import RuleBasedClassifier
from ..classifiers.slm import LlamaCppSlmClassifier
from ..config import Settings
from ..errors import AnalysisError, SlmError
from ..extractors.pdf import PdfExtractor
from ..extractors.rapidocr import RapidOcrExtractor
from ..extractors.router import TextExtractionRouter
from ..naming import DOCUMENT_TYPE_LABELS
from ..pipeline import AnalysisCoordinator
from .contracts import AnalyzeRequest, AnalyzeResponse, DetailedAnalyzeRequest, WarmupResponse


def build_ipc_response(result: dict[str, Any], extract_fields: list[str]) -> AnalyzeResponse:
    """Convert the detailed internal result to FileOrganizer.Shared.AnalyzeResponse."""

    decision = result["final_decision"]
    category = DOCUMENT_TYPE_LABELS[decision["document_type"]]
    available_metadata = {
        "date": decision["document_date"],
        "company": decision["organization"],
        "document_type": category,
        "category": category,
        "title": decision["suggested_base_name"],
    }
    metadata = {
        field: value
        for field in extract_fields
        if isinstance((value := available_metadata.get(field)), str) and value
    }
    ai_suggestion = result.get("ai_suggestion")
    confidence = (
        ai_suggestion.get("confidence")
        if decision["decision_source"] == "slm" and isinstance(ai_suggestion, dict)
        else None
    )
    return AnalyzeResponse(
        success=True,
        category=category,
        metadata=metadata,
        confidence=confidence,
    )


def _build_services(settings: Settings) -> tuple[AnalysisCoordinator, RapidOcrExtractor, LlamaCppSlmClassifier]:
    ocr = RapidOcrExtractor()
    router = TextExtractionRouter(
        PdfExtractor(settings.max_pdf_pages),
        ocr,
        min_pdf_text_chars=settings.min_pdf_text_chars,
        max_ocr_pdf_pages=settings.max_ocr_pdf_pages,
        pdf_render_scale=settings.pdf_render_scale,
    )
    slm = LlamaCppSlmClassifier(
        settings.slm_model_path,
        context_size=settings.slm_context_size,
        threads=settings.slm_threads,
        max_tokens=settings.slm_max_tokens,
        input_chars=settings.slm_input_chars,
        unload_after_inference=settings.unload_slm_after_inference,
    )
    coordinator = AnalysisCoordinator(settings, router, RuleBasedClassifier(), slm)
    return coordinator, ocr, slm


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or Settings.from_env()
    coordinator, ocr, slm = _build_services(settings)
    api = FastAPI(title="File Analyzer", version=__version__)

    def authorize(authorization: Annotated[str | None, Header()] = None) -> None:
        if settings.bearer_token is None:
            return
        expected = f"Bearer {settings.bearer_token}"
        if authorization is None or not hmac.compare_digest(authorization, expected):
            raise HTTPException(status_code=401, detail="Bearer token is invalid")

    auth = Depends(authorize)

    @api.get("/api/v1/health", dependencies=[auth])
    @api.get("/v1/health", dependencies=[auth], include_in_schema=False)
    async def health() -> dict[str, Any]:
        pdf_available = importlib.util.find_spec("pypdf") is not None
        ocr_available = ocr.available and importlib.util.find_spec("pypdfium2") is not None
        slm_available = slm.available
        return {
            "status": "ready" if pdf_available and ocr_available and slm_available else "degraded",
            "service_version": __version__,
            "ocr_available": ocr_available,
            "pdf_available": pdf_available,
            "slm_available": slm_available,
            "slm_model": slm.model_name,
        }

    @api.post("/api/v1/warmup", response_model=WarmupResponse, dependencies=[auth])
    @api.post("/v1/warmup", response_model=WarmupResponse, dependencies=[auth], include_in_schema=False)
    async def warmup() -> WarmupResponse:
        started = perf_counter()
        warnings: list[dict[str, str]] = []
        ocr_ready = False
        slm_ready = False
        try:
            await to_thread(ocr.warmup)
            ocr_ready = True
        except AnalysisError as exc:
            warnings.append({"code": exc.code, "message": exc.message})
        try:
            await to_thread(slm.warmup)
            slm_ready = True
        except SlmError as exc:
            warnings.append({"code": exc.code, "message": str(exc)})
        return WarmupResponse(
            ocr_ready=ocr_ready,
            slm_ready=slm_ready,
            elapsed_ms=round((perf_counter() - started) * 1_000),
            warnings=warnings,
        )

    @api.post("/api/v1/analyze", response_model=AnalyzeResponse, dependencies=[auth])
    async def analyze_ipc(request: AnalyzeRequest) -> AnalyzeResponse:
        try:
            result = await to_thread(
                coordinator.analyze,
                job_id="ipc",
                file_path=request.file_path,
                expected_size=None,
                expected_last_write_utc=None,
                analysis_mode="slm_with_rules_fallback",
                provided_ocr_text=request.ocr_text,
            )
            return build_ipc_response(result, request.extract_fields)
        except AnalysisError:
            return AnalyzeResponse(success=False)

    @api.post("/v1/analyze", dependencies=[auth], include_in_schema=False)
    async def analyze_detailed(request: DetailedAnalyzeRequest) -> JSONResponse:
        try:
            result = await to_thread(
                coordinator.analyze,
                job_id=request.job_id,
                file_path=request.file_path,
                expected_size=request.expected_size,
                expected_last_write_utc=request.expected_last_write_utc,
                analysis_mode=request.analysis_mode,
            )
            return JSONResponse(result)
        except AnalysisError as exc:
            return JSONResponse(
                status_code=exc.http_status,
                content={
                    "schema_version": "1.0",
                    "job_id": request.job_id,
                    "status": "failed",
                    "extraction": None,
                    "baseline": None,
                    "ai_suggestion": None,
                    "final_decision": None,
                    "fallback_used": False,
                    "warnings": [],
                    "error": {
                        "code": exc.code,
                        "message": exc.message,
                        "retryable": exc.retryable,
                        "details": None,
                    },
                    "elapsed_ms": 0,
                },
            )

    return api


app = create_app()
