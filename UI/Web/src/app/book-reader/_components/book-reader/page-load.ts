/** The chapter and page a page request was made for */
export interface PageLoad {
  chapterId: number;
  page: number;
}

/**
 * A page request is still wanted when the reader is on the chapter and page it asked for. Requests aren't cancelled
 * when the reader moves on, and their responses can arrive in any order, so a response that fails this is for a page
 * the reader has left
 */
export function isCurrentPageLoad(load: PageLoad, chapterId: number, page: number): boolean {
  return load.chapterId === chapterId && load.page === page;
}
