// Minimal JSON-RPC 2.0 peer over a WebSocket (the hub's /ide endpoint speaks the same framing as Spyglass's
// agent protocol: one JSON message per text frame, requests in both directions).
import WebSocket from "ws";

export class RpcError extends Error {
  constructor(public readonly code: number, message: string) {
    super(message);
  }
}

type Handler = (params: any) => unknown | Promise<unknown>;

export class RpcPeer {
  private nextId = 1;
  private readonly pending = new Map<number, { resolve: (v: any) => void; reject: (e: Error) => void; timer: NodeJS.Timeout }>();
  private readonly notifications = new Map<string, (params: any) => void>();
  private readonly requests = new Map<string, Handler>();

  constructor(private readonly socket: WebSocket) {
    socket.on("message", (data) => this.receive(data.toString()));
    socket.on("close", () => {
      for (const p of this.pending.values()) {
        clearTimeout(p.timer);
        p.reject(new Error("connection closed"));
      }
      this.pending.clear();
    });
  }

  get isOpen(): boolean {
    return this.socket.readyState === WebSocket.OPEN;
  }

  onNotification(method: string, handler: (params: any) => void): void {
    this.notifications.set(method, handler);
  }

  onRequest(method: string, handler: Handler): void {
    this.requests.set(method, handler);
  }

  request<T>(method: string, params: unknown, timeoutMs = 10000): Promise<T> {
    const id = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`${method} timed out`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.send({ jsonrpc: "2.0", id, method, params });
    });
  }

  notify(method: string, params: unknown): void {
    if (this.isOpen) this.send({ jsonrpc: "2.0", method, params });
  }

  close(): void {
    this.socket.close();
  }

  private send(message: unknown): void {
    this.socket.send(JSON.stringify(message));
  }

  private receive(text: string): void {
    let msg: any;
    try {
      msg = JSON.parse(text);
    } catch {
      return;
    }
    if (msg.method !== undefined) {
      if (msg.id === undefined || msg.id === null) {
        this.notifications.get(msg.method)?.(msg.params);
        return;
      }
      void this.answer(msg.id, msg.method, msg.params);
      return;
    }
    const p = this.pending.get(msg.id);
    if (!p) return;
    this.pending.delete(msg.id);
    clearTimeout(p.timer);
    if (msg.error) p.reject(new RpcError(msg.error.code, msg.error.message));
    else p.resolve(msg.result);
  }

  private async answer(id: number | string, method: string, params: unknown): Promise<void> {
    const handler = this.requests.get(method);
    if (!handler) {
      this.send({ jsonrpc: "2.0", id, error: { code: -32601, message: `Method not found: ${method}` } });
      return;
    }
    try {
      const result = await handler(params);
      this.send({ jsonrpc: "2.0", id, result: result ?? {} });
    } catch (e: any) {
      this.send({ jsonrpc: "2.0", id, error: { code: -32603, message: String(e?.message ?? e) } });
    }
  }
}
