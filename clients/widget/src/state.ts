import type { ChatMessage } from "./protocol";

/** Messages per conversation, ordered by sequence and de-duplicated by message id (pushes and refetches overlap). */
export class Transcript {
  private readonly byConversation = new Map<string, ChatMessage[]>();
  private readonly indexByConversation = new Map<string, Map<string, number>>();

  /** Adds or replaces messages; returns true when anything changed. */
  merge(messages: ChatMessage[]): boolean {
    let changed = false;
    let dirty: Set<string> | undefined;
    for (const message of messages) {
      let list = this.byConversation.get(message.conversation);
      let indexMap = this.indexByConversation.get(message.conversation);
      if (!list) {
        list = [];
        indexMap = new Map();
        this.byConversation.set(message.conversation, list);
        this.indexByConversation.set(message.conversation, indexMap);
      }
      const index = indexMap!.get(message.message);
      if (index !== undefined) {
        const existing = list[index]!;
        if (existing.sequence !== message.sequence || existing.body !== message.body) {
          const seqChanged = existing.sequence !== message.sequence;
          list[index] = message;
          changed = true;
          if (seqChanged) (dirty ??= new Set()).add(message.conversation);
        }
      } else {
        const prevSeq = list.length > 0 ? list[list.length - 1]!.sequence : -Infinity;
        indexMap!.set(message.message, list.length);
        list.push(message);
        changed = true;
        if (message.sequence < prevSeq) (dirty ??= new Set()).add(message.conversation);
      }
    }
    if (dirty) {
      for (const conversation of dirty) {
        const list = this.byConversation.get(conversation)!;
        const indexMap = this.indexByConversation.get(conversation)!;
        list.sort((left, right) => left.sequence - right.sequence);
        indexMap.clear();
        for (let i = 0; i < list.length; i++) indexMap.set(list[i]!.message, i);
      }
    }
    return changed;
  }

  messages(conversation: string): readonly ChatMessage[] {
    return this.byConversation.get(conversation) ?? [];
  }

  /** The highest sequence seen in a conversation (the fetch-on-reconnect cursor). */
  cursor(conversation: string): number {
    const list = this.byConversation.get(conversation);
    return list && list.length > 0 ? list[list.length - 1]!.sequence : 0;
  }

  conversations(): string[] {
    return [...this.byConversation.keys()];
  }
}

/** Storage for the session token: localStorage when available, else memory. Never logs the token. */
export interface TokenStore {
  get(): string | null;
  set(token: string | null): void;
}

/** Pass `storage: undefined` explicitly for memory-only storage. */
export function tokenStore(widgetKey: string, ...args: [storage?: Storage | undefined]): TokenStore {
  const storage = args.length > 0 ? args[0] : globalThis.localStorage;
  const key = `ticketflow.chat.${widgetKey}`;
  let memory: string | null = null;
  return {
    get() {
      try {
        return storage?.getItem(key) ?? memory;
      } catch {
        return memory;
      }
    },
    set(token) {
      memory = token;
      try {
        if (token === null) storage?.removeItem(key);
        else storage?.setItem(key, token);
      } catch {
        // Storage may be unavailable (privacy modes); the in-memory token still works for this page.
      }
    },
  };
}

/** A client message id: unique per send attempt, reused on retries so the server de-duplicates. */
export function clientMessageId(random: () => number = Math.random): string {
  return `m${Date.now().toString(36)}${Math.floor(random() * 1e9).toString(36)}`;
}

/** Calls `onChange(true)` on the first keystroke and `onChange(false)` after `idleMs` without input. */
export class TypingNotifier {
  private timer: ReturnType<typeof setTimeout> | undefined;
  private active = false;

  constructor(private readonly onChange: (typing: boolean) => void, private readonly idleMs = 3000) {}

  keystroke(): void {
    if (!this.active) {
      this.active = true;
      this.onChange(true);
    }
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.stop(), this.idleMs);
  }

  stop(): void {
    clearTimeout(this.timer);
    if (this.active) {
      this.active = false;
      this.onChange(false);
    }
  }
}
