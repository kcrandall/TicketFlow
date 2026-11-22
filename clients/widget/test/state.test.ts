import { describe, expect, it, vi } from "vitest";
import { Transcript, TypingNotifier, clientMessageId, tokenStore } from "../src/state";

const message = (id: string, sequence: number, body = id) => ({
  conversation: "c1", message: id, sequence, direction: "outgoing" as const, body, contentType: "plain-text", attachments: [], at: "",
});

describe("Transcript", () => {
  it("orders by sequence, de-duplicates by message id and tracks the cursor", () => {
    const transcript = new Transcript();
    expect(transcript.merge([message("b", 3), message("a", 1)])).toBe(true);
    expect(transcript.merge([message("a", 1)])).toBe(false);
    expect(transcript.messages("c1").map((item) => item.message)).toEqual(["a", "b"]);
    expect(transcript.cursor("c1")).toBe(3);
    expect(transcript.cursor("other")).toBe(0);
  });
});

describe("tokenStore", () => {
  it("persists to storage and survives storage failures", () => {
    const backing = new Map<string, string>();
    const storage = { getItem: (key: string) => backing.get(key) ?? null, setItem: (key: string, value: string) => void backing.set(key, value), removeItem: (key: string) => void backing.delete(key) } as unknown as Storage;
    const store = tokenStore("wk", storage);
    store.set("tfvs1.a.b");
    expect(backing.get("ticketflow.chat.wk")).toBe("tfvs1.a.b");
    store.set(null);
    expect(store.get()).toBeNull();
    const broken = tokenStore("wk", { getItem: () => { throw new Error("denied"); }, setItem: () => { throw new Error("denied"); } } as unknown as Storage);
    broken.set("t");
    expect(broken.get()).toBe("t");
  });
});

describe("TypingNotifier", () => {
  it("signals start once and stop after idle", () => {
    vi.useFakeTimers();
    const changes: boolean[] = [];
    const notifier = new TypingNotifier((typing) => changes.push(typing), 1000);
    notifier.keystroke();
    notifier.keystroke();
    vi.advanceTimersByTime(999);
    expect(changes).toEqual([true]);
    vi.advanceTimersByTime(1);
    expect(changes).toEqual([true, false]);
    vi.useRealTimers();
  });
});

describe("clientMessageId", () => {
  it("is printable ASCII within 64 characters", () => {
    expect(clientMessageId()).toMatch(/^[a-z0-9]{2,64}$/);
  });
});
