"""Pure domain model for vision projects: no I/O, no Azure, no web."""
from __future__ import annotations

from dataclasses import dataclass, field
import re

NAME = re.compile(r"[a-z0-9][a-z0-9-]{1,62}")
FRAME = re.compile(r"[0-9]{6}")
BOX_STATES = ("auto", "reviewed")


class ValidationError(ValueError):
    pass


def check_name(value, what="name"):
    if not isinstance(value, str) or not NAME.fullmatch(value):
        raise ValidationError(f"{what} must be 2-63 lowercase letters, digits or hyphens.")
    return value


@dataclass(frozen=True)
class Box:
    cls: str
    x: float
    y: float
    w: float
    h: float
    state: str = "reviewed"
    score: float | None = None
    track: int | None = None

    def checked(self, classes, width, height):
        if self.cls not in classes:
            raise ValidationError(f"Unknown class {self.cls!r}.")
        if self.state not in BOX_STATES:
            raise ValidationError("Box state must be auto or reviewed.")
        if not (self.w > 0 and self.h > 0 and self.x >= 0 and self.y >= 0
                and self.x + self.w <= width + 0.5 and self.y + self.h <= height + 0.5):
            raise ValidationError("Box must have positive size and lie inside the frame.")
        return self

    def to_json(self):
        return {key: value for key, value in self.__dict__.items() if value is not None}


@dataclass
class FrameLabels:
    reviewed: bool = False
    boxes: list[Box] = field(default_factory=list)

    @classmethod
    def from_json(cls, value):
        return cls(bool(value.get("reviewed")), [Box(**box) for box in value.get("boxes", [])])

    def to_json(self):
        return {"reviewed": self.reviewed, "boxes": [box.to_json() for box in self.boxes]}


@dataclass(frozen=True)
class Project:
    name: str
    classes: tuple[str, ...]

    @classmethod
    def create(cls, name, classes):
        check_name(name)
        if not isinstance(classes, (list, tuple)) or not 1 <= len(classes) <= 64:
            raise ValidationError("Define between 1 and 64 classes.")
        for item in classes:
            check_name(item, "class")
        if len(set(classes)) != len(classes):
            raise ValidationError("Class names must be unique.")
        return cls(name, tuple(classes))

    def to_json(self):
        return {"name": self.name, "classes": list(self.classes)}
