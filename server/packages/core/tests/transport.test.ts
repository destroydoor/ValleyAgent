import { test, expect } from "bun:test";
import { BunWebSocketTransport } from "../src/bun-transport";

test("BunWebSocketTransport implements Transport interface", () => {
  const transport = new BunWebSocketTransport({ port: 18799 });
  expect(typeof transport.start).toBe("function");
  expect(typeof transport.stop).toBe("function");
  expect(typeof transport.broadcast).toBe("function");
  expect(typeof transport.sendTo).toBe("function");
  expect(typeof transport.onMessage).toBe("function");
  expect(typeof transport.onConnect).toBe("function");
  expect(typeof transport.onDisconnect).toBe("function");
});

test("start begins listening on specified port", async () => {
  const transport = new BunWebSocketTransport({ port: 18801 });
  await transport.start();
  expect(transport.isRunning()).toBe(true);
  await transport.stop();
  expect(transport.isRunning()).toBe(false);
});

test("stop is idempotent", async () => {
  const transport = new BunWebSocketTransport({ port: 18802 });
  await transport.start();
  await transport.stop();
  await transport.stop();
});

test("onConnect callback fires when client connects", async () => {
  const transport = new BunWebSocketTransport({ port: 18803 });
  let connected = false;
  transport.onConnect((conn) => {
    connected = true;
    expect(conn.id).toBeDefined();
  });
  await transport.start();

  const ws = new WebSocket("ws://localhost:18803");
  await new Promise((resolve) => { ws.onopen = () => resolve(null); });
  await new Promise((resolve) => setTimeout(resolve, 50));
  expect(connected).toBe(true);

  ws.close();
  await transport.stop();
});

test("onMessage callback fires when client sends message", async () => {
  const transport = new BunWebSocketTransport({ port: 18804 });
  let receivedMessage: string | null = null;

  transport.onMessage((conn, data) => {
    receivedMessage = typeof data === "string" ? data : data.toString();
  });
  await transport.start();

  const ws = new WebSocket("ws://localhost:18804");
  await new Promise((resolve) => { ws.onopen = () => resolve(null); });
  ws.send("hello world");
  await new Promise((resolve) => setTimeout(resolve, 50));
  expect(receivedMessage).toBe("hello world");

  ws.close();
  await transport.stop();
});

test("sendTo sends message to specific connection", async () => {
  const transport = new BunWebSocketTransport({ port: 18805 });
  let connId: string | null = null;
  transport.onConnect((conn) => { connId = conn.id; });
  await transport.start();

  const ws = new WebSocket("ws://localhost:18805");
  let received = "";
  await new Promise((resolve) => { ws.onopen = () => resolve(null); });
  ws.onmessage = (event) => { received = event.data as string; };
  await new Promise((resolve) => setTimeout(resolve, 50));
  transport.sendTo(connId!, "server says hi");
  await new Promise((resolve) => setTimeout(resolve, 50));
  expect(received).toBe("server says hi");

  ws.close();
  await transport.stop();
});

test("broadcast sends to all connections", async () => {
  const transport = new BunWebSocketTransport({ port: 18806 });
  await transport.start();

  const ws1 = new WebSocket("ws://localhost:18806");
  const ws2 = new WebSocket("ws://localhost:18806");
  let received1 = "";
  let received2 = "";

  await Promise.all([
    new Promise((r) => { ws1.onopen = () => r(null); }),
    new Promise((r) => { ws2.onopen = () => r(null); }),
  ]);
  ws1.onmessage = (e) => (received1 = e.data as string);
  ws2.onmessage = (e) => (received2 = e.data as string);
  await new Promise((resolve) => setTimeout(resolve, 50));
  transport.broadcast("broadcast message");
  await new Promise((resolve) => setTimeout(resolve, 50));

  expect(received1).toBe("broadcast message");
  expect(received2).toBe("broadcast message");

  ws1.close();
  ws2.close();
  await transport.stop();
});

test("onDisconnect fires when client disconnects", async () => {
  const transport = new BunWebSocketTransport({ port: 18807 });
  let disconnected = false;
  transport.onDisconnect((conn) => {
    disconnected = true;
    expect(conn.id).toBeDefined();
  });
  await transport.start();

  const ws = new WebSocket("ws://localhost:18807");
  await new Promise((resolve) => { ws.onopen = () => resolve(null); });
  ws.close();
  await new Promise((resolve) => setTimeout(resolve, 100));
  expect(disconnected).toBe(true);

  await transport.stop();
});

test("getConnectionCount returns active connections", async () => {
  const transport = new BunWebSocketTransport({ port: 18808 });
  await transport.start();

  const ws1 = new WebSocket("ws://localhost:18808");
  const ws2 = new WebSocket("ws://localhost:18808");
  await Promise.all([
    new Promise((r) => { ws1.onopen = () => r(null); }),
    new Promise((r) => { ws2.onopen = () => r(null); }),
  ]);
  await new Promise((resolve) => setTimeout(resolve, 50));
  expect(transport.getConnectionCount()).toBe(2);

  ws1.close();
  ws2.close();
  await new Promise((resolve) => setTimeout(resolve, 100));
  expect(transport.getConnectionCount()).toBe(0);

  await transport.stop();
});
