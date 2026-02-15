"""Structured logging configuration with console and file handlers."""

import logging
import json
import os
from datetime import datetime, timezone


class JsonFormatter(logging.Formatter):
    """Formats log records as JSON lines for the file handler."""

    def format(self, record: logging.LogRecord) -> str:
        log_entry = {
            "timestamp": datetime.fromtimestamp(record.created, tz=timezone.utc).isoformat(),
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
        }
        if record.exc_info and record.exc_info[0] is not None:
            log_entry["exception"] = self.formatException(record.exc_info)
        return json.dumps(log_entry)


class ConsoleFormatter(logging.Formatter):
    """Colored, human-readable console formatter."""

    COLORS = {
        "DEBUG": "\033[90m",     # gray
        "INFO": "\033[0m",      # default
        "WARNING": "\033[33m",  # yellow
        "ERROR": "\033[31m",    # red
        "CRITICAL": "\033[91m", # bright red
    }
    RESET = "\033[0m"

    def format(self, record: logging.LogRecord) -> str:
        color = self.COLORS.get(record.levelname, self.RESET)
        timestamp = datetime.fromtimestamp(record.created).strftime("%H:%M:%S")
        return f"{color}[{timestamp}] {record.levelname:<8} {record.name}: {record.getMessage()}{self.RESET}"


def setup_logging(data_dir: str, verbose: bool = False) -> None:
    """Configure root logger with console + file handlers."""
    os.makedirs(data_dir, exist_ok=True)

    root = logging.getLogger()
    root.setLevel(logging.DEBUG)

    # Remove existing handlers
    root.handlers.clear()

    # Console handler
    console = logging.StreamHandler()
    console.setLevel(logging.DEBUG if verbose else logging.INFO)
    console.setFormatter(ConsoleFormatter())
    root.addHandler(console)

    # File handler (JSON lines)
    log_path = os.path.join(data_dir, "bot.log")
    file_handler = logging.FileHandler(log_path, encoding="utf-8")
    file_handler.setLevel(logging.DEBUG)
    file_handler.setFormatter(JsonFormatter())
    root.addHandler(file_handler)
