// Live conversation island (progressive enhancement, no framework): connects to the TicketFlow SignalR agent hub with the
// SignalR JSON protocol over a WebSocket, subscribes to the open conversation, re-renders the timeline when it changes
// (refetching the server-rendered page, so masking and authorization stay server-side) and shows typing indicators.
// Without it the page still works: reload to see new messages.
// Loaded on every page; (re)initializes after enhanced navigation.
let tfLiveSocket = null;
function tfLiveInit() {
  const root = document.getElementById('tf-conversation');
  if (tfLiveSocket && (!root || root.dataset.conversation !== tfLiveSocket.conversation)) { tfLiveSocket.stop(); tfLiveSocket = null; }
  if (!root || !root.dataset.hub || !('WebSocket' in window) || tfLiveSocket) return;
  const conversation = root.dataset.conversation;
  const users = JSON.parse(root.dataset.users || '{}');
  const RS = '\u001e';
  let socket, invocation = 0, typingTimer, lastTyping = 0, refreshTimer, reconnectTimer, refreshController = null, retry = 1000, stopped = false;

  const send = (message) => socket && socket.readyState === 1 && socket.send(JSON.stringify(message) + RS);
  const invoke = (target, ...args) => send({ type: 1, invocationId: String(++invocation), target, arguments: args });

  async function refresh() {
    refreshController?.abort();
    const controller = new AbortController();
    refreshController = controller;
    try {
      const response = await fetch(location.href, { headers: { 'Accept': 'text/html' }, credentials: 'same-origin', signal: controller.signal });
      if (!response.ok || controller.signal.aborted || stopped) return;
      const html = new DOMParser().parseFromString(await response.text(), 'text/html');
      const next = html.getElementById('tf-timeline');
      const current = document.getElementById('tf-timeline');
      if (next && current) {
        const atBottom = current.getBoundingClientRect().bottom <= window.innerHeight + 80;
        current.innerHTML = next.innerHTML;
        if (atBottom) current.lastElementChild?.scrollIntoView({ block: 'end' });
      }
      const revision = html.querySelector('#tf-composer input[name="_revision"]');
      document.querySelectorAll('form input[name="_revision"]').forEach((input) => { if (revision) input.value = revision.value; });
    } catch { /* offline or aborted: the next event retries */ }
    finally { if (refreshController === controller) refreshController = null; }
  }

  function showTyping(data) {
    const element = document.getElementById('tf-typing');
    if (!element) return;
    const who = data.actor === 'visitor' ? 'Customer' : (users[data.agent] || 'A teammate');
    element.textContent = data.typing ? `${who} is typing…` : '';
    clearTimeout(element._clear);
    if (data.typing) element._clear = setTimeout(() => { element.textContent = ''; }, 6000);
  }

  function onMessage(raw) {
    for (const frame of raw.split(RS)) {
      if (!frame) continue;
      const message = JSON.parse(frame);
      if (message.type === 1 && message.target === 'tf.event') {
        const event = message.arguments[0] || {};
        if (event.type === 'typing') showTyping(event.data || {});
        else if (event.type === 'conversation.changed' || event.type === 'message' || event.type === 'replylock.changed' || event.type === 'presence.changed') {
          clearTimeout(refreshTimer);
          refreshTimer = setTimeout(refresh, 250);
        }
      } else if (message.type === 7) socket.close();
    }
  }

  function connect() {
    const url = new URL(root.dataset.hub, location.href);
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
    url.searchParams.set('workspace', root.dataset.workspace);
    socket = new WebSocket(url);
    let handshaken = false;
    socket.onopen = () => socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
    socket.onmessage = (event) => {
      if (!handshaken) { handshaken = true; retry = 1000; invoke('Subscribe', 'conversation', conversation); return; }
      onMessage(event.data);
    };
    socket.onclose = () => { if (!stopped) { clearTimeout(reconnectTimer); reconnectTimer = setTimeout(connect, retry); retry = Math.min(retry * 2, 30000); } };
  }

  const ping = setInterval(() => send({ type: 6 }), 15000);
  tfLiveSocket = {
    conversation,
    stop: () => {
      stopped = true;
      clearInterval(ping);
      clearTimeout(reconnectTimer);
      clearTimeout(refreshTimer);
      clearTimeout(typingTimer);
      refreshController?.abort();
      socket?.close();
    },
  };
  const composer = document.querySelector('#tf-composer textarea[name="body"]');
  if (composer) {
    composer.addEventListener('input', () => {
      const now = Date.now();
      if (now - lastTyping > 2000) { lastTyping = now; invoke('Typing', conversation, true); }
      clearTimeout(typingTimer);
      typingTimer = setTimeout(() => { lastTyping = 0; invoke('Typing', conversation, false); }, 4000);
    });
    composer.form?.addEventListener('submit', () => invoke('Typing', conversation, false));
  }
  connect();
}
document.addEventListener('DOMContentLoaded', tfLiveInit);
if (document.readyState !== 'loading') tfLiveInit();
window.addEventListener('load', () => window.Blazor?.addEventListener?.('enhancedload', tfLiveInit));
