import {isCurrentPageLoad} from './page-load';

describe('isCurrentPageLoad', () => {
  it('keeps the response for the page the reader is on', () => {
    expect(isCurrentPageLoad({chapterId: 7, page: 11}, 7, 11)).toBe(true);
  });

  it('drops a response for a page the reader has paged past', () => {
    expect(isCurrentPageLoad({chapterId: 7, page: 10}, 7, 11)).toBe(false);
  });

  it('drops a response for the same page number of another chapter', () => {
    expect(isCurrentPageLoad({chapterId: 7, page: 0}, 8, 0)).toBe(false);
  });
});
