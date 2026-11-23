import {Component, viewChild, ViewContainerRef} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {readBlockText} from '../reader-shared/dictionary/block-text';
import {mountHighlight} from './epub-highlight-mount';

/**
 * The real EpubHighlightComponent's template. The real component can't load in a spec, because its imports reach
 * ngx-color-picker, which fails to JIT compile under the test builder
 */
@Component({
  selector: 'app-epub-highlight',
  template: '<span class="epub-highlight clickable"><span class="epub-highlight" (click)="clicks = clicks + 1"><ng-content /></span></span>',
})
class StandInHighlightComponent {
  clicks = 0;
}

@Component({
  template: '<div #content></div>',
})
class HostComponent {
  readonly container = viewChild.required('content', {read: ViewContainerRef});
}

async function render(html: string) {
  const fixture = TestBed.createComponent(HostComponent);
  await fixture.whenStable();
  const page = fixture.nativeElement.querySelector('div') as HTMLElement;
  page.innerHTML = html;
  const refs = Array.from(page.querySelectorAll('app-epub-highlight')).map(highlight =>
    mountHighlight(highlight, content =>
      fixture.componentInstance.container().createComponent(StandInHighlightComponent, {projectableNodes: [content]})));
  await fixture.whenStable();
  return {page, refs};
}

describe('mountHighlight', () => {
  it('keeps a ruby inside a highlight, so the block text still leaves out its reading', async () => {
    const {page} = await render('<p data-kb="0">前<app-epub-highlight data-annotation-id="7"><ruby>漢<rt>かん</rt></ruby>字</app-epub-highlight>後</p>');
    const block = page.querySelector('p')!;
    expect(block.querySelector('app-epub-highlight span span ruby rt')?.textContent).toBe('かん');
    expect(readBlockText(block).text).toBe('前漢字後');
  });

  it('keeps the original text nodes of a plain highlight in place', async () => {
    const {page} = await render('<p>a <app-epub-highlight id="epub-highlight-7">b&amp;c</app-epub-highlight> d</p>');
    const paragraph = page.querySelector('p')!;
    const highlight = paragraph.querySelector('app-epub-highlight')!;
    expect(paragraph.textContent).toBe('a b&c d');
    expect(paragraph.childNodes.length).toBe(3);
    expect(paragraph.childNodes[1]).toBe(highlight);
    expect(highlight.querySelector('span.epub-highlight span.epub-highlight')?.textContent).toBe('b&c');
  });

  it('lets a click on projected ruby reach the highlight click handler', async () => {
    const {page, refs} = await render('<p><app-epub-highlight data-annotation-id="7"><ruby>漢<rt>かん</rt></ruby></app-epub-highlight></p>');
    (page.querySelector('ruby') as HTMLElement).click();
    expect(refs[0].instance.clicks).toBe(1);
  });
});
