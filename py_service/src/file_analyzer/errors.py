from __future__ import annotations

from dataclasses import dataclass


@dataclass(slots=True)
class AnalysisError(Exception):
    code: str
    message: str
    http_status: int = 400
    retryable: bool = False

    def __str__(self) -> str:
        return self.message


class SlmError(Exception):
    code = "SLM_UNAVAILABLE"


class SlmUnavailable(SlmError):
    code = "SLM_UNAVAILABLE"


class InvalidModelOutput(SlmError):
    code = "INVALID_MODEL_OUTPUT"
