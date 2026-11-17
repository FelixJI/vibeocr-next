interface PreviewSnapshot {
  readonly owner: symbol | null;
  readonly committed: HTMLImageElement | null;
  readonly failed: boolean;
}

let owner: symbol | null = null;
let wanted = "";
let committed: HTMLImageElement | null = null;
let failed = false;
let issued: HTMLImageElement | null = null;
const listeners = new Set<() => void>();
let snapshot: PreviewSnapshot = { owner, committed, failed };

function publish() {
  if (
    owner === snapshot.owner &&
    committed === snapshot.committed &&
    failed === snapshot.failed
  )
    return;
  snapshot = { owner, committed, failed };
  for (const listener of listeners) listener();
}

function issueNext() {
  if (
    issued ||
    !owner ||
    !wanted ||
    wanted === committed?.getAttribute("src") ||
    failed
  )
    return;

  const requestOwner = owner;
  const url = wanted;
  // This image outlives the inspection component until load/decode settles.
  const image = new Image();
  issued = image;
  const clearHandlers = () => {
    image.onload = null;
    image.onerror = null;
  };
  const settle = (ok: boolean) => {
    clearHandlers();
    issued = null;
    if (owner === requestOwner && wanted === url) {
      if (ok) committed = image;
      else failed = true;
    }
    issueNext();
    publish();
  };
  image.onload = () => {
    clearHandlers();
    if (typeof image.decode === "function")
      void image.decode().then(
        () => settle(true),
        () => settle(false),
      );
    else settle(true);
  };
  image.onerror = () => settle(false);
  image.src = url;
}

export const progressivePreviewDecoder = {
  subscribe(listener: () => void) {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
  getSnapshot: () => snapshot,
  attach(token: symbol, sheet: HTMLElement, alt: string) {
    if (owner !== token || !committed) return;
    // Resource responses are no-store. Reuse the image that actually decoded.
    const image = committed;
    image.alt = alt;
    image.draggable = false;
    sheet.prepend(image);
    return () => image.remove();
  },
  request(token: symbol, url: string) {
    if (token !== owner) {
      owner = token;
      committed = null;
      failed = false;
    }
    if (url !== wanted) failed = false;
    wanted = url;
    issueNext();
    publish();
  },
  release(token: symbol) {
    if (owner !== token) return;
    owner = null;
    wanted = "";
    committed = null;
    failed = false;
    publish();
  },
  retry(token: symbol) {
    if (owner !== token || !failed) return;
    failed = false;
    issueNext();
    publish();
  },
};
