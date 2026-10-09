from __future__ import annotations

from dataclasses import dataclass, field

from pydantic import BaseModel, ConfigDict, Field


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class LoginRequest(StrictModel):
    username: str = Field(min_length=1, max_length=320)
    password: str = Field(min_length=1, max_length=1024)


class Login2faRequest(StrictModel):
    code: str = Field(pattern=r"^[0-9]{4,8}$")


@dataclass(frozen=True, slots=True)
class MediaAccount:
    token: str = field(repr=False)
    storefront: str
    scope: str
