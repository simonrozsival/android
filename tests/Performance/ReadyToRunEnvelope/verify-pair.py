#!/usr/bin/env python3
"""Verify actual ELF publication, PE component forwarding, and matched runtime inputs."""

import argparse
import hashlib
import json
from pathlib import Path
import struct
import zipfile

PAGE_SIZE = 16384


def require(condition, message):
    if not condition:
        raise ValueError(message)


def cstring(data, offset):
    end = data.find(b"\0", offset)
    require(0 <= offset < len(data) and end >= offset, "Invalid string offset/termination")
    return data[offset:end].decode("utf-8")


def ready_to_run(data, offset, rva_to_offset):
    signature, major, minor, flags, count = struct.unpack_from("<IHHII", data, offset)
    require(signature == 0x00525452 and (major, minor) == (30, 0), "Invalid/mismatched R2R header")
    sections = {}
    previous = 0
    for index in range(count):
        kind, rva, size = struct.unpack_from("<III", data, offset + 16 + index * 12)
        require(kind > previous, "Unsorted/duplicate R2R sections")
        sections[kind] = (rva, size)
        previous = kind
    owner = None
    if 116 in sections:
        rva, size = sections[116]
        start = rva_to_offset(rva)
        require(size > 0 and start + size <= len(data), "Invalid component owner range")
        owner = cstring(data[start:start + size], 0)
    return flags, sections, owner


def pe_layout(data):
    require(data[:2] == b"MZ", "Component is not a PE assembly")
    nt = struct.unpack_from("<I", data, 0x3C)[0]
    require(data[nt:nt + 4] == b"PE\0\0", "Invalid PE signature")
    count = struct.unpack_from("<H", data, nt + 6)[0]
    optional_size = struct.unpack_from("<H", data, nt + 20)[0]
    optional = nt + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    require(magic in (0x10B, 0x20B), "Invalid PE optional header")
    section_table = optional + optional_size

    def file_offset(rva):
        for index in range(count):
            _, virtual_size, address, raw_size, raw_offset = struct.unpack_from(
                "<8sIIII", data, section_table + index * 40)
            if address <= rva < address + max(virtual_size, raw_size):
                require(rva - address < raw_size, "PE RVA is not backed by file data")
                return raw_offset + rva - address
        raise ValueError(f"Unmapped PE RVA {rva:#x}")

    directories = optional + (112 if magic == 0x20B else 96)
    cli_rva = struct.unpack_from("<I", data, directories + 14 * 8)[0]
    return nt, file_offset(cli_rva), file_offset


def component(data):
    _, cli, file_offset = pe_layout(data)
    native_rva, native_size = struct.unpack_from("<II", data, cli + 64)
    require(native_size >= 16, "Component has no R2R header")
    return ready_to_run(data, file_offset(native_rva), file_offset)


def native_alignment(data):
    require(data[:7] == b"\x7fELF\x02\x01\x01", "Native library is not ELF64 little-endian")
    require(struct.unpack_from("<HH", data, 16) == (3, 183), "Native library is not AArch64 ET_DYN")
    phoff = struct.unpack_from("<Q", data, 32)[0]
    phsize, phcount = struct.unpack_from("<HH", data, 54)
    segments = [struct.unpack_from("<IIQQQQQQ", data, phoff + index * phsize)
                for index in range(phcount)]
    loads = [segment for segment in segments if segment[0] == 1]
    require(loads, "Native library has no load segments")
    for _, flags, offset, address, _, file_size, memory_size, alignment in loads:
        require(alignment >= PAGE_SIZE and alignment & (alignment - 1) == 0,
                "Native library has a load alignment smaller than 16 KiB")
        require(offset % alignment == address % alignment and
                offset % PAGE_SIZE == address % PAGE_SIZE,
                "Native library load offset/address are not 16-KiB congruent")
        require(flags & 3 != 3, "Native library has a writable/executable load segment")
        require(offset + file_size <= len(data) and file_size <= memory_size, "Invalid native load size")
    relro_ends = [segment[3] + segment[6] for segment in segments if segment[0] == 0x6474E552]
    require(all(end % PAGE_SIZE == 0 for end in relro_ends), "Native RELRO end is not 16-KiB aligned")
    for segment in segments:
        if segment[0] != 2:
            continue
        for offset in range(segment[2], segment[2] + segment[5], 16):
            tag, value = struct.unpack_from("<qQ", data, offset)
            if tag == 0:
                break
            require(tag != 22 and not (tag == 30 and value & 4),
                    "Native library requires text relocations")
    return {"load_alignments": [segment[7] for segment in loads], "relro_ends": relro_ends,
            "no_rwx": True, "no_text_relocations": True}


def elf(data, expected_owner):
    native_alignment(data)
    require(data[:7] == b"\x7fELF\x02\x01\x01", "Native owner is not ELF64 little-endian")
    _, kind, machine, _, _, phoff, shoff, _, _, phsize, phcount, shsize, shcount, _ = struct.unpack_from(
        "<16sHHIQQQIHHHHHH", data)
    require(kind == 3 and machine == 183, "Native owner is not AArch64 ET_DYN")
    segments = [struct.unpack_from("<IIQQQQQQ", data, phoff + index * phsize)
                for index in range(phcount)]
    loads = [segment for segment in segments if segment[0] == 1]
    require(any(segment[2] == 0 and segment[3] == 0 and segment[1] & 4 for segment in loads),
            "Native owner has no zero-based readable load segment")
    require(any(segment[1] & 1 and segment[6] > 0 for segment in loads), "No executable native code")
    require(all(segment[1] & 3 != 3 for segment in loads), "Unexpected writable/executable segment")

    def file_offset(rva):
        for _, _, offset, address, _, size, _, _ in loads:
            if address <= rva < address + size:
                return offset + rva - address
        raise ValueError(f"Unmapped ELF file RVA {rva:#x}")

    dynamic = next(segment for segment in segments if segment[0] == 2)
    tags = {}
    for offset in range(dynamic[2], dynamic[2] + dynamic[5], 16):
        tag, value = struct.unpack_from("<qQ", data, offset)
        if tag == 0:
            break
        tags[tag] = value
    soname = cstring(data, file_offset(tags[5]) + tags[14])
    require(soname == expected_owner, f"Unexpected SONAME {soname}")
    headers = [struct.unpack_from("<IIQQQQIIQQ", data, shoff + index * shsize)
               for index in range(shcount)]
    exported = {}
    for header in headers:
        if header[1] != 11:  # SHT_DYNSYM
            continue
        strings = headers[header[6]]
        for offset in range(header[4], header[4] + header[5], header[9]):
            name, info, visibility, section, value, size = struct.unpack_from("<IBBHQQ", data, offset)
            if section != 0 and info >> 4 in (1, 2) and visibility & 3 == 0:
                exported[cstring(data, strings[4] + name)] = (value, size)
    require("RTR_HEADER" in exported, "RTR_HEADER is not dynamically exported")
    header_rva = exported["RTR_HEADER"][0]
    _, sections, _ = ready_to_run(data, file_offset(header_rva), file_offset)
    require(102 in sections and sections[102][1] > 0, "No native runtime-function table")
    for rva, size in sections.values():
        require(any(address <= rva and rva + size <= address + extent
                    for _, flags, _, address, _, _, extent, _ in loads if flags & 4),
                f"R2R section outside readable ELF mapping: {rva:#x}+{size:#x}")
    return {"soname": soname, "header_rva": header_rva,
            "extent": max(segment[3] + segment[6] for segment in loads),
            "executable_segments": [{"rva": segment[3], "size": segment[6]}
                                    for segment in loads if segment[1] & 1]}


def strip_evidence(original, published):
    def layout(data):
        phoff, shoff = struct.unpack_from("<QQ", data, 32)
        phsize, phcount, shsize, shcount, names_index = struct.unpack_from("<HHHHH", data, 54)
        programs = data[phoff:phoff + phsize * phcount]
        loads = [struct.unpack_from("<IIQQQQQQ", data, phoff + i * phsize)
                 for i in range(phcount)]
        headers = [struct.unpack_from("<IIQQQQIIQQ", data, shoff + i * shsize)
                   for i in range(shcount)]
        names = headers[names_index]
        sections = {cstring(data, names[4] + header[0]): header for header in headers}
        allocated = {}
        for name, header in sections.items():
            if header[2] & 2:  # SHF_ALLOC
                payload = data[header[4]:header[4] + header[5]] if header[1] != 8 else b""
                allocated[name] = (header[1], header[2], header[3], header[5], header[8], payload)
        load_hashes = []
        for kind, flags, offset, address, _, size, memory_size, alignment in loads:
            if kind != 1:
                continue
            payload = bytearray(data[offset:offset + size])
            # Stripping moves/removes the section table. Only its ELF-header fields may change.
            for start, end in ((40, 48), (58, 64)):
                if offset <= start and end <= offset + size:
                    payload[start - offset:end - offset] = b"\0" * (end - start)
            load_hashes.append({"rva": address, "file_size": size, "memory_size": memory_size,
                                "flags": flags, "alignment": alignment,
                                "sha256": hashlib.sha256(payload).hexdigest()})
        return programs, sections, allocated, load_hashes

    before = layout(original)
    after = layout(published)
    require(before[0] == after[0], "Stripping changed ELF program headers")
    require(before[2] == after[2], "Stripping changed an allocated section/RVA/payload")
    require(before[3] == after[3], "Stripping changed PT_LOAD data beyond section-table header fields")
    require(".symtab" not in after[1] and ".strtab" not in after[1],
            "Published native owner retains nonallocated symbol/string tables")
    return {"unstripped_bytes": len(original), "published_bytes": len(published),
            "removed_bytes": len(original) - len(published), "program_headers_identical": True,
            "allocated_sections_identical": True, "pt_load_payloads": after[3],
            "normalized_elf_header_fields": ["e_shoff", "e_shentsize", "e_shnum", "e_shstrndx"]}


def hashes(directory):
    result = {}
    for path in sorted(directory.glob("*.dll")):
        data = bytearray(path.read_bytes())
        if path.name == "_Microsoft.Android.Resource.Designer.dll":
            # The Cecil resource designer emits a current COFF timestamp and random MVID.
            # Normalize only those two fields; all IL, resources, and other metadata must match.
            nt, cli, file_offset = pe_layout(data)
            data[nt + 8:nt + 12] = b"\0" * 4
            metadata = file_offset(struct.unpack_from("<I", data, cli + 8)[0])
            require(data[metadata:metadata + 4] == b"BSJB", "Invalid designer metadata signature")
            version_length = struct.unpack_from("<I", data, metadata + 12)[0]
            stream_header = metadata + ((16 + version_length + 3) & ~3)
            count = struct.unpack_from("<H", data, stream_header + 2)[0]
            stream_header += 4
            normalized = False
            for _ in range(count):
                offset, size = struct.unpack_from("<II", data, stream_header)
                name = cstring(data, stream_header + 8)
                stream_header += 8 + ((len(name) + 1 + 3) & ~3)
                if name == "#GUID":
                    require(size == 16, "Designer has unexpected GUIDs beyond its single MVID")
                    data[metadata + offset:metadata + offset + size] = b"\0" * size
                    normalized = True
            require(normalized, "Designer has no MVID GUID heap")
        result[path.name] = hashlib.sha256(data).hexdigest()
    return result


def extracts_native_libraries(manifest):
    require(struct.unpack_from("<H", manifest)[0] == 3, "Invalid binary Android manifest")
    offset = struct.unpack_from("<H", manifest, 2)[0]
    resources = []
    while offset < len(manifest):
        kind, header_size, size = struct.unpack_from("<HHI", manifest, offset)
        require(size >= header_size and size > 0 and offset + size <= len(manifest),
                "Invalid Android manifest chunk")
        if kind == 0x180:
            resources = list(struct.unpack_from(
                "<" + "I" * ((size - header_size) // 4), manifest, offset + header_size))
        elif kind == 0x102:
            start, stride, count = struct.unpack_from("<HHH", manifest, offset + header_size + 8)
            for index in range(count):
                attribute = offset + header_size + start + index * stride
                name = struct.unpack_from("<I", manifest, attribute + 4)[0]
                if name < len(resources) and resources[name] == 0x010104EA:
                    require(manifest[attribute + 15] == 0x12, "Native extraction flag is not Boolean")
                    return struct.unpack_from("<I", manifest, attribute + 16)[0] != 0
        offset += size
    raise ValueError("Manifest does not explicitly declare extractNativeLibs")


def apk_alignment(path):
    libraries = {}
    with zipfile.ZipFile(path) as apk, open(path, "rb") as file:
        extracted = extracts_native_libraries(apk.read("AndroidManifest.xml"))
        for entry in apk.infolist():
            if not entry.filename.startswith("lib/") or not entry.filename.endswith(".so"):
                continue
            require(entry.filename.startswith("lib/arm64-v8a/"), "Unexpected native ABI in arm64 APK")
            try:
                evidence = native_alignment(apk.read(entry))
            except ValueError as error:
                raise ValueError(f"{entry.filename}: {error}") from error
            file.seek(entry.header_offset)
            local = file.read(30)
            name_size, extra_size = struct.unpack_from("<HH", local, 26)
            data_offset = entry.header_offset + 30 + name_size + extra_size
            if entry.compress_type == zipfile.ZIP_STORED:
                require(data_offset % PAGE_SIZE == 0, f"{entry.filename}: unaligned stored native entry")
            else:
                require(extracted, f"{entry.filename}: compressed APK-backed library")
            evidence.update(zip_data_offset=data_offset, compressed=entry.compress_type != zipfile.ZIP_STORED)
            libraries[entry.filename] = evidence
    require(libraries, "APK has no native libraries to audit")
    return {"page_size": PAGE_SIZE, "native_loading": "extracted" if extracted else "apk",
            "libraries": libraries}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    parser.add_argument("artifacts", type=Path)
    args = parser.parse_args()
    name = "ReadyToRunEnvelope"
    owner = f"lib{name}.r2r.so"
    pe = args.app / "obj/pe/Release/net11.0-android/android-arm64"
    native = args.app / "obj/elf/Release/net11.0-android/android-arm64"
    require(hashes(pe / "linked") and hashes(pe / "linked") == hashes(native / "linked"),
            "PE/ELF trimmed compiler input assemblies differ")
    pe_flags, _, pe_owner = component((pe / "R2R" / f"{name}.dll").read_bytes())
    flags, _, actual_owner = component((native / "R2R" / f"{name}.dll").read_bytes())
    require(pe_flags & 0x20 and not pe_flags & 0x100, "PE control has wrong component flags")
    require(pe_owner == f"{name}.r2r.dll", f"Unexpected PE composite owner {pe_owner}")
    require(flags & 0x20 and flags & 0x100, "ELF component lacks COMPONENT/PLATFORM_NATIVE_IMAGE")
    require(actual_owner == owner, f"Component names intermediate/wrong owner: {actual_owner}")
    image = (native / "R2R" / owner).read_bytes()
    result = elf(image, owner)
    with zipfile.ZipFile(args.artifacts / f"{args.app.name}-pe.apk") as control, zipfile.ZipFile(
            args.artifacts / f"{args.app.name}-elf.apk") as treatment:
        extracted = extracts_native_libraries(treatment.read("AndroidManifest.xml"))
        require(extracted == extracts_native_libraries(control.read("AndroidManifest.xml")),
                "PE/ELF native extraction settings differ")
        entry_name = f"lib/arm64-v8a/{owner}"
        require(entry_name not in control.namelist(), "PE APK unexpectedly contains native R2R owner")
        entry = treatment.getinfo(entry_name)
        require(extracted or entry.compress_type == zipfile.ZIP_STORED,
                "APK-backed native owner is compressed")
        require(elf(treatment.read(entry), owner) == result,
                "APK native owner layout/header differs from linked ELF")
        result["stripping"] = strip_evidence(image, treatment.read(entry))
        with open(args.artifacts / f"{args.app.name}-elf.apk", "rb") as apk:
            apk.seek(entry.header_offset)
            local = apk.read(30)
            name_size, extra_size = struct.unpack_from("<HH", local, 26)
        require(extracted or (entry.header_offset + 30 + name_size + extra_size) % PAGE_SIZE == 0,
                "APK native owner is not page-aligned")
        runtime_hashes = {}
        for library in ("libcoreclr.so", "libclrjit.so", "libSystem.IO.Compression.Native.so",
                        "libSystem.Native.so"):
            path = "lib/arm64-v8a/" + library
            require(control.read(path) == treatment.read(path), f"Runtime mismatch: {library}")
            runtime_hashes[library] = hashlib.sha256(control.read(path)).hexdigest()
    result.update(component_owner=actual_owner, component_flags=hex(flags),
                  compiler_input_count=len(hashes(pe / "linked")), matched_runtime=True,
                  normalized_designer_fields=["COFF timestamp", "single MVID GUID"],
                  native_loading="extracted" if extracted else "apk",
                  runtime_library_sha256=runtime_hashes)
    result["apk_alignment"] = {
        variant: apk_alignment(args.artifacts / f"{args.app.name}-{variant}.apk")
        for variant in ("pe", "elf")}
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
