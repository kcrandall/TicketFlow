// Wire shapes of the TicketFlow visitor hub (TicketFlow.Realtime.SignalR). Versioned: events carry "v": 1.

export interface HubResult<T> {
  ok: boolean;
  error?: string | null;
  data?: T | null;
}

export interface PrechatField {
  key: string;
  label: string;
  kind: "name" | "email" | "text";
  required: boolean;
  maxLength: number;
}

export interface WidgetState {
  themeRef?: string | null;
  greeting?: string | null;
  online: boolean;
  awayMessage?: string | null;
  deflection: boolean;
  requireVerified: boolean;
  attachments?: { maxBytes: number; types: string[] } | null;
  prechat: PrechatField[];
}

export interface ConversationSummary {
  conversation: string;
  closed: boolean;
  lastSequence: number;
  readSequence: number;
  lastActivityAt: string;
}

export interface ConnectData {
  token?: string | null;
  visitorId: string;
  verified: boolean;
  prechatAnswered: boolean;
  widget?: WidgetState | null;
  conversations: ConversationSummary[];
}

export interface Attachment {
  storageRef: string;
  fileName: string;
  mediaType: string;
  sizeBytes: number;
}

export interface ChatMessage {
  conversation: string;
  message: string;
  sequence: number;
  direction: "incoming" | "outgoing";
  body: string;
  contentType: string;
  attachments: Attachment[];
  at: string;
}

export interface MessagePage {
  conversation: string;
  readSequence: number;
  next?: number | null;
  messages: Omit<ChatMessage, "conversation">[];
}

export interface SendResult {
  conversation: string;
  message: string;
  sequence: number;
  started: boolean;
  replayed: boolean;
}

export interface ArticleHit {
  article: string;
  title: string;
  slug: string;
  snippet: string;
  locale: string;
}

export interface WireEvent {
  v: number;
  type: "message" | "typing" | "read" | "session.ended" | string;
  topic: string;
  data: Record<string, unknown>;
}

export const EVENT_METHOD = "tf.event";
