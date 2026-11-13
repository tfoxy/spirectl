// Minimal PE/CLI metadata fixture; contains no executable code or game content.
fn bridge_metadata_dll(metadata: &[(&str, &str)]) -> Vec<u8> {
    fn put32(bytes: &mut [u8], at: usize, value: usize) {
        bytes[at..at + 4].copy_from_slice(&(value as u32).to_le_bytes());
    }
    fn put16(bytes: &mut [u8], at: usize, value: usize) {
        bytes[at..at + 2].copy_from_slice(&(value as u16).to_le_bytes());
    }
    fn append_string(bytes: &mut Vec<u8>, text: &str) {
        assert!(text.len() < 128);
        bytes.push(text.len() as u8);
        bytes.extend_from_slice(text.as_bytes());
    }
    let strings = b"\0AssemblyMetadataAttribute\0System.Reflection\0.ctor\0";
    let mut blobs = vec![0];
    let mut attributes = Vec::new();
    for (key, value) in metadata {
        let index = blobs.len();
        let mut blob = vec![1, 0];
        append_string(&mut blob, key);
        append_string(&mut blob, value);
        blob.extend_from_slice(&[0, 0]);
        assert!(blob.len() < 128);
        blobs.push(blob.len() as u8);
        blobs.extend(blob);
        // Assembly row 1, MemberRef constructor row 1, blob index.
        for field in [46u16, 11, index as u16] {
            attributes.extend_from_slice(&field.to_le_bytes());
        }
    }
    let mut tables = vec![0; 24];
    tables[4] = 2;
    tables[8..16].copy_from_slice(&((1u64 << 1) | (1 << 10) | (1 << 12) | (1 << 32)).to_le_bytes());
    for count in [1, 1, metadata.len(), 1] {
        tables.extend_from_slice(&(count as u32).to_le_bytes());
    }
    // TypeRef (name + namespace), MemberRef (TypeRef owner + .ctor + signature).
    for field in [0u16, 1, 27, 9, 45, 0] {
        tables.extend_from_slice(&field.to_le_bytes());
    }
    tables.extend(attributes);
    tables.extend_from_slice(&[0; 22]); // Assembly row (unused by the reader).
    let mut root = vec![0; 24];
    root[..4].copy_from_slice(b"BSJB");
    put32(&mut root, 12, 4);
    root[16..20].copy_from_slice(b"v4\0\0");
    put16(&mut root, 22, 3);
    let mut stream_offset = 72;
    for (name, stream) in [
        ("#~", tables.as_slice()),
        ("#Strings", strings.as_slice()),
        ("#Blob", blobs.as_slice()),
    ] {
        root.extend_from_slice(&(stream_offset as u32).to_le_bytes());
        root.extend_from_slice(&(stream.len() as u32).to_le_bytes());
        root.extend_from_slice(name.as_bytes());
        root.push(0);
        while root.len() % 4 != 0 {
            root.push(0);
        }
        stream_offset += stream.len();
    }
    assert_eq!(root.len(), 72);
    root.extend(tables);
    root.extend(strings);
    root.extend(blobs);
    let mut pe = vec![0; 0x280 + root.len()];
    pe[..2].copy_from_slice(b"MZ");
    put32(&mut pe, 0x3c, 0x80);
    pe[0x80..0x84].copy_from_slice(b"PE\0\0");
    put16(&mut pe, 0x86, 1);
    put16(&mut pe, 0x94, 0xe0);
    put16(&mut pe, 0x98, 0x10b);
    put32(&mut pe, 0x168, 0x2000); // CLI header RVA.
    put32(&mut pe, 0x178 + 12, 0x2000);
    put32(&mut pe, 0x178 + 16, 0x80 + root.len());
    put32(&mut pe, 0x178 + 20, 0x200);
    put32(&mut pe, 0x208, 0x2080); // Metadata RVA and size.
    put32(&mut pe, 0x20c, root.len());
    pe[0x280..].copy_from_slice(&root);
    pe
}
