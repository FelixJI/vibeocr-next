"""MinerU 4.1 parse transport on the official ``MinerUApiParser`` SDK.

上传、任务提交、轮询、下载与上游取消全部交给随包官方 SDK；本模块只保留
产品适配层：配置转换、输入 staging、同步/异步桥、逐文件汇总、错误分类
脱敏与 :class:`MineruDocument` 素材物化。旧 4.0 uploads/jobs 自写传输
循环已删除，不存在运行时回退路径。
"""

from __future__ import annotations

import asyncio
import json
import logging
import re
import tempfile
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path

from vibeocr.runtime.recognition.mineru_config import MineruConfigError
from vibeocr.runtime_contracts import ErrorCode, MineruConfig

logger = logging.getLogger(__name__)

# 官方 SDK 的取消收尾不可确认时仅打 warning（"Task cancellation could not
# be confirmed"）；在解析窗口内挂载 handler 把它转成产品侧可观察日志。
_SDK_LOGGER_NAME = "mineru.api_client"
_UNCONFIRMED_CANCEL_MARK = "could not be confirmed"
_CANCEL_POLL_SECONDS = 0.05


class MineruApiError(RuntimeError):
    """An upstream failure; never a reason to select another quality tier."""


class MineruCancelled(MineruApiError):
    """The caller or upstream canceled this job."""


def object_value(value: object, label: str) -> dict[str, object]:
    if not isinstance(value, dict) or not all(isinstance(k, str) for k in value):
        raise MineruApiError(f"Invalid MinerU {label}: expected object")
    return value


def string_value(value: object, label: str) -> str:
    if not isinstance(value, str) or not value:
        raise MineruApiError(f"Invalid MinerU {label}: expected nonempty string")
    return value


@dataclass(frozen=True)
class MineruDocument:
    """Materialized outputs survive cleanup of the upstream task's files."""

    markdown: str
    structured_content: dict[str, object]
    middle_json: dict[str, object]
    # 已物化的图片素材（相对路径 -> 字节）。SDK ``save(writer)`` 负责外置
    # 与校验；这里只保留产品投影需要的字节映射，不再持有上游 zip。
    images: dict[str, bytes] = field(default_factory=dict)


def _load_api_parser() -> type:
    """惰性导入官方 SDK；缺包环境 import 本模块不失败，调用才需要 SDK。"""

    from mineru.parser.api_client import MinerUApiParser  # type: ignore[no-any-return]

    return MinerUApiParser


class _UnconfirmedCancelForwarder(logging.Handler):
    """把 SDK 取消收尾 warning 转发到产品 logger，保留可观察性。"""

    def emit(self, record: logging.LogRecord) -> None:
        try:
            message = record.getMessage()
        except Exception:  # noqa: BLE001 - logging hook must never raise
            return
        if _UNCONFIRMED_CANCEL_MARK in message:
            logger.warning("MinerU SDK: %s", message)


# 旧服务（4.0 uploads/jobs）对新端点返回 404/405。SDK 的 ``http_error``
# message 形如 "HTTP {status}: {body}"；只允许从固定前缀提取状态码，正文
# 绝不进入产品错误或日志。
_HTTP_ERROR_STATUS = re.compile(r"^HTTP (\d{3})\b")

# SDK ``save`` 写出的协议文件；其余条目一律按图片素材处理。
_PROTOCOL_FILES = frozenset(
    {"markdown.md", "middle_json.json", "structured_content.json"}
)


def _transport_stage_slug(exc: BaseException) -> str:
    stage = getattr(exc, "stage", None)
    if not isinstance(stage, str) or not stage:
        return "unknown"
    slug = re.sub(r"[^a-z0-9]+", "_", stage.strip().lower()).strip("_")
    return slug or "unknown"


def _classify_sdk_exception(
    exc: BaseException,
) -> MineruApiError | MineruConfigError | None:
    """SDK 异常 -> 脱敏的产品错误；未知形状返回 ``None`` 由调用方兜底。

    分类只使用 SDK 固定的 ``code``/``stage`` 枚举值与状态码前缀；上游
    响应正文、URL、签名或凭据不进入错误消息（SDK 的 message 可能携带
    响应正文前缀，只透出 code）。
    """
    code = getattr(exc, "code", None)
    if isinstance(code, str) and code:
        if code == "invalid_api_key":
            return MineruConfigError(
                ErrorCode.VALIDATION_ERROR, "mineru_api_authentication_failed"
            )
        if code == "http_error":
            match = _HTTP_ERROR_STATUS.match(str(getattr(exc, "message", "")))
            if match is not None and match.group(1) in ("404", "405"):
                return MineruConfigError(
                    ErrorCode.VALIDATION_ERROR, "mineru_api_endpoint_incompatible"
                )
        return MineruApiError(f"mineru_sdk_{code}")
    if isinstance(getattr(exc, "stage", None), str):
        # _APITransportError：message 只含 stage/尝试次数/异常类型，安全；
        # 但 cause（httpx.TransportError）的 str 可能携带 URL，不回显。
        if getattr(exc, "timed_out", False):
            return MineruApiError("mineru_transport_timeout")
        return MineruApiError(f"mineru_transport_error_{_transport_stage_slug(exc)}")
    return None


class _MemoryDataWriter:
    """收集 SDK ``ParseResult.save(writer)`` 的协议文件与素材字节。"""

    def __init__(self) -> None:
        self.entries: dict[str, bytes] = {}

    def write(self, path: str, data: bytes) -> None:
        self.entries[path] = data

    def write_string(self, path: str, data: str) -> None:
        self.entries[path] = data.encode("utf-8")


def _materialize_document(result: object) -> MineruDocument:
    """经公开 ``save(writer)`` 边界物化素材，再组装产品 DTO。"""

    writer = _MemoryDataWriter()
    # save 内部在副本上外置素材并校验后才写出；PDF 的 to_dict 会省略
    # image_base64，因此不能只取 markdown()/to_dict()。
    result.save(writer)  # type: ignore[attr-defined]
    try:
        return MineruDocument(
            markdown=writer.entries["markdown.md"].decode("utf-8"),
            structured_content=json.loads(
                writer.entries["structured_content.json"].decode("utf-8")
            ),
            middle_json=json.loads(writer.entries["middle_json.json"].decode("utf-8")),
            images={
                path: payload
                for path, payload in writer.entries.items()
                if path not in _PROTOCOL_FILES
            },
        )
    except (KeyError, ValueError, UnicodeDecodeError) as exc:
        raise MineruApiError(
            "MinerU SDK produced an incomplete result package"
        ) from exc


def _staged_name(name: str) -> str:
    """显示名只作为文件名使用，绝不当作任意路径。"""

    filename = Path(name).name
    if not filename or filename in (".", ".."):
        raise MineruApiError("MinerU input name is not a usable filename")
    return filename


# staged 条目：原始名 -> (暂存路径, page_range)；page_range 为 None 表示
# 调用方为非 PDF 输入配置了页范围，按单文件配置错误隔离。
_StagedEntry = tuple[str, Path | None, str | None]


class MineruApiClient:
    """官方 ``MinerUApiParser`` 的同步 facade，外层合同与旧客户端一致。"""

    def __init__(
        self,
        url: str,
        *,
        timeout: float = 3600,
        api_key: str | None = None,
    ) -> None:
        self.url = url.rstrip("/")
        if not self.url:
            # SDK 对空 api_url 会回落 env 再回落官方 SaaS；纵深防御拒绝空值。
            raise ValueError("MinerU API URL must not be empty")
        self.timeout = timeout
        # 远程自部署服务器可选 Bearer 鉴权；空 key 不发送 Authorization。
        # 必须显式传 ""（而非 None）给 SDK：None 会回落到进程环境
        # MINERU_API_KEY，"" 归一化为无鉴权。
        self.api_key = api_key or ""

    def parse(
        self,
        files: list[tuple[str, bytes]],
        config: MineruConfig,
        *,
        cancelled: Callable[[], bool] = lambda: False,
    ) -> dict[str, MineruDocument | MineruApiError]:
        if not files:
            return {}
        if len({name for name, _ in files}) != len(files):
            raise ValueError("MinerU input names must be unique")
        if cancelled():
            raise MineruCancelled("MinerU job canceled by caller")
        deadline = time.monotonic() + self.timeout
        if deadline - time.monotonic() <= 0:
            # 预算耗尽时零副作用：不 staging、不创建任何任务。
            raise TimeoutError("MinerU job deadline exceeded")
        parser_cls = _load_api_parser()
        # 显式传入经校验的 URL/key：SDK 的 None 默认会回落环境变量与官方
        # SaaS（DEFAULT_API_URL），这里必须钉死传输目标。
        parser = parser_cls(
            api_url=self.url,
            api_key=self.api_key,
            tier=config.tier.value,
            ocr_mode=config.ocr_mode.value,
            include_images=True,
        )
        with tempfile.TemporaryDirectory(prefix="vibeocr-mineru-input-") as directory:
            staged = self._stage(directory, files, config)
            sdk_logger = logging.getLogger(_SDK_LOGGER_NAME)
            forwarder = _UnconfirmedCancelForwarder()
            sdk_logger.addHandler(forwarder)
            try:
                return asyncio.run(self._run(staged, parser, deadline, cancelled))
            finally:
                sdk_logger.removeHandler(forwarder)

    def _stage(
        self, directory: str, files: list[tuple[str, bytes]], config: MineruConfig
    ) -> list[_StagedEntry]:
        """把输入字节暂存为真实文件，保留媒体扩展名与唯一映射。"""

        staged: list[_StagedEntry] = []
        seen: set[str] = set()
        for name, data in files:
            filename = _staged_name(name)
            if filename in seen:
                raise ValueError("MinerU input names must be unique")
            seen.add(filename)
            is_pdf = filename.lower().endswith(".pdf")
            if not is_pdf and config.page_range != "all":
                # 非 PDF 输入的页范围是调用方配置错误，按文件失败隔离。
                staged.append((filename, None, None))
                continue
            path = Path(directory) / filename
            path.write_bytes(data)
            staged.append((filename, path, config.page_range if is_pdf else ""))
        return staged

    async def _run(
        self,
        staged: list[_StagedEntry],
        parser: object,
        deadline: float,
        cancelled: Callable[[], bool],
    ) -> dict[str, MineruDocument | MineruApiError]:
        # 把同步取消回调桥接到 asyncio：任务在既有执行线程的专属 loop
        # 内收敛，取消传播给 SDK 协程并等待其有界收尾。
        task = asyncio.ensure_future(self._parse_all(staged, parser, deadline))
        try:
            while True:
                done, _ = await asyncio.wait({task}, timeout=_CANCEL_POLL_SECONDS)
                if done:
                    break
                if cancelled():
                    task.cancel()
                    break
            return await task
        except asyncio.CancelledError:
            raise MineruCancelled("MinerU job canceled by caller") from None

    async def _parse_all(
        self,
        staged: list[_StagedEntry],
        parser: object,
        deadline: float,
    ) -> dict[str, MineruDocument | MineruApiError]:
        results: dict[str, MineruDocument | MineruApiError] = {}
        for name, path, page_range in staged:
            if path is None:
                results[name] = MineruApiError("Page ranges require a PDF input")
                continue
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                # 总预算耗尽：停止提交未开始文件；在途文件已被上一轮的
                # asyncio.timeout 取消并等待 SDK 有界收尾。
                raise TimeoutError("MinerU job deadline exceeded")
            try:
                async with asyncio.timeout(remaining):
                    result = await parser.parse_async(  # type: ignore[attr-defined]
                        path,
                        page_range=page_range or "",
                        cancel_on_cancel=True,
                    )
                # save() 同步物化；SDK 解包错误同样按单文件失败分类脱敏。
                document: MineruDocument | MineruApiError = _materialize_document(
                    result
                )
            except asyncio.CancelledError:
                raise
            except asyncio.TimeoutError:
                raise TimeoutError("MinerU job deadline exceeded") from None
            except Exception as exc:  # noqa: BLE001 - 单文件失败不得污染其他文件
                if isinstance(exc, MineruApiError):
                    # 产品侧已脱敏的错误（如结果包不完整）原样保留。
                    classified = exc
                else:
                    classified = _classify_sdk_exception(exc)
                if isinstance(classified, MineruConfigError):
                    # 端点/凭据配置错误属于整批失败，不逐文件重复分类。
                    raise classified from exc
                if classified is None:
                    # 只记异常类型：未分类异常的 traceback 可能携带 httpx URL
                    # （含带签名的上传地址），不进日志。
                    logger.debug(
                        "MinerU SDK raised an unclassified exception (%s)",
                        type(exc).__name__,
                    )
                    classified = MineruApiError("mineru_sdk_unexpected_error")
                document = classified
            results[name] = document
        return results
