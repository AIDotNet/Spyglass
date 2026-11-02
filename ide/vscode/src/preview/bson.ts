// Minimal BSON codec for the Avalonia remote-preview protocol (Metsys.Bson in Avalonia.Remote.Protocol).
// Written by hand instead of using the `bson` package because that serializer only understands binary subtype 2
// (old binary: [len+4][0x02][len][bytes]) and Guid subtype 3, and distinguishes int32/int64/double exactly: the
// writer therefore takes an explicit schema instead of guessing types from JS numbers.

export type FieldType = "i32" | "i64" | "f64" | "str" | "bool" | "bin" | "i32[]" | "obj";
export type Schema = Record<string, FieldType>;

const cstr = (s: string) => Buffer.concat([Buffer.from(s, "utf8"), Buffer.from([0])]);

function element(type: number, name: string, payload: Buffer): Buffer {
  return Buffer.concat([Buffer.from([type]), cstr(name), payload]);
}

const i32 = (n: number) => { const b = Buffer.alloc(4); b.writeInt32LE(n | 0); return b; };

/** Serializes `value` (an object) with the given field types. null/undefined fields are written as BSON null. */
export function encode(value: Record<string, unknown>, schema: Schema, nested: Record<string, Schema> = {}): Buffer {
  const parts: Buffer[] = [];
  for (const [name, type] of Object.entries(schema)) {
    const v = value[name];
    if (v === undefined || v === null) { parts.push(element(0x0a, name, Buffer.alloc(0))); continue; }
    switch (type) {
      case "i32": parts.push(element(0x10, name, i32(v as number))); break;
      case "i64": { const b = Buffer.alloc(8); b.writeBigInt64LE(BigInt(v as number)); parts.push(element(0x12, name, b)); break; }
      case "f64": { const b = Buffer.alloc(8); b.writeDoubleLE(v as number); parts.push(element(0x01, name, b)); break; }
      case "bool": parts.push(element(0x08, name, Buffer.from([v ? 1 : 0]))); break;
      case "str": { const s = Buffer.from(v as string, "utf8"); parts.push(element(0x02, name, Buffer.concat([i32(s.length + 1), s, Buffer.from([0])]))); break; }
      case "bin": { const d = v as Buffer; parts.push(element(0x05, name, Buffer.concat([i32(d.length + 4), Buffer.from([2]), i32(d.length), d]))); break; }
      case "i32[]": {
        const items = (v as number[]).map((n, i) => element(0x10, String(i), i32(n)));
        parts.push(element(0x04, name, Buffer.concat([i32(4 + items.reduce((a, b) => a + b.length, 0) + 1), ...items, Buffer.from([0])])));
        break;
      }
      case "obj": parts.push(element(0x03, name, encode(v as Record<string, unknown>, nested[name] ?? {}))); break;
    }
  }
  const body = Buffer.concat(parts);
  return Buffer.concat([i32(4 + body.length + 1), body, Buffer.from([0])]);
}

/** Parses a BSON document into plain JS (int64 → number, binary → Buffer, arrays → arrays, null → null). */
export function decode(buf: Buffer, offset = 0): Record<string, any> {
  return readDocument(buf, offset).value;
}

function readDocument(buf: Buffer, start: number, asArray = false): { value: any; end: number } {
  const size = buf.readInt32LE(start);
  const end = start + size;
  const out: any = asArray ? [] : {};
  let p = start + 4;
  while (p < end - 1) {
    const type = buf[p++];
    let z = p; while (buf[z] !== 0) z++;
    const name = buf.toString("utf8", p, z);
    p = z + 1;
    let v: any;
    switch (type) {
      case 0x01: v = buf.readDoubleLE(p); p += 8; break;
      case 0x02: { const n = buf.readInt32LE(p); v = buf.toString("utf8", p + 4, p + 4 + n - 1); p += 4 + n; break; }
      case 0x03: { const r = readDocument(buf, p); v = r.value; p = r.end; break; }
      case 0x04: { const r = readDocument(buf, p, true); v = r.value; p = r.end; break; }
      case 0x05: {
        const n = buf.readInt32LE(p); const sub = buf[p + 4];
        if (sub === 2) { const m = buf.readInt32LE(p + 5); v = Buffer.from(buf.subarray(p + 9, p + 9 + m)); }
        else v = Buffer.from(buf.subarray(p + 5, p + 5 + n));
        p += 5 + n; break;
      }
      case 0x08: v = buf[p++] !== 0; break;
      case 0x0a: v = null; break;
      case 0x10: v = buf.readInt32LE(p); p += 4; break;
      case 0x12: v = Number(buf.readBigInt64LE(p)); p += 8; break;
      default: throw new Error(`unsupported BSON type 0x${type.toString(16)} for "${name}"`);
    }
    if (asArray) out.push(v); else out[name] = v;
  }
  return { value: out, end };
}
