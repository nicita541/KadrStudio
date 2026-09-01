from __future__ import annotations


def _read_varint(data: bytes, offset: int) -> tuple[int, int]:
    value = 0
    shift = 0
    while offset < len(data) and shift < 64:
        current = data[offset]
        offset += 1
        value |= (current & 0x7F) << shift
        if current & 0x80 == 0:
            return value, offset
        shift += 7
    raise ValueError("invalid protobuf varint")


def parse_fields(data: bytes) -> dict[int, list[bytes | int]]:
    fields: dict[int, list[bytes | int]] = {}
    offset = 0
    while offset < len(data):
        tag, offset = _read_varint(data, offset)
        field, wire = tag >> 3, tag & 7
        if wire == 0:
            value, offset = _read_varint(data, offset)
        elif wire == 2:
            length, offset = _read_varint(data, offset)
            end = offset + length
            if end > len(data):
                raise ValueError("truncated protobuf field")
            value = data[offset:end]
            offset = end
        else:
            raise ValueError(f"unsupported protobuf wire type {wire}")
        fields.setdefault(field, []).append(value)
    return fields


def text(fields: dict[int, list[bytes | int]], field: int, default: str = "") -> str:
    values = fields.get(field)
    if not values:
        return default
    value = values[-1]
    return value.decode("utf-8") if isinstance(value, bytes) else str(value)


def texts(fields: dict[int, list[bytes | int]], field: int) -> list[str]:
    return [value.decode("utf-8") for value in fields.get(field, []) if isinstance(value, bytes)]


def binary(fields: dict[int, list[bytes | int]], field: int) -> bytes:
    values = fields.get(field)
    if not values:
        return b""
    value = values[-1]
    return value if isinstance(value, bytes) else b""


def _varint(value: int) -> bytes:
    output = bytearray()
    while value >= 0x80:
        output.append((value & 0x7F) | 0x80)
        value >>= 7
    output.append(value)
    return bytes(output)


def field_bytes(field: int, value: bytes) -> bytes:
    return _varint((field << 3) | 2) + _varint(len(value)) + value


def field_text(field: int, value: str) -> bytes:
    return field_bytes(field, value.encode("utf-8"))


def field_int(field: int, value: int) -> bytes:
    return _varint(field << 3) + _varint(value)
