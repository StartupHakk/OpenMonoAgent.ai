"""OMOS pack format: load once, never re-read experts from disk.

Layout (little-endian):
  magic:     4 bytes  b'OMOS'
  version:   u16      1
  family:    u16 length + utf-8 bytes
  n_layers, n_experts, top_k, n_shared, hidden, expert_dim, first_k_dense: 7 x u32
  n_entries: u32
  index:     n_entries x (layer u32, expert u32, offset u64, size u32)
  blob:      concatenated expert payloads

The packer records how many times the blob has been read from the file. After
``load_into_arena`` that counter must stay at 1 for the rest of the process.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path

from .types import ExpertId, ModelConfig, PackedExpert

MAGIC = b"OMOS"
VERSION = 1
_HDR = struct.Struct("<4sH")
_U16 = struct.Struct("<H")
_U32 = struct.Struct("<I")
_U64 = struct.Struct("<Q")
_INDEX = struct.Struct("<IIQI")  # layer, expert, offset, size
_META7 = struct.Struct("<7I")


class PackError(ValueError):
    pass


@dataclass
class PackHeader:
    family: str
    n_layers: int
    n_experts: int
    top_k: int
    n_shared: int
    hidden_size: int
    expert_dim: int
    first_k_dense: int
    n_entries: int
    index: list[tuple[int, int, int, int]]  # layer, expert, offset, size
    blob_offset: int


class PackFile:
    def __init__(self, path: str | Path):
        self.path = Path(path)
        self._disk_reads = 0
        self.header = read_header(self.path)

    @property
    def disk_reads(self) -> int:
        return self._disk_reads

    def load_blob(self) -> bytes:
        """Single allowed disk read of expert payloads."""
        self._disk_reads += 1
        with self.path.open("rb") as fh:
            fh.seek(self.header.blob_offset)
            return fh.read()

    def load_into_arena(self, arena: "object") -> None:
        blob = self.load_blob()
        payloads: dict[tuple[int, int], bytes] = {}
        for layer, expert, offset, size in self.header.index:
            payloads[(layer, expert)] = blob[offset: offset + size]
        arena.load_all(payloads)  # type: ignore[attr-defined]


def read_header(path: Path) -> PackHeader:
    """Parse the index without reading expert payloads."""
    with path.open("rb") as fh:
        lead = fh.read(_HDR.size + _U16.size)
        if len(lead) < _HDR.size + _U16.size:
            raise PackError("pack too small")
        magic, version = _HDR.unpack_from(lead, 0)
        if magic != MAGIC:
            raise PackError(f"bad magic {magic!r}")
        if version != VERSION:
            raise PackError(f"unsupported version {version}")
        (fam_len,) = _U16.unpack_from(lead, _HDR.size)
        family = fh.read(fam_len)
        if len(family) != fam_len:
            raise PackError("truncated family")
        meta = fh.read(_META7.size + _U32.size)
        n_layers, n_experts, top_k, n_shared, hidden, expert_dim, first_k = _META7.unpack_from(meta, 0)
        (n_entries,) = _U32.unpack_from(meta, _META7.size)
        index_buf = fh.read(_INDEX.size * n_entries)
        if len(index_buf) != _INDEX.size * n_entries:
            raise PackError("truncated index")
        index: list[tuple[int, int, int, int]] = []
        for i in range(n_entries):
            index.append(_INDEX.unpack_from(index_buf, i * _INDEX.size))
        blob_offset = fh.tell()
    return PackHeader(
        family=family.decode("utf-8"),
        n_layers=n_layers,
        n_experts=n_experts,
        top_k=top_k,
        n_shared=n_shared,
        hidden_size=hidden,
        expert_dim=expert_dim,
        first_k_dense=first_k,
        n_entries=n_entries,
        index=index,
        blob_offset=blob_offset,
    )


def unpack_header(buf: bytes) -> PackHeader:
    if len(buf) < 8:
        raise PackError("pack too small")
    magic, version = _HDR.unpack_from(buf, 0)
    if magic != MAGIC:
        raise PackError(f"bad magic {magic!r}")
    if version != VERSION:
        raise PackError(f"unsupported version {version}")
    off = _HDR.size
    (fam_len,) = _U16.unpack_from(buf, off)
    off += _U16.size
    family = buf[off: off + fam_len].decode("utf-8")
    off += fam_len
    n_layers, n_experts, top_k, n_shared, hidden, expert_dim, first_k = _META7.unpack_from(buf, off)
    off += _META7.size
    (n_entries,) = _U32.unpack_from(buf, off)
    off += _U32.size
    index: list[tuple[int, int, int, int]] = []
    for _ in range(n_entries):
        layer, expert, offset, size = _INDEX.unpack_from(buf, off)
        index.append((layer, expert, offset, size))
        off += _INDEX.size
    return PackHeader(
        family=family,
        n_layers=n_layers,
        n_experts=n_experts,
        top_k=top_k,
        n_shared=n_shared,
        hidden_size=hidden,
        expert_dim=expert_dim,
        first_k_dense=first_k,
        n_entries=n_entries,
        index=index,
        blob_offset=off,
    )


def pack_experts(path: str | Path, config: ModelConfig, experts: list[PackedExpert]) -> Path:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    family = config.family.encode("utf-8")
    blob = bytearray()
    index_recs: list[tuple[int, int, int, int]] = []
    for item in experts:
        offset = len(blob)
        blob.extend(item.payload)
        index_recs.append((item.expert_id.layer, item.expert_id.expert, offset, len(item.payload)))

    parts = [
        _HDR.pack(MAGIC, VERSION),
        _U16.pack(len(family)),
        family,
        _META7.pack(
            config.n_layers,
            config.n_experts,
            config.top_k,
            config.n_shared,
            config.hidden_size,
            config.expert_dim,
            config.first_k_dense,
        ),
        _U32.pack(len(index_recs)),
    ]
    for layer, expert, offset, size in index_recs:
        parts.append(_INDEX.pack(layer, expert, offset, size))
    parts.append(bytes(blob))
    path.write_bytes(b"".join(parts))
    return path


def synthetic_payload(expert_id: ExpertId, nbytes: int = 64) -> bytes:
    """Deterministic fake expert bytes (not real weights)."""
    seed = (expert_id.layer * 1009 + expert_id.expert * 9176) & 0xFF
    return bytes((seed + i) & 0xFF for i in range(nbytes))


def pack_synthetic(path: str | Path, config: ModelConfig, nbytes: int = 64) -> Path:
    experts: list[PackedExpert] = []
    for layer in range(config.first_k_dense, config.n_layers):
        for eid in range(config.n_experts):
            experts.append(
                PackedExpert(
                    expert_id=ExpertId(layer=layer, expert=eid),
                    payload=synthetic_payload(ExpertId(layer, eid), nbytes),
                )
            )
    return pack_experts(path, config, experts)
