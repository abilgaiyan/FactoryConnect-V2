export interface NavigationClick {
  readonly defaultPrevented: boolean;
  readonly button: number;
  readonly metaKey: boolean;
  readonly ctrlKey: boolean;
  readonly shiftKey: boolean;
  readonly altKey: boolean;
}

export interface NavigationAnchor {
  readonly href: string;
  readonly target: string;
  readonly hasDownload: boolean;
}

export function shouldHandleApplicationNavigation(
  click: NavigationClick,
  anchor: NavigationAnchor,
  currentOrigin: string,
): boolean {
  if (
    click.defaultPrevented ||
    click.button !== 0 ||
    click.metaKey ||
    click.ctrlKey ||
    click.shiftKey ||
    click.altKey
  ) {
    return false;
  }

  if (anchor.hasDownload) {
    return false;
  }

  if (anchor.target !== "" && anchor.target !== "_self") {
    return false;
  }

  const target = new URL(anchor.href);
  return target.origin === currentOrigin;
}
