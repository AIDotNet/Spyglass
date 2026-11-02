// Avalonia remote-preview wire protocol (Avalonia.Remote.Protocol 12.1.4, BsonTcpTransport):
// frame = [int32 LE body length][16-byte Guid (.NET byte order)][BSON body]. GUIDs/fields from decompiled
// Avalonia.Remote.Protocol (design/preview/README.md §3).
import { decode, encode, Schema } from "./bson";

export enum PixelFormat { Rgb565 = 0, Rgba8888 = 1, Bgra8888 = 2 }
export enum MouseButton { None = 0, Left = 1, Right = 2, Middle = 3 }
/** InputModifiers as the protocol declares them (an array of single values, not a bit mask). */
export enum InputModifier { Alt = 0, Control = 1, Shift = 2, Windows = 3, LeftMouseButton = 4, RightMouseButton = 5, MiddleMouseButton = 6 }

interface MessageDef { guid: string; schema: Schema; nested?: Record<string, Schema> }

const exception: Schema = { ExceptionType: "str", Message: "str", LineNumber: "i32", LinePosition: "i32" };
const pointer: Schema = { Modifiers: "i32[]", X: "f64", Y: "f64" };

export const Messages = {
  StartDesignerSession: { guid: "854887CF-2694-4EB6-B499-7461B6FB96C7", schema: { SessionId: "str" } },
  UpdateXaml: { guid: "9AEC9A2E-6315-4066-B4BA-E9A9EFD0F8CC", schema: { Xaml: "str", AssemblyPath: "str", XamlFileProjectPath: "str" } },
  UpdateXamlResult: { guid: "B7A70093-0C5D-47FD-9261-22086D43A2E2", schema: { Error: "str", Handle: "str", Exception: "obj" }, nested: { Exception: exception } },
  ClientSupportedPixelFormats: { guid: "63481025-7016-43FE-BADC-F2FD0F88609E", schema: { Formats: "i32[]" } },
  ClientRenderInfo: { guid: "7A3c25d3-3652-438D-8EF1-86E942CC96C0", schema: { DpiX: "f64", DpiY: "f64" } },
  ClientViewportAllocated: { guid: "BD7A8DE6-3DB8-4A13-8583-D6D4AB189A31", schema: { Width: "f64", Height: "f64", DpiX: "f64", DpiY: "f64" } },
  MeasureViewport: { guid: "6E3C5310-E2B1-4C3D-8688-01183AA48C5B", schema: { Width: "f64", Height: "f64" } },
  RequestViewportResize: { guid: "9B47B3D8-61DF-4C38-ACD4-8C1BB72554AC", schema: { Width: "f64", Height: "f64" } },
  FrameReceived: { guid: "68014F8A-289D-4851-8D34-5367EDA7F827", schema: { SequenceId: "i64" } },
  Frame: { guid: "F58313EE-FE69-4536-819D-F52EDF201A0E", schema: { SequenceId: "i64", Format: "i32", Data: "bin", Width: "i32", Height: "i32", Stride: "i32", DpiX: "f64", DpiY: "f64" } },
  PointerMoved: { guid: "6228F0B9-99F2-4F62-A621-414DA2881648", schema: pointer },
  PointerPressed: { guid: "7E9E2818-F93F-411A-800E-6B1AEB11DA46", schema: { ...pointer, Button: "i32" } },
  PointerReleased: { guid: "4ADC84EE-E7C8-4BCF-986C-DE3A2F78EDE4", schema: { ...pointer, Button: "i32" } },
  Scroll: { guid: "79301A05-F02D-4B90-BB39-472563B504AE", schema: { ...pointer, DeltaX: "f64", DeltaY: "f64" } },
  KeyEvent: { guid: "1C3B691E-3D54-4237-BFB0-9FEA83BC1DB8", schema: { Modifiers: "i32[]", IsDown: "bool", Key: "i32", PhysicalKey: "i32", KeySymbol: "str" } },
  TextInput: { guid: "C174102E-7405-4594-916F-B10B8248A17D", schema: { Modifiers: "i32[]", Text: "str" } },
} satisfies Record<string, MessageDef>;

export type MessageName = keyof typeof Messages;

/** .NET Guid.ToByteArray(): first three groups little-endian. */
export function guidBytes(guid: string): Buffer {
  const h = guid.replace(/-/g, "").toLowerCase();
  const b = Buffer.from(h, "hex");
  return Buffer.concat([Buffer.from(b.subarray(0, 4)).reverse(), Buffer.from(b.subarray(4, 6)).reverse(), Buffer.from(b.subarray(6, 8)).reverse(), b.subarray(8, 16)]);
}

const byGuid = new Map<string, MessageName>(
  (Object.keys(Messages) as MessageName[]).map((n) => [guidBytes(Messages[n].guid).toString("hex"), n]));

export function encodeMessage(name: MessageName, value: Record<string, unknown>): Buffer {
  const def: MessageDef = Messages[name];
  const body = encode(value, def.schema, def.nested ?? {});
  const head = Buffer.alloc(4);
  head.writeInt32LE(body.length);
  return Buffer.concat([head, guidBytes(def.guid), body]);
}

export interface DecodedMessage { name?: MessageName; guid: string; body: Record<string, any> }

/** Reassembles frames from arbitrary TCP chunks. */
export class FrameParser {
  private buf: Buffer = Buffer.alloc(0);
  push(chunk: Buffer): DecodedMessage[] {
    this.buf = this.buf.length ? Buffer.concat([this.buf, chunk]) : chunk;
    const out: DecodedMessage[] = [];
    while (this.buf.length >= 20) {
      const len = this.buf.readInt32LE(0);
      if (len < 5 || len > 256 * 1024 * 1024) throw new Error(`invalid preview frame length ${len}`);
      if (this.buf.length < 20 + len) break;
      const guid = this.buf.subarray(4, 20).toString("hex");
      out.push({ name: byGuid.get(guid), guid, body: decode(this.buf.subarray(20, 20 + len)) });
      this.buf = this.buf.subarray(20 + len);
    }
    return out;
  }
}
