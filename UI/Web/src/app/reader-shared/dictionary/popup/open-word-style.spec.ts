import {installOpenWordStyle, OPEN_WORD_CSS} from './open-word-style';

describe('installOpenWordStyle', () => {
  const style = () => document.getElementById('kavita-dict-open-style');

  it('adds the rule once and removes it after the last remover', () => {
    const first = installOpenWordStyle(document);
    const second = installOpenWordStyle(document);
    expect(document.querySelectorAll('#kavita-dict-open-style')).toHaveLength(1);
    expect(style()?.textContent).toBe(OPEN_WORD_CSS);
    expect(OPEN_WORD_CSS).toContain('::highlight(kavita-dict-open)');

    first();
    first();
    expect(style()).not.toBeNull();
    second();
    expect(style()).toBeNull();
  });
});
