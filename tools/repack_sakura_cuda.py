"""Losslessly repack GGUF TQ1_0 tensors as Prism PTQ1_0 for CUDA.

Requires numpy and the Prism ggml-base.dll. No floating-point requantization:
ternary codes are rearranged and each original fp16 scale is copied twice.
Every converted value is checked with Prism's independent native decoders.
Metadata and non-TQ1 tensors are preserved byte-for-byte. Never overwrites.
"""

import argparse
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import struct
import time

import numpy as np


TQ1 = 34
PTQ1 = 143
CHUNK_BLOCKS = 32768
SCALAR_SIZES = {0: 1, 1: 1, 2: 2, 3: 2, 4: 4, 5: 4, 6: 4, 7: 1, 10: 8, 11: 8, 12: 8}


def read_num(stream, fmt):
    return struct.unpack('<' + fmt, stream.read(struct.calcsize('<' + fmt)))[0]


def read_string(stream):
    return stream.read(read_num(stream, 'Q')).decode('utf-8')


def skip_value(stream, kind):
    if kind == 8:
        stream.seek(read_num(stream, 'Q'), 1)
    elif kind == 9:
        subtype, count = read_num(stream, 'I'), read_num(stream, 'Q')
        if subtype in SCALAR_SIZES:
            stream.seek(SCALAR_SIZES[subtype] * count, 1)
        else:
            for _ in range(count):
                skip_value(stream, subtype)
    else:
        stream.seek(SCALAR_SIZES[kind], 1)


def align(n, alignment):
    return (n + alignment - 1) // alignment * alignment


def read_header(stream, lib):
    if stream.read(4) != b'GGUF' or read_num(stream, 'I') != 3:
        raise ValueError('Only little-endian GGUF V3 is supported')
    n_tensors, n_kv = read_num(stream, 'Q'), read_num(stream, 'Q')
    alignment = 32
    for _ in range(n_kv):
        name, kind = read_string(stream), read_num(stream, 'I')
        if name == 'general.alignment':
            if kind != 4:
                raise ValueError('Unexpected alignment metadata type')
            alignment = read_num(stream, 'I')
        else:
            skip_value(stream, kind)
    if alignment < 1 or alignment > 65536 or alignment & (alignment - 1):
        raise ValueError('Invalid alignment')
    tensors = []
    for _ in range(n_tensors):
        name, ndims = read_string(stream), read_num(stream, 'I')
        if not 1 <= ndims <= 4:
            raise ValueError(f'Invalid dimensions: {name}')
        dims = [read_num(stream, 'Q') for _ in range(ndims)]
        type_pos = stream.tell()
        kind, offset = read_num(stream, 'I'), read_num(stream, 'Q')
        if kind not in (0, 1, 30, TQ1, 41, PTQ1):
            raise ValueError(f'Unsupported tensor type {kind}: {name}')
        block = lib.ggml_blck_size(kind)
        count = math.prod(dims)
        if dims[0] % block:
            raise ValueError(f'Misaligned row: {name}')
        size = count // block * lib.ggml_type_size(kind)
        tensors.append(dict(name=name, dims=dims, kind=kind, offset=offset,
                            type_pos=type_pos, size=size, count=count))
    data_start = align(stream.tell(), alignment)
    stream.seek(0)
    header = bytearray(stream.read(data_start))
    return header, tensors, data_start, alignment


def load_prism(path):
    lib = ctypes.CDLL(str(path.resolve()))
    for name in ('ggml_blck_size', 'ggml_type_size'):
        fn = getattr(lib, name)
        fn.argtypes = [ctypes.c_int]
        fn.restype = ctypes.c_size_t
    for name in ('dequantize_row_tq1_0', 'dequantize_row_ptq1_0'):
        fn = getattr(lib, name)
        fn.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int64]
        fn.restype = None
    if (lib.ggml_blck_size(TQ1), lib.ggml_type_size(TQ1),
            lib.ggml_blck_size(PTQ1), lib.ggml_type_size(PTQ1)) != (256, 54, 128, 28):
        raise ValueError('Incompatible Prism codec definitions')
    return lib


# In the CPU reference, uint8 multiplication wraps before the next trit.
DECODE = ((((np.arange(256, dtype=np.uint16)[:, None]
             * np.array([1, 3, 9, 27, 81], dtype=np.uint16)) & 255) * 3) >> 8).astype(np.uint8)
BASE3 = np.array([81, 27, 9, 3, 1], dtype=np.uint16)


def repack(raw):
    blocks = np.frombuffer(raw, dtype=np.uint8).reshape(-1, 54)
    codes = np.concatenate((
        DECODE[blocks[:, :32]].transpose(0, 2, 1).reshape(-1, 160),
        DECODE[blocks[:, 32:48]].transpose(0, 2, 1).reshape(-1, 80),
        DECODE[blocks[:, 48:52]][:, :, :4].transpose(0, 2, 1).reshape(-1, 16),
    ), axis=1).reshape(-1, 128)
    out = np.empty((len(codes), 28), dtype=np.uint8)
    for first, width, dest in ((0, 16, 0), (80, 8, 16), (120, 2, 24)):
        digits = 4 if first == 120 else 5
        values = codes[:, first:first + width * digits].reshape(-1, digits, width)
        packed = (values.astype(np.uint16) * BASE3[:digits][None, :, None]).sum(axis=1)
        out[:, dest:dest + width] = ((packed * 256 + 242) // 243).astype(np.uint8)
    out[:, 26:28] = np.repeat(blocks[:, 52:54], 2, axis=0)
    return out.tobytes()


def verify_native(lib, original, converted):
    count = len(original) // 54 * 256
    a, b = np.empty(count, dtype=np.float32), np.empty(count, dtype=np.float32)
    lib.dequantize_row_tq1_0(original, a.ctypes.data, count)
    lib.dequantize_row_ptq1_0(converted, b.ctypes.data, count)
    # Bitwise includes signed zero; all scales must also remain finite.
    if not np.isfinite(a).all() or not np.array_equal(a.view(np.uint32), b.view(np.uint32)):
        raise ValueError('Native decoder comparison failed: output is not lossless')


def self_test(lib):
    rng = np.random.default_rng(87215)
    blocks = rng.integers(0, 256, size=(8192, 54), dtype=np.uint8)
    # Include every possible packed byte in every position.
    blocks[:256, :52] = np.arange(256, dtype=np.uint8)[:, None]
    scales = rng.uniform(-2, 2, len(blocks)).astype('<f2')
    scales[:4] = [0.0, -0.0, np.finfo(np.float16).smallest_subnormal, 65504.0]
    blocks[:, 52:54] = scales.view(np.uint8).reshape(-1, 2)
    original = blocks.tobytes()
    converted = repack(original)
    verify_native(lib, original, converted)
    print('Codec self-test: 2,097,152 values exactly match native Prism decoders', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prism-dll', required=True, type=Path)
    parser.add_argument('--input', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--expected-sha256')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    lib = load_prism(args.prism_dll)
    self_test(lib)
    if args.self_test:
        return
    if not args.input or not args.output:
        parser.error('--input and --output are required')
    if args.output.exists() or args.input.resolve() == args.output.resolve():
        raise ValueError('Output must be a new file; original model is never overwritten')
    started = time.monotonic()
    original_hash = hashlib.sha256()
    with args.input.open('rb') as source:
        for chunk in iter(lambda: source.read(8 * 1024 * 1024), b''):
            original_hash.update(chunk)
        original_sha = original_hash.hexdigest()
        if args.expected_sha256 and original_sha != args.expected_sha256.lower():
            raise ValueError(f'Unexpected source SHA-256: {original_sha}')
        print(f'Source SHA-256: {original_sha}', flush=True)
        source.seek(0)
        header, tensors, start, alignment = read_header(source, lib)
        source_size = args.input.stat().st_size
        ordered = sorted(tensors, key=lambda t: t['offset'])
        source_end = 0
        output_size = 0
        for tensor in ordered:
            if tensor['offset'] < source_end or start + tensor['offset'] + tensor['size'] > source_size:
                raise ValueError('Overlapping or truncated input tensors')
            source_end = tensor['offset'] + tensor['size']
            tensor['new_offset'] = align(output_size, alignment)
            tensor['new_size'] = (tensor['count'] // 128 * 28 if tensor['kind'] == TQ1 else tensor['size'])
            output_size = tensor['new_offset'] + tensor['new_size']
            struct.pack_into('<IQ', header, tensor['type_pos'],
                             PTQ1 if tensor['kind'] == TQ1 else tensor['kind'], tensor['new_offset'])
        if not any(t['kind'] == TQ1 for t in tensors):
            raise ValueError('Input has no TQ1_0 tensors')
        args.output.parent.mkdir(parents=True, exist_ok=True)
        records, checked = [], 0
        with args.output.open('x+b') as dest:
            dest.write(header)
            for index, tensor in enumerate(ordered):
                source.seek(start + tensor['offset'])
                dest.seek(start + tensor['new_offset'])
                digest = hashlib.sha256()
                remaining = tensor['size']
                while remaining:
                    amount = min(remaining, CHUNK_BLOCKS * 54 if tensor['kind'] == TQ1 else 8 * 1024 * 1024)
                    raw = source.read(amount)
                    if len(raw) != amount:
                        raise ValueError('Unexpected end of source')
                    data = repack(raw) if tensor['kind'] == TQ1 else raw
                    if tensor['kind'] == TQ1:
                        verify_native(lib, raw, data)
                        checked += len(raw) // 54 * 256
                    dest.write(data)
                    digest.update(data)
                    remaining -= amount
                records.append(dict(name=tensor['name'], type=tensor['kind'], sha256=digest.hexdigest()))
                if tensor['kind'] == TQ1 and (index % 25 == 0 or index == len(ordered) - 1):
                    print(f'{index + 1}/{len(ordered)} tensors; {checked:,} weights verified; {time.monotonic() - started:.1f}s', flush=True)
            dest.flush()
            os.fsync(dest.fileno())
            for tensor, record in zip(ordered, records):
                dest.seek(start + tensor['new_offset'])
                digest, remaining = hashlib.sha256(), tensor['new_size']
                while remaining:
                    data = dest.read(min(remaining, 8 * 1024 * 1024))
                    if not data:
                        raise ValueError('Truncated output')
                    digest.update(data)
                    remaining -= len(data)
                if digest.hexdigest() != record['sha256']:
                    raise ValueError(f'Output readback mismatch: {tensor["name"]}')
    output_hash = hashlib.sha256()
    with args.output.open('rb') as output:
        for chunk in iter(lambda: output.read(8 * 1024 * 1024), b''):
            output_hash.update(chunk)
    report = dict(source=str(args.input.resolve()), output=str(args.output.resolve()),
                  source_sha256=original_sha, output_sha256=output_hash.hexdigest(),
                  source_bytes=source_size, output_bytes=args.output.stat().st_size,
                  converted_tensors=sum(t['kind'] == TQ1 for t in tensors),
                  preserved_tensors=sum(t['kind'] != TQ1 for t in tensors),
                  native_decoder_bit_exact_weights=checked, output_readback_verified=True,
                  elapsed_seconds=round(time.monotonic() - started, 2), tensors=records)
    report_path = args.output.with_suffix('.verification.json')
    with report_path.open('x', encoding='utf-8') as output:
        json.dump(report, output, indent=2)
    print(json.dumps({k: v for k, v in report.items() if k != 'tensors'}, indent=2), flush=True)


if __name__ == '__main__':
    main()
