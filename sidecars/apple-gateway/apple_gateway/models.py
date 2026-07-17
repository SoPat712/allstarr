from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class LoginRequest(StrictModel):
    username: str = Field(min_length=1, max_length=320)
    password: str = Field(min_length=1, max_length=1024)


class Login2faRequest(StrictModel):
    code: str = Field(pattern=r"^[0-9]{4,8}$")


class DownloadJobRequest(StrictModel):
    url: str = Field(min_length=1, max_length=2048)
    quality: str = Field(default="alac", pattern=r"^(alac|aac|aac-web|aac-he|aac-he-web)$")


class DownloadJobView(StrictModel):
    id: str
    state: Literal["queued", "running", "succeeded", "failed"]
    media_kind: str
    artifact_count: int = 0
    error_code: str | None = None
