from __future__ import annotations

from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field


class DetailedAnalyzeRequest(BaseModel):
    """Python diagnostics endpoint request retained for local debugging."""

    model_config = ConfigDict(extra="forbid")

    schema_version: Literal["1.0"] = "1.0"
    job_id: str = Field(min_length=1, max_length=100)
    file_path: str = Field(min_length=1)
    expected_size: int | None = Field(default=None, ge=0)
    expected_last_write_utc: datetime | None = None
    analysis_mode: Literal["rules_only", "slm_with_rules_fallback"] = "slm_with_rules_fallback"
    language: Literal["ja", "en"] = "ja"


class AnalyzeRequest(BaseModel):
    """C# IPC request. ``file_path`` is metadata only and is never opened."""

    model_config = ConfigDict(extra="forbid")

    file_path: str = Field(
        min_length=1,
        max_length=1_024,
        description="Original path metadata. The IPC analyzer never opens this path.",
    )
    ocr_text: str = Field(
        min_length=1,
        max_length=100_000,
        description="Text extracted by Windows OCR in the C# process.",
    )
    extract_fields: list[str]


class AnalyzeResponse(BaseModel):
    """C# FileOrganizer.Shared.Models.AnalyzeResponse compatible response."""

    success: bool
    category: str | None = None
    metadata: dict[str, str] | None = None
    confidence: float | None = Field(default=None, ge=0, le=1)
    classification_source: Literal["slm", "rules"] | None = None


class WarmupResponse(BaseModel):
    ocr_ready: bool
    slm_ready: bool
    elapsed_ms: int
    warnings: list[dict[str, str]]
