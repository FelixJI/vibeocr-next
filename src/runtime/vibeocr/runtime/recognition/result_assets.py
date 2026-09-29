"""job 作用域结果资产（PNG）写入器与预算。

复用 InputStager 的 job 私有目录生命周期：资产写入该 job staging 目录下的
固定 ``results/`` 子目录（目录由 supervisor 侧 executor 从 stager 受信路径
派生后经内部 IPC 传入，绝不接受 Web 提供的路径）。文件名为
``<item_id>-<asset_id>.png``，asset_id 为随机 opaque id（uuid4 hex），
不可猜测、不跨 job 复用。

上限：单图编码后 8 MiB；单 item 32 图；单 job results 目录 256 MiB
（含既有文件，构造预算时扫描一次）。超限/编码失败/无目录时返回
``{"available": false, "reason": ...}``——逐图降级，识别任务不失败，
下游（预览/导出）必须显式呈现不完整，不得当作完整成功。
"""

from __future__ import annotations

import io
import re
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

MAX_ASSET_IMAGE_BYTES = 8 * 1024 * 1024
MAX_IMAGES_PER_ITEM = 32
MAX_JOB_ASSET_BYTES = 256 * 1024 * 1024
ASSET_MEDIA_TYPE = "image/png"

_SAFE_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]*$")
_SAFE_NAME_RE = re.compile(r"[^A-Za-z0-9._/-]+")

REASON_STORE_UNAVAILABLE = "asset_store_unavailable"
REASON_ITEM_LIMIT = "asset_item_limit"
REASON_JOB_BUDGET = "asset_job_budget"
REASON_TOO_LARGE = "asset_too_large"
REASON_ENCODE_FAILED = "asset_encode_failed"
REASON_SOURCE_UNAVAILABLE = "asset_source_unavailable"


def unavailable_asset(reason: str) -> dict[str, Any]:
    """构造不可用标记（wire 形状，禁止携带本地路径）。"""
    return {"available": False, "reason": reason}


def is_safe_asset_id(value: object) -> bool:
    return isinstance(value, str) and bool(_SAFE_ID_RE.fullmatch(value))


def sanitize_asset_name(name: object, *, fallback: str) -> str:
    """清洗 provider 的 markdown 图片相对名，作为导出 images 键。"""
    if not isinstance(name, str) or not name.strip():
        return fallback
    parts = name.strip().replace("\\", "/").split("/")
    if any(part in ("", ".", "..") for part in parts):
        return fallback
    cleaned = "/".join(_SAFE_NAME_RE.sub("_", part) for part in parts)
    return cleaned[:128] or fallback


@dataclass
class ResultAssetBudget:
    """一次 recognize_many 调用内多个 item sink 共享的 job 级字节预算。"""

    asset_dir: Path
    max_total_bytes: int = MAX_JOB_ASSET_BYTES
    _used_bytes: int = field(default=0, init=False)

    def __post_init__(self) -> None:
        # 既有文件计入预算（目录可能与前序 compute batch 共享）。
        existing = 0
        if self.asset_dir.is_dir():
            for entry in self.asset_dir.iterdir():
                if entry.is_file():
                    try:
                        existing += entry.stat().st_size
                    except OSError:
                        continue
        self._used_bytes = existing

    def try_reserve(self, size_bytes: int) -> bool:
        if self._used_bytes + size_bytes > self.max_total_bytes:
            return False
        self._used_bytes += size_bytes
        return True


class ResultAssetSink:
    """单 item 的结果资产写入器（worker 进程内使用）。

    只写 ``<asset_dir>/<item_id>-<asset_id>.png``；item_id 必须满足
    ``[A-Za-z0-9][A-Za-z0-9_-]*``（staging 生成的内部 id 天然满足），
    asset_id 由本类随机生成，二者共同保证无路径穿越。
    """

    def __init__(
        self,
        asset_dir: str | Path | None,
        item_id: str,
        budget: ResultAssetBudget | None = None,
        *,
        max_image_bytes: int = MAX_ASSET_IMAGE_BYTES,
        max_images_per_item: int = MAX_IMAGES_PER_ITEM,
    ) -> None:
        self._dir = Path(asset_dir) if asset_dir else None
        self._item_id = item_id
        self._budget = budget
        self._max_image_bytes = max_image_bytes
        self._max_images_per_item = max_images_per_item
        self._written = 0
        if self._dir is not None and not is_safe_asset_id(item_id):
            # 内部 item id 不合法时不落盘（防御：正常为 it-0000 形态）。
            self._dir = None

    @property
    def available(self) -> bool:
        return self._dir is not None

    def save(self, image: Any, *, name: object = None) -> dict[str, Any]:
        """把 PIL/ndarray 图像编码为 PNG 落盘，返回 wire ref（或不可用标记）。

        成功形状（executor 随后补 job_id）：
        ``{item_id, asset_id, media_type, width, height, size_bytes, name}``
        失败形状：``{available: false, reason}``。
        """
        if self._dir is None:
            return unavailable_asset(REASON_STORE_UNAVAILABLE)
        if self._written >= self._max_images_per_item:
            return unavailable_asset(REASON_ITEM_LIMIT)
        encoded, width, height = _encode_png(image)
        if encoded is None:
            return unavailable_asset(REASON_ENCODE_FAILED)
        if len(encoded) > self._max_image_bytes:
            return unavailable_asset(REASON_TOO_LARGE)
        if self._budget is not None and not self._budget.try_reserve(len(encoded)):
            return unavailable_asset(REASON_JOB_BUDGET)
        asset_id = uuid.uuid4().hex
        filename = f"{self._item_id}-{asset_id}.png"
        target = self._dir / filename
        try:
            self._dir.mkdir(parents=True, exist_ok=True)
            target.write_bytes(encoded)
        except OSError:
            if self._budget is not None:
                self._budget._used_bytes -= len(encoded)  # noqa: SLF001 — 同模块内部
            return unavailable_asset(REASON_ENCODE_FAILED)
        self._written += 1
        return {
            "item_id": self._item_id,
            "asset_id": asset_id,
            "media_type": ASSET_MEDIA_TYPE,
            "width": width,
            "height": height,
            "size_bytes": len(encoded),
            "name": f"{asset_id}.png",
        }


def _encode_png(image: Any) -> tuple[bytes | None, int, int]:
    """把 PIL Image / ndarray 编码为 PNG 字节，失败返回 (None, 0, 0)。"""
    try:
        from PIL import Image as PILImage

        if isinstance(image, PILImage.Image):
            pil_img = image
        else:
            pil_img = PILImage.fromarray(image)
        if pil_img.mode not in ("RGB", "RGBA", "L"):
            pil_img = pil_img.convert("RGB")
        width, height = pil_img.size
        buf = io.BytesIO()
        pil_img.save(buf, format="PNG")
        return buf.getvalue(), width, height
    except Exception:
        return None, 0, 0


__all__ = [
    "ASSET_MEDIA_TYPE",
    "MAX_ASSET_IMAGE_BYTES",
    "MAX_IMAGES_PER_ITEM",
    "MAX_JOB_ASSET_BYTES",
    "ResultAssetBudget",
    "ResultAssetSink",
    "is_safe_asset_id",
    "sanitize_asset_name",
    "unavailable_asset",
]
