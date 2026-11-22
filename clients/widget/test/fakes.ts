import type { HubLike } from "../src/client";
import type { WireEvent } from "../src/protocol";

/** A scripted hub: `handlers[method]` answers invocations; `push` delivers server events; `reconnect` simulates a reconnect. */
export class FakeHub implements HubLike {
  readonly calls: { method: string; args: unknown[] }[] = [];
  private readonly events = new Map<string, (payload: never) => void>();
  private reconnected: (() => void) | undefined;

  constructor(readonly handlers: Record<string, (...args: unknown[]) => unknown>) {}

  async start(): Promise<void> {}
  async stop(): Promise<void> {}

  async invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    this.calls.push({ method, args });
    const handler = this.handlers[method];
    if (!handler) return { ok: false, error: "unknown" } as T;
    return handler(...args) as T;
  }

  on(method: string, handler: (payload: never) => void): void {
    this.events.set(method, handler);
  }

  onreconnected(handler: () => void): void {
    this.reconnected = handler;
  }

  onclose(): void {}

  push(event: WireEvent): void {
    this.events.get("tf.event")?.(event as never);
  }

  reconnect(): void {
    this.reconnected?.();
  }
}

export const widgetState = {
  greeting: "Hi there",
  online: true,
  deflection: false,
  requireVerified: false,
  attachments: null,
  prechat: [{ key: "name", label: "Name", kind: "name", required: true, maxLength: 64 }],
};

export function memoryTokens(initial: string | null = null) {
  let token = initial;
  return { get: () => token, set: (value: string | null) => void (token = value) };
}

export const flush = () => new Promise((resolve) => setTimeout(resolve, 0));
