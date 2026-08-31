from __future__ import annotations

import re
from datetime import date
from pathlib import Path

from ..models import ClassificationCandidate, DocumentType


class RuleBasedClassifier:
    KEYWORDS: dict[DocumentType, tuple[str, ...]] = {
        "receipt": ("領収書", "領収証", "合計", "税込", "お預り", "現金"),
        "invoice": ("請求書", "請求金額", "支払期限", "お支払期限", "振込先"),
        "meeting_minutes": ("議事録", "出席者", "議題", "決定事項"),
        "contract": ("契約書", "甲", "乙", "契約期間", "署名"),
        "other": (),
    }
    TYPE_PRIORITY: tuple[DocumentType, ...] = (
        "receipt",
        "invoice",
        "meeting_minutes",
        "contract",
    )
    DATE_PATTERNS = (
        re.compile(r"(?<!\d)(20\d{2})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日"),
        re.compile(r"(?<!\d)(20\d{2})[-/.](\d{1,2})[-/.](\d{1,2})(?!\d)"),
    )
    ORGANIZATION_PATTERNS = (
        re.compile(r"(?:発行元|発行者|請求元|販売元|会社名)\s*[:：]\s*([^\n]{1,100})"),
        re.compile(r"([^\n]{1,80}?(?:株式会社|有限会社|合同会社|Inc\.|LLC))(?=\s|$|[、,。])", re.IGNORECASE),
        re.compile(r"((?:株式会社|有限会社|合同会社)[^\n]{1,80})"),
    )

    def classify(self, text: str, original_file_name: str = "") -> ClassificationCandidate:
        searchable = f"{text}\n{Path(original_file_name).stem}"
        scores: dict[DocumentType, int] = {
            document_type: sum(searchable.count(keyword) for keyword in keywords)
            for document_type, keywords in self.KEYWORDS.items()
        }
        best_type: DocumentType = "other"
        best_score = 0
        for document_type in self.TYPE_PRIORITY:
            if scores[document_type] > best_score:
                best_type = document_type
                best_score = scores[document_type]

        matched = [keyword for keyword in self.KEYWORDS[best_type] if keyword in searchable]
        reason = "一致キーワード: " + "、".join(matched[:5]) if matched else "文書種別キーワードなし"
        return ClassificationCandidate(
            document_type=best_type,
            organization=self._extract_organization(text),
            document_date=self._extract_date(text),
            confidence=None,
            reason=reason,
            source="rules",
        )

    @classmethod
    def _extract_date(cls, text: str) -> str | None:
        for pattern in cls.DATE_PATTERNS:
            for match in pattern.finditer(text):
                try:
                    return date(*(int(value) for value in match.groups())).isoformat()
                except ValueError:
                    continue
        return None

    @classmethod
    def _extract_organization(cls, text: str) -> str | None:
        for pattern in cls.ORGANIZATION_PATTERNS:
            match = pattern.search(text)
            if not match:
                continue
            value = re.sub(r"\s+", " ", match.group(1)).strip(" 、,。:：")
            if value:
                return value[:100]
        return None

