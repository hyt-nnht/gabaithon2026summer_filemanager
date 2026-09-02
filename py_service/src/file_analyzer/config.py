from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True, slots=True)
class Settings:
    allowed_root: Path
    bearer_token: str | None = None
    max_file_bytes: int = 20 * 1024 * 1024
    max_pdf_pages: int = 10
    min_pdf_text_chars: int = 50
    max_ocr_pdf_pages: int = 3
    pdf_render_scale: float = 2.5
    text_preview_chars: int = 1_000
    slm_input_chars: int = 4_000
    slm_model_path: Path | None = None
    slm_context_size: int = 4_096
    slm_threads: int | None = None
    slm_max_tokens: int = 384
    unload_slm_after_inference: bool = True

    @classmethod
    def from_env(cls) -> "Settings":
        model_value = os.getenv("ANALYZER_SLM_MODEL")
        threads_value = os.getenv("ANALYZER_SLM_THREADS")
        bearer_token = os.getenv("ORGANIZER_IPC_TOKEN") or os.getenv("ANALYZER_BEARER_TOKEN") or None
        return cls(
            allowed_root=Path(os.getenv("ANALYZER_ALLOWED_ROOT", os.getcwd())).resolve(),
            bearer_token=bearer_token,
            max_file_bytes=int(os.getenv("ANALYZER_MAX_FILE_BYTES", 20 * 1024 * 1024)),
            max_pdf_pages=int(os.getenv("ANALYZER_MAX_PDF_PAGES", "10")),
            min_pdf_text_chars=int(os.getenv("ANALYZER_MIN_PDF_TEXT_CHARS", "50")),
            max_ocr_pdf_pages=int(os.getenv("ANALYZER_MAX_OCR_PDF_PAGES", "3")),
            pdf_render_scale=float(os.getenv("ANALYZER_PDF_RENDER_SCALE", "2.5")),
            slm_model_path=Path(model_value).resolve() if model_value else None,
            slm_context_size=int(os.getenv("ANALYZER_SLM_CONTEXT_SIZE", "4096")),
            slm_threads=int(threads_value) if threads_value else None,
            slm_max_tokens=int(os.getenv("ANALYZER_SLM_MAX_TOKENS", "384")),
            unload_slm_after_inference=os.getenv("ANALYZER_SLM_UNLOAD", "true").strip().lower()
            in {"1", "true", "yes", "on"},
        )
