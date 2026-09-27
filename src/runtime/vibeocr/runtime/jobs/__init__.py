"""Job domain: registry, events, staging, retention.

These modules are the observable state machine of the supervisor. They hold
no OCR/model/PDF knowledge — an :class:`~vibeocr.runtime.jobs.module.Executor`
drives item transitions.
"""

from __future__ import annotations

from vibeocr.runtime.jobs.registry import JobNotFoundError, JobRecord, JobRegistry
from vibeocr.runtime.jobs.retention import RetentionPolicy
from vibeocr.runtime.jobs.staging import (
    InputExpiredError,
    InputStager,
    StagedInput,
    StagingQuotaError,
)

__all__ = [
    "InputExpiredError",
    "InputStager",
    "JobNotFoundError",
    "JobRecord",
    "JobRegistry",
    "RetentionPolicy",
    "StagedInput",
    "StagingQuotaError",
]
