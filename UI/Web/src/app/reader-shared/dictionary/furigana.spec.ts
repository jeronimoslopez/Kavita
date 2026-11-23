import {furigana_segments, toHiragana} from './furigana';

describe('toHiragana', () => {
  it('turns katakana into hiragana and leaves the rest alone', () => {
    expect(toHiragana('カタカナ')).toBe('かたかな');
    expect(toHiragana('ヴァ漢字ーa')).toBe('ゔぁ漢字ーa');
  });

  it('keeps the long vowel mark as it is', () => {
    expect(toHiragana('ラーメン')).toBe('らーめん');
  });
});

describe('furigana_segments edge cases', () => {
  it('matches the long vowel mark in the surface against the reading', () => {
    expect(furigana_segments('コーヒー豆', 'こーひーまめ')).toEqual([
      {text: 'コーヒー', ruby: null},
      {text: '豆', ruby: 'まめ'},
    ]);
  });

  it('falls back to one ruby when the reading is shorter than the surface kana', () => {
    expect(furigana_segments('食べる', 'たべ')).toEqual([{text: '食べる', ruby: 'たべ'}]);
    expect(furigana_segments('お茶', 'お')).toEqual([{text: 'お茶', ruby: 'お'}]);
  });
});

describe('furigana_segments', () => {
  it('puts one ruby over an all-kanji surface', () => {
    expect(furigana_segments('懸念', 'けねん')).toEqual([{ text: '懸念', ruby: 'けねん' }]);
  });

  it('keeps trailing okurigana outside the ruby', () => {
    expect(furigana_segments('食べる', 'たべる')).toEqual([
      { text: '食', ruby: 'た' },
      { text: 'べる', ruby: null },
    ]);
  });

  it('splits a leading kana prefix', () => {
    expect(furigana_segments('お知らせ', 'おしらせ')).toEqual([
      { text: 'お', ruby: null },
      { text: '知', ruby: 'し' },
      { text: 'らせ', ruby: null },
    ]);
  });

  it('aligns kana inside the surface', () => {
    expect(furigana_segments('引き出す', 'ひきだす')).toEqual([
      { text: '引', ruby: 'ひ' },
      { text: 'き', ruby: null },
      { text: '出', ruby: 'だ' },
      { text: 'す', ruby: null },
    ]);
    expect(furigana_segments('聞き取り', 'ききとり')).toEqual([
      { text: '聞', ruby: 'き' },
      { text: 'き', ruby: null },
      { text: '取', ruby: 'と' },
      { text: 'り', ruby: null },
    ]);
  });

  it('compares katakana in the surface against the hiragana reading', () => {
    expect(furigana_segments('ガス管', 'がすかん')).toEqual([
      { text: 'ガス', ruby: null },
      { text: '管', ruby: 'かん' },
    ]);
  });

  it('treats ヶ and 々 as needing a reading', () => {
    expect(furigana_segments('一ヶ月', 'いっかげつ')).toEqual([
      { text: '一ヶ月', ruby: 'いっかげつ' },
    ]);
    expect(furigana_segments('人々', 'ひとびと')).toEqual([{ text: '人々', ruby: 'ひとびと' }]);
  });

  it('falls back to one ruby when the kana do not line up', () => {
    expect(furigana_segments('行う', 'おこなわ')).toEqual([{ text: '行う', ruby: 'おこなわ' }]);
    expect(furigana_segments('引き出す', 'ひだす')).toEqual([
      { text: '引き出す', ruby: 'ひだす' },
    ]);
  });

  it('leaves kana-only or unread surfaces alone', () => {
    expect(furigana_segments('ある', 'ある')).toEqual([{ text: 'ある', ruby: null }]);
    expect(furigana_segments('会見', null)).toEqual([{ text: '会見', ruby: null }]);
  });

  it('keeps characters outside the basic plane whole', () => {
    expect(furigana_segments('𠮟る', 'しかる')).toEqual([
      { text: '𠮟', ruby: 'しか' },
      { text: 'る', ruby: null },
    ]);
  });
});
