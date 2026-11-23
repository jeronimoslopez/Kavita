import {ComponentRef} from '@angular/core';

/**
 * Puts a highlight component where a server-rendered `<app-epub-highlight>` stands. The element's own child nodes
 * move into the component instead of a copy of its text, so ruby inside a highlight keeps its rt, and the block text
 * the dictionary reads stays the length the server stored
 */
export function mountHighlight<T>(highlight: Element, create: (content: Node[]) => ComponentRef<T>): ComponentRef<T> {
  const componentRef = create(Array.from(highlight.childNodes));
  highlight.parentNode?.replaceChild(componentRef.location.nativeElement, highlight);
  return componentRef;
}
