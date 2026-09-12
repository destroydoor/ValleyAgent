import type { ServerWebSocket } from "bun";
import type {
  Transport,
  Connection,
  MessageHandler,
  ConnectHandler,
  DisconnectHandler,
  TransportConfig,
} from "./transport";

interface BunWebSocketData {
  connId: string;
  metadata: Record<string, unknown>;
}

export class BunWebSocketTransport implements Transport {
  private server: ReturnType<typeof Bun.serve> | null = null;
  private connections: Map<string, { ws: ServerWebSocket<BunWebSocketData>; conn: Connection }> = new Map();
  private messageHandler: MessageHandler | null = null;
  private connectHandler: ConnectHandler | null = null;
  private disconnectHandler: DisconnectHandler | null = null;
  private connCounter = 0;
  private readonly config: Required<TransportConfig>;

  constructor(config: TransportConfig) {
    this.config = {
      port: config.port,
      hostname: config.hostname ?? "127.0.0.1",
      idleTimeoutMs: config.idleTimeoutMs ?? 120000,
      backpressureLimit: config.backpressureLimit ?? 1024 * 1024,
    };
  }

  async start(): Promise<void> {
    if (this.server) return;

    this.server = Bun.serve<BunWebSocketData>({
      port: this.config.port,
      hostname: this.config.hostname,
      websocket: {
        idleTimeout: this.config.idleTimeoutMs / 1000,
        backpressureLimit: this.config.backpressureLimit,
        open: (ws) => {
          const connId = ws.data.connId;
          const conn: Connection = { id: connId, metadata: ws.data.metadata };
          this.connections.set(connId, { ws, conn });
          this.connectHandler?.(conn);
        },
        message: (ws, message) => {
          const connId = ws.data.connId;
          const entry = this.connections.get(connId);
          if (entry) this.messageHandler?.(entry.conn, message);
        },
        close: (ws) => {
          const connId = ws.data.connId;
          const entry = this.connections.get(connId);
          if (entry) {
            this.connections.delete(connId);
            this.disconnectHandler?.(entry.conn);
          }
        },
      },
      fetch: (req, server) => {
        if (req.headers.get("upgrade") === "websocket") {
          const connId = `conn_${++this.connCounter}`;
          if (server.upgrade(req, { data: { connId, metadata: {} } })) {
            return new Response(null, { status: 204 });
          }
        }
        return new Response("Not Found", { status: 404 });
      },
    });
  }

  async stop(): Promise<void> {
    if (!this.server) return;
    for (const [, { ws }] of this.connections) {
      ws.close();
    }
    this.connections.clear();
    this.server.stop(true);
    this.server = null;
  }

  broadcast(message: string): void {
    for (const [, { ws }] of this.connections) {
      ws.send(message);
    }
  }

  sendTo(connId: string, message: string): void {
    const entry = this.connections.get(connId);
    if (entry) entry.ws.send(message);
  }

  onMessage(handler: MessageHandler): void { this.messageHandler = handler; }
  onConnect(handler: ConnectHandler): void { this.connectHandler = handler; }
  onDisconnect(handler: DisconnectHandler): void { this.disconnectHandler = handler; }

  isRunning(): boolean { return this.server !== null; }
  getConnectionCount(): number { return this.connections.size; }
  getConnections(): Connection[] {
    return [...this.connections.values()].map((e) => e.conn);
  }
}
