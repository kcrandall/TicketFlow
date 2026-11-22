import { ChatClient, ChatError, type ChatClientOptions } from "./client";
import type { ArticleHit, Attachment } from "./protocol";
import { TypingNotifier } from "./state";

const STYLE = `
:host { all: initial; font-family: var(--tf-font, system-ui, sans-serif); color: var(--tf-text, #1f2328); }
.launcher { position: fixed; right: 20px; bottom: 20px; border: 0; border-radius: 999px; padding: 12px 18px; cursor: pointer;
  background: var(--tf-accent, #2f6feb); color: var(--tf-accent-text, #fff); font: inherit; box-shadow: 0 4px 14px rgba(0,0,0,.2); }
.panel { position: fixed; right: 20px; bottom: 76px; width: min(360px, calc(100vw - 40px)); height: min(520px, calc(100vh - 120px));
  display: flex; flex-direction: column; background: var(--tf-surface, #fff); border-radius: 12px; overflow: hidden;
  box-shadow: 0 8px 30px rgba(0,0,0,.25); }
.panel[hidden] { display: none; }
header { padding: 12px 14px; background: var(--tf-accent, #2f6feb); color: var(--tf-accent-text, #fff); }
header small { display: block; opacity: .85; }
.body { flex: 1; overflow-y: auto; padding: 12px; display: flex; flex-direction: column; gap: 8px; }
.bubble { max-width: 80%; padding: 8px 10px; border-radius: 10px; white-space: pre-wrap; word-wrap: break-word; }
.incoming { align-self: flex-end; background: var(--tf-accent, #2f6feb); color: var(--tf-accent-text, #fff); }
.outgoing { align-self: flex-start; background: var(--tf-muted, #eef1f4); }
.notice, .typing { font-size: 12px; opacity: .75; }
form { display: flex; flex-direction: column; gap: 6px; padding: 10px; border-top: 1px solid var(--tf-border, #d0d7de); }
form.composer { flex-direction: row; align-items: center; }
input, textarea, button { font: inherit; }
input[type=text], input[type=email], textarea { flex: 1; padding: 8px; border: 1px solid var(--tf-border, #d0d7de); border-radius: 8px; }
.articles a { display: block; padding: 6px 0; color: var(--tf-accent, #2f6feb); }
`;

export interface MountOptions extends ChatClientOptions {
  /** Element to attach the shadow root to (default: a new element appended to document.body). */
  container?: HTMLElement;
  title?: string;
  /** Builds article links for deflection results (default: none, titles only). */
  articleUrl?: (hit: ArticleHit) => string;
  /** Pre-built client (tests). */
  client?: ChatClient;
}

/** The mounted widget. */
export class ChatWidget {
  readonly root: ShadowRoot;
  readonly client: ChatClient;
  private readonly body: HTMLElement;
  private readonly panel: HTMLElement;
  private readonly typingLine: HTMLElement;
  private readonly composer: HTMLFormElement;
  private readonly typing: TypingNotifier;
  private pending: Attachment[] = [];
  private searched = false;

  constructor(private readonly options: MountOptions) {
    const host = options.container ?? document.body.appendChild(document.createElement("div"));
    this.root = host.attachShadow({ mode: "open" });
    this.client = options.client ?? new ChatClient(options);
    this.root.innerHTML = `<style>${STYLE}</style>
      <button class="launcher" part="launcher" aria-expanded="false">Chat</button>
      <section class="panel" part="panel" hidden role="dialog" aria-label="Chat">
        <header><strong class="title"></strong><small class="status"></small></header>
        <div class="body" aria-live="polite"></div>
        <div class="typing" hidden>Support is typing…</div>
        <form class="composer" hidden>
          <input type="file" class="file" hidden>
          <button type="button" class="attach" aria-label="Attach a file" hidden>📎</button>
          <textarea rows="1" class="text" placeholder="Write a message" aria-label="Message"></textarea>
          <button type="submit">Send</button>
        </form>
      </section>`;
    this.panel = this.$(".panel");
    this.body = this.$(".body");
    this.typingLine = this.$(".typing");
    this.composer = this.$<HTMLFormElement>(".composer");
    this.$(".title").textContent = options.title ?? "Chat with us";
    this.typing = new TypingNotifier((typing) => void this.client.typing(typing));

    const launcher = this.$<HTMLButtonElement>(".launcher");
    launcher.addEventListener("click", () => {
      this.panel.hidden = !this.panel.hidden;
      launcher.setAttribute("aria-expanded", String(!this.panel.hidden));
      if (!this.panel.hidden) void this.client.markRead();
    });
    const text = this.$<HTMLTextAreaElement>(".text");
    text.addEventListener("input", () => this.typing.keystroke());
    this.composer.addEventListener("submit", (event) => {
      event.preventDefault();
      void this.sendCurrent(text);
    });
    const file = this.$<HTMLInputElement>(".file");
    this.$(".attach").addEventListener("click", () => file.click());
    file.addEventListener("change", () => void this.attach(file));

    this.client.subscribe((event) => {
      switch (event.type) {
        case "connected":
          this.renderState();
          break;
        case "messages":
          this.renderMessages();
          if (!this.panel.hidden) void this.client.markRead();
          break;
        case "typing":
          this.typingLine.hidden = !event.typing;
          break;
        case "ended":
          this.notice("This chat has ended.");
          this.composer.hidden = true;
          break;
        case "error":
          this.notice("Connection problem, retrying…");
          break;
        case "read":
          break;
      }
    });
  }

  async start(): Promise<void> {
    try {
      await this.client.start();
    } catch (error) {
      this.notice(error instanceof ChatError && error.code === "origin-not-allowed" ? "Chat is not available on this site." : "Chat is unavailable right now.");
    }
  }

  private renderState(): void {
    const widget = this.client.widget;
    this.$(".status").textContent = widget?.online === false ? widget.awayMessage ?? "We are away" : "We typically reply in a few minutes";
    this.$(".attach").hidden = !widget?.attachments;
    const needsPrechat = !this.client.prechatAnswered && (widget?.prechat.some((field) => field.required) ?? false);
    this.root.querySelector(".prechat")?.remove();
    if (needsPrechat && widget) {
      this.composer.hidden = true;
      const form = document.createElement("form");
      form.className = "prechat";
      for (const field of widget.prechat) {
        const input = document.createElement("input");
        input.type = field.kind === "email" ? "email" : "text";
        input.name = field.key;
        input.placeholder = field.label + (field.required ? " *" : "");
        input.required = field.required;
        input.maxLength = field.maxLength;
        input.setAttribute("aria-label", field.label);
        form.append(input);
      }
      const submit = document.createElement("button");
      submit.type = "submit";
      submit.textContent = "Start chat";
      form.append(submit);
      form.addEventListener("submit", (event) => {
        event.preventDefault();
        const answers: Record<string, string> = {};
        for (const input of Array.from(form.querySelectorAll("input"))) if (input.value.trim()) answers[input.name] = input.value.trim();
        void this.client.submitPrechat(answers).then(() => {
          form.remove();
          this.composer.hidden = false;
        }, () => this.notice("Please check the form."));
      });
      this.panel.insertBefore(form, this.typingLine);
    } else {
      this.composer.hidden = false;
    }
    if (widget?.greeting && this.body.childElementCount === 0) this.notice(widget.greeting);
    this.renderMessages();
  }

  private renderMessages(): void {
    const conversation = this.client.conversation;
    for (const element of Array.from(this.body.querySelectorAll(".bubble"))) element.remove();
    if (!conversation) return;
    for (const message of this.client.transcript.messages(conversation)) {
      const bubble = document.createElement("div");
      bubble.className = `bubble ${message.direction}`;
      bubble.textContent = message.body; // Never innerHTML: bodies are untrusted.
      for (const attachment of message.attachments) {
        const line = document.createElement("div");
        line.className = "notice";
        line.textContent = `📎 ${attachment.fileName}`;
        bubble.append(line);
      }
      this.body.append(bubble);
    }
    this.body.scrollTop = this.body.scrollHeight;
  }

  private async sendCurrent(input: HTMLTextAreaElement): Promise<void> {
    const text = input.value.trim();
    if (!text && this.pending.length === 0) return;
    if (!this.searched && !this.client.conversation && this.client.widget?.deflection && text) {
      this.searched = true;
      const hits = await this.client.search(text).catch(() => []);
      if (hits.length > 0) {
        this.renderArticles(hits);
        return; // The visitor can read the suggestions or press Send again to reach an agent.
      }
    }
    input.value = "";
    this.typing.stop();
    const attachments = this.pending;
    this.pending = [];
    try {
      await this.client.send(text, attachments);
    } catch (error) {
      input.value = text;
      this.notice(error instanceof ChatError && error.code === "prechat-required" ? "Please fill in the form first." : "Your message could not be sent.");
    }
  }

  private renderArticles(hits: ArticleHit[]): void {
    const box = document.createElement("div");
    box.className = "articles notice";
    box.append("These articles might help:");
    for (const hit of hits) {
      const link = document.createElement(this.options.articleUrl ? "a" : "span");
      link.textContent = hit.title;
      if (link instanceof HTMLAnchorElement) {
        link.href = this.options.articleUrl!(hit);
        link.target = "_blank";
        link.rel = "noopener";
      }
      box.append(link);
    }
    this.body.append(box);
  }

  private async attach(input: HTMLInputElement): Promise<void> {
    const file = input.files?.[0];
    input.value = "";
    if (!file) return;
    try {
      this.pending.push(await this.client.upload(file));
      this.notice(`Attached ${file.name}`);
    } catch {
      this.notice("The file could not be attached.");
    }
  }

  private notice(text: string): void {
    const line = document.createElement("div");
    line.className = "notice";
    line.textContent = text;
    this.body.append(line);
  }

  private $<T extends HTMLElement = HTMLElement>(selector: string): T {
    const element = this.root.querySelector<T>(selector);
    if (!element) throw new Error(`Missing ${selector}`);
    return element;
  }
}
