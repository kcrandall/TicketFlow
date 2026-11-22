import { describe, expect, it } from "vitest";
import { ChatClient } from "../src/client";
import { ChatWidget } from "../src/ui";
import { FakeHub, flush, memoryTokens, widgetState } from "./fakes";

function mount(prechatAnswered: boolean, deflection = false) {
  const hub = new FakeHub({
    Connect: () => ({ ok: true, data: { token: "t", visitorId: "v", verified: false, prechatAnswered, widget: { ...widgetState, deflection }, conversations: [] } }),
    SubmitPrechat: () => ({ ok: true, data: { verified: false } }),
    SearchArticles: () => ({ ok: true, data: [{ article: "a1", title: "Reset your password", slug: "reset", snippet: "", locale: "en" }] }),
    SendMessage: () => ({ ok: true, data: { conversation: "c1", message: "m1", sequence: 1, started: true, replayed: false } }),
    FetchMessages: () => ({ ok: true, data: { conversation: "c1", readSequence: 0, next: null, messages: [] } }),
    MarkRead: () => ({ ok: true, data: 1 }),
    Typing: () => ({ ok: true }),
  });
  const container = document.createElement("div");
  document.body.append(container);
  const client = new ChatClient({ hubUrl: "https://x/hub", widgetKey: "wk", tokens: memoryTokens(), connect: () => hub });
  const widget = new ChatWidget({ hubUrl: "https://x/hub", widgetKey: "wk", container, client, articleUrl: (hit) => `https://help.example/${hit.slug}` });
  return { widget, hub };
}

describe("ChatWidget", () => {
  it("renders inside a shadow root and asks for required pre-chat fields", async () => {
    const { widget, hub } = mount(false);
    await widget.start();
    const form = widget.root.querySelector<HTMLFormElement>(".prechat")!;
    expect(form).not.toBeNull();
    expect(widget.root.querySelector<HTMLFormElement>(".composer")!.hidden).toBe(true);
    form.querySelector<HTMLInputElement>("input[name=name]")!.value = "Ada";
    form.dispatchEvent(new Event("submit", { cancelable: true }));
    await flush();
    expect(hub.calls.find((call) => call.method === "SubmitPrechat")!.args[0]).toEqual({ name: "Ada" });
    expect(widget.root.querySelector(".prechat")).toBeNull();
    expect(widget.root.querySelector<HTMLFormElement>(".composer")!.hidden).toBe(false);
  });

  it("shows deflection results before the first message and escapes bodies", async () => {
    const { widget, hub } = mount(true, true);
    await widget.start();
    const text = widget.root.querySelector<HTMLTextAreaElement>(".text")!;
    const composer = widget.root.querySelector<HTMLFormElement>(".composer")!;
    text.value = "password reset";
    composer.dispatchEvent(new Event("submit", { cancelable: true }));
    await flush();
    expect(widget.root.querySelector<HTMLAnchorElement>(".articles a")!.href).toBe("https://help.example/reset");
    expect(hub.calls.some((call) => call.method === "SendMessage")).toBe(false);

    composer.dispatchEvent(new Event("submit", { cancelable: true }));
    await flush();
    expect(hub.calls.some((call) => call.method === "SendMessage")).toBe(true);
    hub.push({ v: 1, type: "message", topic: "visitor", data: { conversation: "c1", message: "m2", sequence: 2, direction: "outgoing", body: "<img src=x onerror=alert(1)>", contentType: "plain-text", attachments: [], at: "" } });
    const bubbles = widget.root.querySelectorAll(".bubble.outgoing");
    expect(bubbles[bubbles.length - 1]!.textContent).toBe("<img src=x onerror=alert(1)>");
    expect(widget.root.querySelector("img")).toBeNull();
  });

  it("shows the typing indicator from support", async () => {
    const { widget, hub } = mount(true);
    await widget.start();
    hub.push({ v: 1, type: "typing", topic: "visitor", data: { conversation: "c1", actor: "support", typing: true } });
    expect(widget.root.querySelector<HTMLElement>(".typing")!.hidden).toBe(false);
  });
});
