"""File OrganizerのSQLite操作履歴を読み取り専用で表示する補助ツール。"""

from __future__ import annotations

import argparse
import csv
import json
import os
import sqlite3
import sys
from pathlib import Path
from typing import Any, Sequence


STATES = ("Planned", "Executing", "Completed", "Failed", "Undoing", "Undone", "UndoFailed")
OPERATION_TYPES = ("Move", "Rename", "Copy", "Recycle")
SELECT_COLUMNS = (
    "id",
    "operation_id",
    "op_type",
    "source_path",
    "destination_path",
    "file_size_bytes",
    "file_last_modified_utc",
    "lightweight_hash",
    "state",
    "error_message",
    "created_at_utc",
    "updated_at_utc",
)


def default_database_path() -> Path | None:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        return None
    return Path(local_app_data) / "FileOrganizer" / "organizer.db"


def parse_arguments() -> argparse.Namespace:
    default_path = default_database_path()
    parser = argparse.ArgumentParser(
        description="File Organizerのoperation_historyを読み取り専用で表示します。"
    )
    parser.add_argument(
        "--db",
        type=Path,
        default=default_path,
        help="DBファイル。Windowsでは%%LOCALAPPDATA%%\\FileOrganizer\\organizer.dbを自動使用します。",
    )
    parser.add_argument("--limit", type=int, default=20, help="表示件数（既定: 20、0: 全件）")
    parser.add_argument("--state", choices=STATES, help="状態で絞り込み")
    parser.add_argument("--type", choices=OPERATION_TYPES, dest="operation_type", help="操作種別で絞り込み")
    parser.add_argument("--format", choices=("table", "json", "csv"), default="table", help="出力形式")
    return parser.parse_args()


def read_history(
    database_path: Path,
    *,
    limit: int,
    state: str | None,
    operation_type: str | None,
) -> list[dict[str, Any]]:
    if limit < 0:
        raise ValueError("--limitには0以上を指定してください。")
    if not database_path.is_file():
        raise FileNotFoundError(f"履歴DBが見つかりません: {database_path}")

    where: list[str] = []
    parameters: list[Any] = []
    if state:
        where.append("state = ?")
        parameters.append(state)
    if operation_type:
        where.append("op_type = ?")
        parameters.append(operation_type)

    query = f"SELECT {', '.join(SELECT_COLUMNS)} FROM operation_history"
    if where:
        query += " WHERE " + " AND ".join(where)
    query += " ORDER BY created_at_utc DESC, id DESC"
    if limit:
        query += " LIMIT ?"
        parameters.append(limit)

    # mode=roにより、このツールからDBの作成・更新・削除はできない。
    database_uri = database_path.resolve().as_uri() + "?mode=ro"
    with sqlite3.connect(database_uri, uri=True) as connection:
        connection.row_factory = sqlite3.Row
        return [dict(row) for row in connection.execute(query, parameters)]


def abbreviated(value: Any, width: int) -> str:
    text = "" if value is None else str(value)
    return text if len(text) <= width else text[: width - 1] + "…"


def print_table(rows: Sequence[dict[str, Any]]) -> None:
    columns = (
        ("id", "ID", 6),
        ("op_type", "操作", 8),
        ("state", "状態", 12),
        ("source_path", "操作前", 42),
        ("destination_path", "操作後", 42),
        ("updated_at_utc", "更新日時(UTC)", 27),
        ("error_message", "エラー", 30),
    )
    widths = [width for _, _, width in columns]
    print("  ".join(label.ljust(width) for (_, label, width) in columns))
    print("  ".join("-" * width for width in widths))
    for row in rows:
        print("  ".join(abbreviated(row[key], width).ljust(width) for key, _, width in columns))


def print_json(rows: Sequence[dict[str, Any]]) -> None:
    print(json.dumps(rows, ensure_ascii=False, indent=2))


def print_csv(rows: Sequence[dict[str, Any]]) -> None:
    writer = csv.DictWriter(sys.stdout, fieldnames=SELECT_COLUMNS, lineterminator="\n")
    writer.writeheader()
    writer.writerows(rows)


def main() -> int:
    args = parse_arguments()
    if args.db is None:
        print("LOCALAPPDATAを取得できません。--dbでorganizer.dbを指定してください。", file=sys.stderr)
        return 2

    try:
        rows = read_history(
            args.db,
            limit=args.limit,
            state=args.state,
            operation_type=args.operation_type,
        )
    except (FileNotFoundError, ValueError, sqlite3.Error) as exc:
        print(f"履歴を読み込めませんでした: {exc}", file=sys.stderr)
        return 1

    if args.format == "json":
        print_json(rows)
    elif args.format == "csv":
        print_csv(rows)
    else:
        print_table(rows)
    print(f"\n{len(rows)}件を表示しました。", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
