export interface Connection {
  id: string;
  metadata: Record<string, unknown>;
}

export type MessageHandler = (conn: Connection, data: string | Buffer) => void;
export type ConnectHandler = (conn: Connection) => void;
export type DisconnectHandler = (conn: Connection) => void;

export interface Transport {
  start(): Promise<void>;
  stop(): Promise<void>;
  broadcast(message: string): void;
  sendTo(connId: string, message: string): void;
  onMessage(handler: MessageHandler): void;
  onConnect(handler: ConnectHandler): void;
  onDisconnect(handler: DisconnectHandler): void;
}

export interface TransportConfig {
  port: number;
  hostname?: string;
  idleTimeoutMs?: number;
  backpressureLimit?: number;
}
