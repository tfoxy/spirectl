//! Read assembly-level string metadata without loading the DLL or requiring dotnet.

fn u16_at(bytes: &[u8], offset: usize) -> Option<usize> {
    Some(u16::from_le_bytes(bytes.get(offset..offset.checked_add(2)?)?.try_into().ok()?) as usize)
}

fn u32_at(bytes: &[u8], offset: usize) -> Option<usize> {
    Some(u32::from_le_bytes(bytes.get(offset..offset.checked_add(4)?)?.try_into().ok()?) as usize)
}

fn compressed(bytes: &mut &[u8]) -> Option<usize> {
    let first = *bytes.first()?;
    let (count, mut value) = match first {
        0..=0x7f => (1, first as usize),
        0x80..=0xbf => (2, (first & 0x3f) as usize),
        0xc0..=0xdf => (4, (first & 0x1f) as usize),
        _ => return None,
    };
    for byte in bytes.get(1..count)? {
        value = (value << 8) | *byte as usize;
    }
    *bytes = bytes.get(count..)?;
    Some(value)
}

fn blob_at(bytes: &[u8], index: usize) -> Option<&[u8]> {
    let mut rest = bytes.get(index..)?;
    let length = compressed(&mut rest)?;
    rest.get(..length)
}

fn string_at(bytes: &[u8], index: usize) -> Option<&str> {
    let rest = bytes.get(index..)?;
    std::str::from_utf8(rest.get(..rest.iter().position(|byte| *byte == 0)?)?).ok()
}

fn serialized_string<'a>(bytes: &mut &'a [u8]) -> Option<&'a str> {
    let length = compressed(bytes)?;
    let value = std::str::from_utf8(bytes.get(..length)?).ok()?;
    *bytes = bytes.get(length..)?;
    Some(value)
}

fn metadata_root(bytes: &[u8]) -> Option<&[u8]> {
    if bytes.get(..2)? != b"MZ" {
        return None;
    }
    let pe = u32_at(bytes, 0x3c)?;
    if bytes.get(pe..pe.checked_add(4)?)? != b"PE\0\0" {
        return None;
    }
    let sections = u16_at(bytes, pe + 6)?;
    let optional = pe + 24;
    let directories = match u16_at(bytes, optional)? {
        0x10b => optional + 96,
        0x20b => optional + 112,
        _ => return None,
    };
    let section_start = optional + u16_at(bytes, pe + 20)?;
    let rva_offset = |rva: usize| {
        for section in 0..sections {
            let base = section_start + section * 40;
            let start = u32_at(bytes, base + 12)?;
            let size = u32_at(bytes, base + 16)?;
            if rva >= start && rva - start < size {
                return u32_at(bytes, base + 20)?.checked_add(rva - start);
            }
        }
        None
    };
    let cli = rva_offset(u32_at(bytes, directories + 14 * 8)?)?;
    let root = rva_offset(u32_at(bytes, cli + 8)?)?;
    bytes.get(root..root.checked_add(u32_at(bytes, cli + 12)?)?)
}

/// Only assembly-owned `System.Reflection.AssemblyMetadataAttribute` values
/// count. Strings in IL, resources, or unrelated custom attributes cannot claim
/// a build identity. Invalid metadata and duplicate keys fail closed.
pub(super) fn read(bytes: &[u8], key: &str) -> Option<String> {
    let root = metadata_root(bytes)?;
    if root.get(..4)? != b"BSJB" {
        return None;
    }
    let flags = (16usize.checked_add(u32_at(root, 12)?)?.checked_add(3)?) & !3;
    let stream_count = u16_at(root, flags + 2)?;
    let mut header = flags + 4;
    let (mut tables, mut strings, mut blobs) = (None, None, None);
    for _ in 0..stream_count {
        let offset = u32_at(root, header)?;
        let length = u32_at(root, header + 4)?;
        let name = string_at(root, header + 8)?;
        let stream = root.get(offset..offset.checked_add(length)?)?;
        match name {
            "#~" | "#-" => tables = Some(stream),
            "#Strings" => strings = Some(stream),
            "#Blob" => blobs = Some(stream),
            _ => {}
        }
        header = (header + 8 + name.len() + 1 + 3) & !3;
    }
    let (tables, strings, blobs) = (tables?, strings?, blobs?);
    let heaps = *tables.get(6)?;
    let valid = u64::from_le_bytes(tables.get(8..16)?.try_into().ok()?);
    let mut rows = [0usize; 64];
    let mut cursor = 24;
    for (table, count) in rows.iter_mut().enumerate() {
        if valid & (1u64 << table) != 0 {
            *count = u32_at(tables, cursor)?;
            cursor += 4;
        }
    }
    let heap_size = |bit| if heaps & bit == 0 { 2 } else { 4 };
    let (s, g, b) = (heap_size(1), heap_size(2), heap_size(4));
    let index = |table: usize| if rows[table] < 65536 { 2 } else { 4 };
    let coded = |bits: usize, targets: &[usize]| {
        if targets
            .iter()
            .all(|table| rows[*table] < (1usize << (16 - bits)))
        {
            2
        } else {
            4
        }
    };
    let resolution = coded(2, &[0, 26, 35, 1]);
    let type_ref = coded(2, &[2, 1, 27]);
    let member_parent = coded(3, &[2, 1, 26, 6, 27]);
    let attribute_parent = coded(
        5,
        &[
            6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43,
        ],
    );
    let attribute_type = coded(3, &[6, 10]);
    // ECMA-335 tables up to CustomAttribute. Later tables need no decoding.
    let sizes = [
        2 + s + 3 * g,
        resolution + 2 * s,
        4 + 2 * s + type_ref + index(4) + index(6),
        index(4),
        2 + s + b,
        index(6),
        8 + s + b + index(8),
        index(8),
        4 + s,
        index(2) + type_ref,
        member_parent + s + b,
        2 + coded(2, &[4, 8, 23]) + b,
        attribute_parent + attribute_type + b,
    ];
    let mut offsets = [0usize; 13];
    for table in 0..13 {
        offsets[table] = cursor;
        cursor = cursor.checked_add(rows[table].checked_mul(sizes[table])?)?;
    }
    tables.get(..cursor)?;
    let number = |offset, size| {
        if size == 2 {
            u16_at(tables, offset)
        } else {
            u32_at(tables, offset)
        }
    };
    let row = |table: usize, rid: usize| {
        if rid == 0 || rid > rows[table] {
            None
        } else {
            Some(offsets[table] + (rid - 1) * sizes[table])
        }
    };
    let mut found = None;
    for rid in 1..=rows[12] {
        let attr = row(12, rid)?;
        // HasCustomAttribute: Assembly (tag 14), row 1.
        if number(attr, attribute_parent)? != (1 << 5) | 14 {
            continue;
        }
        let constructor = number(attr + attribute_parent, attribute_type)?;
        if constructor & 7 != 3 {
            continue;
        } // MemberRef
        let member = row(10, constructor >> 3)?;
        let owner = number(member, member_parent)?;
        if owner & 7 != 1 {
            continue;
        } // TypeRef
        let ty = row(1, owner >> 3)? + resolution;
        if string_at(strings, number(ty, s)?)? != "AssemblyMetadataAttribute"
            || string_at(strings, number(ty + s, s)?)? != "System.Reflection"
            || string_at(strings, number(member + member_parent, s)?)? != ".ctor"
        {
            continue;
        }
        let mut value = blob_at(blobs, number(attr + attribute_parent + attribute_type, b)?)?;
        if value.get(..2)? != [1, 0] {
            return None;
        }
        value = &value[2..];
        if serialized_string(&mut value)? != key {
            continue;
        }
        let text = serialized_string(&mut value)?;
        if value != [0, 0] || text.is_empty() || found.is_some() {
            return None;
        }
        found = Some(text.to_string());
    }
    found
}
