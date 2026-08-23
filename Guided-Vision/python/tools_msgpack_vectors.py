#!/usr/bin/env python3
"""
Generate MessagePack test vectors with the reference library, for the C# codec to
check itself against.

Written because GvMsgPack.cs is hand-rolled. A hand-rolled codec that is only ever
tested against itself will agree with itself perfectly and still be wrong on the wire,
so the only test worth having is one against a real implementation.
"""
import pathlib
import sys

import msgpack

CASES = [
    ("nil", None),
    ("true", True),
    ("false", False),
    ("zero", 0),
    ("fixint_max", 127),
    ("uint8", 128),
    ("uint8_max", 255),
    ("uint16", 256),
    ("uint16_max", 65535),
    ("uint32", 65536),
    ("uint32_max", 4294967295),
    ("uint64", 4294967296),
    ("neg_fixint", -1),
    ("neg_fixint_min", -32),
    ("int8", -33),
    ("int8_min", -128),
    ("int16", -129),
    ("int16_min", -32768),
    ("int32", -32769),
    ("int32_min", -2147483648),
    ("int64", -2147483649),
    ("float", 1.5),
    ("float_neg", -0.125),
    ("float_big", 1.7976931348623157e308),
    ("str_empty", ""),
    ("str_short", "hello"),
    ("str_31", "a" * 31),
    ("str_32", "a" * 32),
    ("str_255", "b" * 255),
    ("str_256", "b" * 256),
    ("str_utf8", "joint θ=1.5 °"),
    ("bin", b"\x00\x01\xfe\xff"),
    ("array_empty", []),
    ("array_15", list(range(15))),
    ("array_16", list(range(16))),
    ("map_empty", {}),
    ("map_small", {"a": 1, "b": "two"}),
    ("nested", {"topic": "arm/state", "seq": 42,
                "q": [0.1, -0.2, 3.5], "ok": True, "note": None}),
    ("deep", {"a": {"b": {"c": [1, {"d": "e"}]}}}),
]


def main() -> int:
    out = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "msgpack_vectors.txt")
    with out.open("w") as f:
        for name, value in CASES:
            blob = msgpack.packb(value, use_bin_type=True)
            f.write(f"{name}\t{blob.hex()}\n")
    print(f"wrote {len(CASES)} vectors to {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
