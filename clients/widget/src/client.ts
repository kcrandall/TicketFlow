import { HubConnectionBuilder, HttpTransportType, LogLevel } from "@microsoft/signalr";
import {
  EVENT_METHOD,
  type ArticleHit,
  type Attachment,
  type ChatMessage,
  type ConnectData,
  type HubResult,
  type MessagePage,
  type SendResult,
  type WidgetState,
  type WireEvent,
} from "./protocol";
import { Transcript, clientMessageId, tokenStore, type TokenStore } from "./state";

/** The subset of a SignalR HubConnection the client uses (replaceable in tests). */
export interface HubLike {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke<T = unknown>(method: string, ...args: unknown[]): Promise<T>;
  on(method: string, handler: (payload: never) => void): void;
  onreconnected(handler: () => void): void;
  onclose(handler: (error?: Error) => void): void;
}

export interface ChatClientOptions {
  /** Visitor hub URL, for example https://support.example.com/ticketflow/realtime/visitor. */
  hubUrl: string;
  /** The public widget key (the live-chat inbox's channel endpoint address). */
  widgetKey: string;
  /** Optional attachment upload URL (MapLiveChatAttachmentUpload). */
  uploadUrl?: string;
  /**
   * Where the session token lives: `"local"` (default, `localStorage`, survives reloads) or `"memory"` (this page only; a reload
   * starts a new session). Any script running on the page (for example through XSS) can read a `localStorage` token and act as the
   * visitor until it expires or is revoked; choose `"memory"` where that matters more than continuity.
   */
  tokenStorage?: "local" | "memory";
  tokens?: TokenStore;
  connect?: (url: string) => HubLike;
  fetch?: typeof fetch;
}

export type ChatEvent =
  | { type: "connected"; data: ConnectData }
  | { type: "messages"; conversation: string }
  | { type: "typing"; conversation: string; typing: boolean }
  | { type: "read"; conversation: string; upToSequence: number }
  | { type: "ended" }
  | { type: "error"; error: string };

export class ChatError extends Error {
  constructor(readonly code: string) {
    super(`TicketFlow chat error: ${code}`);
  }
}

/**
 * Visitor-side client: session token handshake, sending, typing, read receipts, attachments, deflection and
 * fetch-on-reconnect from the highest sequence seen per conversation.
 */
export class ChatClient {
  readonly transcript = new Transcript();
  widget: WidgetState | null = null;
  conversation: string | null = null;
  prechatAnswered = false;
  private readonly hub: HubLike;
  private readonly tokens: TokenStore;
  private readonly listeners = new Set<(event: ChatEvent) => void>();
  private readonly lastMarkedRead = new Map<string, number>();

  constructor(private readonly options: ChatClientOptions) {
    this.tokens = options.tokens ?? tokenStore(options.widgetKey, options.tokenStorage === "memory" ? undefined : globalThis.localStorage);
    this.hub = (options.connect ?? defaultConnect)(options.hubUrl);
    this.hub.on(EVENT_METHOD, (payload: never) => this.onEvent(payload as WireEvent));
    this.hub.onreconnected(() => void this.handshake().then((data) => this.catchUp(data)).catch((error) => this.emit({ type: "error", error: code(error) })));
  }

  subscribe(listener: (event: ChatEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  async start(): Promise<ConnectData> {
    await this.hub.start();
    const data = await this.handshake();
    await this.catchUp(data);
    return data;
  }

  async stop(): Promise<void> {
    await this.hub.stop();
  }

  async submitPrechat(answers: Record<string, string>): Promise<void> {
    await this.call("SubmitPrechat", answers);
    this.prechatAnswered = true;
  }

  /** Verifies the visitor: `hmac` (value = user id, signature = hex HMAC from the site's backend) or `token` (value = signed token). */
  async identify(method: "hmac" | "token", value: string, signature?: string, displayName?: string): Promise<void> {
    await this.call("Identify", method, value, signature ?? null, displayName ?? null);
  }

  async send(text: string, attachments: Attachment[] = []): Promise<SendResult> {
    const id = clientMessageId();
    const result = await this.retry(() => this.call<SendResult>("SendMessage", id, text || null, attachments.length ? attachments.map((item) => item.storageRef) : null));
    this.conversation = result.conversation;
    this.transcript.merge([{ conversation: result.conversation, message: result.message, sequence: result.sequence, direction: "incoming", body: text, contentType: "plain-text", attachments, at: new Date().toISOString() }]);
    this.emit({ type: "messages", conversation: result.conversation });
    return result;
  }

  async typing(typing: boolean): Promise<void> {
    if (this.conversation) await this.call("Typing", this.conversation, typing).catch(() => undefined);
  }

  async markRead(): Promise<void> {
    if (!this.conversation) return;
    const cursor = this.transcript.cursor(this.conversation);
    if (cursor > 0 && (this.lastMarkedRead.get(this.conversation) ?? 0) < cursor) {
      this.lastMarkedRead.set(this.conversation, cursor);
      await this.call("MarkRead", this.conversation, cursor).catch(() => {
        if (this.lastMarkedRead.get(this.conversation!) === cursor) this.lastMarkedRead.delete(this.conversation!);
      });
    }
  }

  async search(text: string): Promise<ArticleHit[]> {
    return this.call<ArticleHit[]>("SearchArticles", text);
  }

  async upload(file: Blob & { name?: string }): Promise<Attachment> {
    const token = this.tokens.get();
    if (!this.options.uploadUrl || !token) throw new ChatError("unavailable");
    const response = await (this.options.fetch ?? fetch)(this.options.uploadUrl, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": file.type || "application/octet-stream",
        "X-TicketFlow-File-Name": encodeURIComponent(file.name ?? "attachment"),
      },
      body: file,
    });
    if (!response.ok) throw new ChatError(response.status === 413 ? "too-large" : response.status === 415 ? "unsupported-media-type" : "upload-failed");
    return (await response.json()) as Attachment;
  }

  /** Authenticates the stored token (or starts a session) and keeps any issued or rotated token. */
  private async handshake(): Promise<ConnectData> {
    let result = await this.hub.invoke<HubResult<ConnectData>>("Connect", this.options.widgetKey, this.tokens.get());
    if (!result.ok && result.error === "unauthenticated") {
      this.tokens.set(null);
      result = await this.hub.invoke<HubResult<ConnectData>>("Connect", this.options.widgetKey, null);
    }
    if (!result.ok || !result.data) throw new ChatError(result.error ?? "unavailable");
    const data = result.data;
    if (data.token) this.tokens.set(data.token);
    this.widget = data.widget ?? null;
    this.prechatAnswered = data.prechatAnswered;
    const open = data.conversations.find((item) => !item.closed) ?? data.conversations[0];
    this.conversation = open?.conversation ?? this.conversation;
    this.emit({ type: "connected", data });
    return data;
  }

  /** Fetches every message after the highest sequence seen, for the current and known conversations. */
  private async catchUp(data?: ConnectData): Promise<void> {
    const serverSeqs = new Map<string, number>();
    if (data) for (const item of data.conversations) serverSeqs.set(item.conversation, item.lastSequence);
    const conversations = new Set(this.transcript.conversations());
    if (this.conversation) conversations.add(this.conversation);
    for (const conversation of conversations) {
      let after = this.transcript.cursor(conversation);
      const serverSeq = serverSeqs.get(conversation);
      if (after === 0 && serverSeq === 0) continue;
      let changed = false;
      for (let page = 0; page < 20; page++) {
        const result = await this.call<MessagePage>("FetchMessages", conversation, after, 100);
        if (result.messages.length > 0 && this.transcript.merge(result.messages.map((message) => ({ ...message, conversation }))))
          changed = true;
        if (result.next == null) break;
        after = result.next;
      }
      if (changed) this.emit({ type: "messages", conversation });
    }
  }

  private onEvent(event: WireEvent): void {
    if (event.v !== 1) return;
    const data = event.data;
    switch (event.type) {
      case "message": {
        const message = data as unknown as ChatMessage;
        this.conversation ??= message.conversation;
        if (this.transcript.merge([message])) this.emit({ type: "messages", conversation: message.conversation });
        break;
      }
      case "typing":
        this.emit({ type: "typing", conversation: String(data.conversation), typing: Boolean(data.typing) });
        break;
      case "read":
        this.emit({ type: "read", conversation: String(data.conversation), upToSequence: Number(data.upToSequence) });
        break;
      case "session.ended":
        this.tokens.set(null);
        this.emit({ type: "ended" });
        break;
    }
  }

  private async call<T>(method: string, ...args: unknown[]): Promise<T> {
    const result = await this.hub.invoke<HubResult<T>>(method, ...args);
    if (!result.ok) throw new ChatError(result.error ?? "error");
    return result.data as T;
  }

  private async retry<T>(action: () => Promise<T>): Promise<T> {
    try {
      return await action();
    } catch (error) {
      if (error instanceof ChatError) throw error;
      await new Promise((resolve) => setTimeout(resolve, 500));
      return action();
    }
  }

  private emit(event: ChatEvent): void {
    for (const listener of this.listeners) listener(event);
  }
}

function code(error: unknown): string {
  return error instanceof ChatError ? error.code : "connection-lost";
}

function defaultConnect(url: string): HubLike {
  const baseDelays = [0, 2000, 5000, 10000, 30000];
  const connection = new HubConnectionBuilder()
    .withUrl(url, { transport: HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling })
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds(context) {
        if (context.previousRetryCount >= baseDelays.length) return null;
        const base = baseDelays[context.previousRetryCount]!;
        return base === 0 ? 0 : Math.round(base * (0.75 + Math.random() * 0.5));
      },
    })
    .configureLogging(LogLevel.Warning)
    .build();
  return connection as unknown as HubLike;
}
