import { describe, expect, it } from "vitest";
import { ChatClient } from "../src/client";
import { FakeHub, flush, memoryTokens, widgetState } from "./fakes";

function setup(token: string | null = null) {
  const tokens = memoryTokens(token);
  const fetched: number[] = [];
  const hub = new FakeHub({
    Connect: (_key, provided) => provided === "stale"
      ? { ok: false, error: "unauthenticated" }
      : { ok: true, data: { token: provided ? null : "tfvs1.vs_1.secret", visitorId: "v1", verified: false, prechatAnswered: true, widget: widgetState,
          conversations: provided ? [{ conversation: "c1", closed: false, lastSequence: 4, readSequence: 0, lastActivityAt: "" }] : [] } },
    FetchMessages: (_conversation, after) => {
      fetched.push(after as number);
      return { ok: true, data: { conversation: "c1", readSequence: 0, next: null, messages: (after as number) < 4
        ? [{ message: "m4", sequence: 4, direction: "outgoing", body: "Welcome back", contentType: "plain-text", attachments: [], at: "" }] : [] } };
    },
    SendMessage: (id) => ({ ok: true, data: { conversation: "c1", message: `msg-${String(id)}`, sequence: 5, started: false, replayed: false } }),
    MarkRead: (_c, sequence) => ({ ok: true, data: sequence }),
    Typing: () => ({ ok: true }),
  });
  const client = new ChatClient({ hubUrl: "https://x/hub", widgetKey: "wk", tokens, connect: () => hub });
  return { client, hub, tokens, fetched };
}

describe("ChatClient", () => {
  it("starts a session and stores the issued token", async () => {
    const { client, tokens, hub } = setup();
    await client.start();
    expect(tokens.get()).toBe("tfvs1.vs_1.secret");
    expect(hub.calls[0]).toEqual({ method: "Connect", args: ["wk", null] });
    expect(client.widget?.greeting).toBe("Hi there");
  });

  it("drops a rejected token and starts over", async () => {
    const { client, tokens, hub } = setup("stale");
    await client.start();
    expect(hub.calls.filter((call) => call.method === "Connect").map((call) => call.args[1])).toEqual(["stale", null]);
    expect(tokens.get()).toBe("tfvs1.vs_1.secret");
  });

  it("resumes with the token and fetches after the cursor on reconnect", async () => {
    const { client, hub, fetched } = setup("tfvs1.vs_1.secret");
    await client.start();
    expect(client.conversation).toBe("c1");
    expect(client.transcript.messages("c1").map((item) => item.body)).toEqual(["Welcome back"]);
    hub.push({ v: 1, type: "message", topic: "visitor", data: { conversation: "c1", message: "m6", sequence: 6, direction: "outgoing", body: "Live", contentType: "plain-text", attachments: [], at: "" } });
    hub.push({ v: 1, type: "message", topic: "visitor", data: { conversation: "c1", message: "m6", sequence: 6, direction: "outgoing", body: "Live", contentType: "plain-text", attachments: [], at: "" } });
    expect(client.transcript.messages("c1")).toHaveLength(2);
    hub.reconnect();
    await flush();
    await flush();
    expect(fetched).toEqual([0, 6]);
  });

  it("sends with a client message id, emits typing and read receipts", async () => {
    const { client, hub } = setup("tfvs1.vs_1.secret");
    await client.start();
    const events: string[] = [];
    client.subscribe((event) => events.push(event.type));
    await client.send("Hello");
    await client.typing(true);
    await client.markRead();
    const send = hub.calls.find((call) => call.method === "SendMessage")!;
    expect(send.args[0]).toMatch(/^m/);
    expect(send.args[1]).toBe("Hello");
    expect(hub.calls.find((call) => call.method === "MarkRead")!.args).toEqual(["c1", 5]);
    expect(events).toContain("messages");
    hub.push({ v: 1, type: "typing", topic: "visitor", data: { conversation: "c1", actor: "support", typing: true } });
    hub.push({ v: 2, type: "typing", topic: "visitor", data: {} });
    expect(events.filter((type) => type === "typing")).toHaveLength(1);
  });

  it("uploads attachments with the bearer token", async () => {
    const requests: { url: string; init: RequestInit }[] = [];
    const tokens = memoryTokens("tfvs1.vs_1.secret");
    const client = new ChatClient({
      hubUrl: "https://x/hub", widgetKey: "wk", uploadUrl: "https://x/upload", tokens, connect: () => new FakeHub({}),
      fetch: (async (url: string, init: RequestInit) => {
        requests.push({ url, init });
        return new Response(JSON.stringify({ storageRef: "ref-1", fileName: "a b.png", mediaType: "image/png", sizeBytes: 3 }), { status: 200 });
      }) as unknown as typeof fetch,
    });
    const attachment = await client.upload(Object.assign(new Blob([new Uint8Array([1, 2, 3])], { type: "image/png" }), { name: "a b.png" }));
    expect(attachment.storageRef).toBe("ref-1");
    const headers = requests[0]!.init.headers as Record<string, string>;
    expect(headers.Authorization).toBe("Bearer tfvs1.vs_1.secret");
    expect(headers["X-TicketFlow-File-Name"]).toBe("a%20b.png");
  });
});
