import { act } from "react";
import { JSDOM } from "jsdom";

const GLOBAL_NAMES = [
  "window",
  "document",
  "navigator",
  "HTMLElement",
  "HTMLInputElement",
  "Event",
  "MouseEvent",
  "PopStateEvent",
  "IS_REACT_ACT_ENVIRONMENT",
];

export async function mountInDom(element, initialUrl = "http://factory-dashboard/") {
  const previous = new Map(GLOBAL_NAMES.map((name) => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  const dom = new JSDOM("<!doctype html><html><body><div id=\"root\"></div></body></html>", { url: initialUrl });
  const { window } = dom;

  install("window", window);
  install("document", window.document);
  install("navigator", window.navigator);
  install("HTMLElement", window.HTMLElement);
  install("HTMLInputElement", window.HTMLInputElement);
  install("Event", window.Event);
  install("MouseEvent", window.MouseEvent);
  install("PopStateEvent", window.PopStateEvent);
  install("IS_REACT_ACT_ENVIRONMENT", true);

  const { createRoot } = await import("react-dom/client");
  const container = window.document.getElementById("root");
  const root = createRoot(container);
  await act(async () => root.render(element));

  return {
    window,
    document: window.document,
    container,
    async click(element) {
      await act(async () => {
        element.dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
      });
    },
    async changeInput(input, value) {
      await act(async () => {
        const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value").set;
        setter.call(input, value);
        input.dispatchEvent(new window.Event("input", { bubbles: true, composed: true }));
        input.dispatchEvent(new window.Event("change", { bubbles: true, composed: true }));
      });
    },
    async submit(form) {
      await act(async () => {
        form.dispatchEvent(new window.Event("submit", { bubbles: true, cancelable: true }));
      });
    },
    async popstate(path) {
      window.history.pushState(null, "", path);
      await act(async () => {
        window.dispatchEvent(new window.PopStateEvent("popstate"));
      });
    },
    async dispose() {
      await act(async () => root.unmount());
      dom.window.close();
      for (const name of GLOBAL_NAMES) {
        const descriptor = previous.get(name);
        if (descriptor) {
          Object.defineProperty(globalThis, name, descriptor);
        } else {
          delete globalThis[name];
        }
      }
    },
  };
}

function install(name, value) {
  Object.defineProperty(globalThis, name, {
    configurable: true,
    writable: true,
    value,
  });
}
