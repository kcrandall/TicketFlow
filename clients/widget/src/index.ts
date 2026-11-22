import { ChatWidget, type MountOptions } from "./ui";

export { ChatClient, ChatError } from "./client";
export { ChatWidget } from "./ui";
export { Transcript, TypingNotifier, tokenStore } from "./state";

/** Mounts the widget and connects. */
export function mount(options: MountOptions): ChatWidget {
  const widget = new ChatWidget(options);
  void widget.start();
  return widget;
}
