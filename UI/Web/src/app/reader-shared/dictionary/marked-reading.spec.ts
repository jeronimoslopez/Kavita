import {markedReadingOf, MarkedReadingResult, RubySpan, toHiraganaServer} from './marked-reading';

/** The cases of Kavita.Services.Tests/Dictionary/MarkedReadingTests.cs, one for one with the same inputs and answers */

function of(text: string, s: number, e: number, lemma: string | null, ...ruby: RubySpan[]) {
  return markedReadingOf(text, s, e, ruby, lemma);
}

function result(printed: string, reading: string, dictionaryPrinted: string | null,
                dictionaryReading: string | null): MarkedReadingResult {
  return {printed, reading, dictionaryPrinted, dictionaryReading};
}

describe('markedReadingOf', () => {
  describe('cases from real books', () => {
    it('InflectedVerb_TakesTheLemmasTrailingKana', () => {
      expect(of('戸を開かない', 2, 4, '開く', [2, 3, 'あ'])).toEqual(result('あか', 'あか', 'あく', 'あく'));
    });

    it('JukujikunOverTwoKanji_IsTheWholeReading', () => {
      expect(of('小姐が', 0, 2, '小姐', [0, 2, 'ねえちゃん']))
        .toEqual(result('ねえちゃん', 'ねえちゃん', 'ねえちゃん', 'ねえちゃん'));
    });

    it('SurfaceThatIsTheLemma_GivesTheSameReadingTwice', () => {
      expect(of('言う', 0, 2, '言う', [0, 1, 'い'])).toEqual(result('いう', 'いう', 'いう', 'いう'));
    });

    it('GeminateStem_TakesTheLemmasEnding', () => {
      expect(of('言った', 0, 2, '言う', [0, 1, 'い'])).toEqual(result('いっ', 'いっ', 'いう', 'いう'));
    });

    it('KatakanaName_KeepsThePrintedFormBesideTheHiragana', () => {
      expect(of('高順は', 0, 2, '高順', [0, 2, 'ガオシュン']))
        .toEqual(result('ガオシュン', 'がおしゅん', 'ガオシュン', 'がおしゅん'));
    });

    it('IchidanStem_TakesTheLemmasWholeEnding', () => {
      expect(of('食べた', 0, 2, '食べる', [0, 1, 'た'])).toEqual(result('たべ', 'たべ', 'たべる', 'たべる'));
    });

    it('TokenOnlyPartlyCovered_IsNotMarked', () => {
      expect(of('漢字', 0, 2, '漢字', [0, 1, 'かん'])).toBeNull();
    });

    it('SpanCrossingTheTokensEnd_IsNotMarked', () => {
      expect(of('漢字', 0, 1, '漢', [0, 2, 'かんじ'])).toBeNull();
    });
  });

  it('SpanCrossingTheTokensStart_IsNotMarked', () => {
    expect(of('漢字', 1, 2, '字', [0, 2, 'かんじ'])).toBeNull();
  });

  it('OneSpanPerKanji_JoinInOrder', () => {
    const ruby: RubySpan[] = [[4, 5, 'じ'], [3, 4, 'かん']];

    expect(markedReadingOf('これは漢字だ', 3, 5, ruby, '漢字')).toEqual(result('かんじ', 'かんじ', 'かんじ', 'かんじ'));
  });

  it('KanaBetweenAndAroundSpans_IsReadAsWritten', () => {
    expect(of('お茶', 0, 2, 'お茶', [1, 2, 'ちゃ'])).toEqual(result('おちゃ', 'おちゃ', 'おちゃ', 'おちゃ'));
    expect(of('引き出し', 0, 4, '引き出す', [0, 1, 'ひ'], [2, 3, 'だ']))
      .toEqual(result('ひきだし', 'ひきだし', 'ひきだす', 'ひきだす'));
  });

  it('KatakanaInTheSurface_IsHiraganaInTheReading', () => {
    expect(of('馬鹿ヤロウ', 0, 5, '馬鹿ヤロウ', [0, 2, 'ばか']))
      .toEqual(result('ばかヤロウ', 'ばかやろう', 'ばかヤロウ', 'ばかやろう'));
  });

  it('StemThatDiffersFromTheLemmas_HasNoDictionaryForm', () => {
    expect(of('云った', 0, 2, '言う', [0, 1, 'い'])).toEqual(result('いっ', 'いっ', null, null));
  });

  it('NoLemma_HasNoDictionaryForm', () => {
    expect(of('開か', 0, 2, null, [0, 1, 'あ'])).toEqual(result('あか', 'あか', null, null));
    expect(of('開か', 0, 2, '', [0, 1, 'あ'])).toEqual(result('あか', 'あか', null, null));
  });

  it('KanaUnderASpan_AreStemAndNotTrailingKana', () => {
    // The span covers the kana, so the stem is さま, which isn't the lemma's stem
    expect(of('さま', 0, 2, '様', [0, 2, 'さま'])).toEqual(result('さま', 'さま', null, null));
  });

  it('TokenWithoutRuby_IsNotMarked', () => {
    expect(of('ひらがな', 0, 4, 'ひらがな')).toBeNull();
    expect(of('漢字とかな', 2, 5, 'とかな', [0, 2, 'かんじ'])).toBeNull();
  });

  it('EmphasisDots_CountAsNoSpan', () => {
    expect(of('それ', 0, 2, 'それ', [0, 1, '・'], [1, 2, '・'])).toBeNull();
    expect(of('それ', 0, 2, 'それ', [0, 2, '﹅﹅'])).toBeNull();
    // A dot over a neighbouring token doesn't stop this one being read
    expect(of('漢字それ', 0, 2, '漢字', [0, 2, 'かんじ'], [1, 3, '・'])?.reading).toBe('かんじ');
  });

  it('KanjiUnderANonKanaReading_IsNotMarked', () => {
    expect(of('漢字', 0, 2, '漢字', [0, 2, 'kanji'])).toBeNull();
  });

  it('IterationMark_IsAKanji', () => {
    expect(of('人々', 0, 2, '人々', [0, 2, 'ひとびと'])?.reading).toBe('ひとびと');
    expect(of('人々', 0, 2, '人々', [0, 1, 'ひと'])).toBeNull();
    expect(of('〆る', 0, 2, '〆る')).toBeNull();
  });

  it('SurrogatePairKanji_IsCoveredByItsSpan', () => {
    expect(of('𠮟った', 0, 3, '𠮟る', [0, 2, 'しか'])).toEqual(result('しかっ', 'しかっ', 'しかる', 'しかる'));
    expect(of('𠮟った', 0, 3, '𠮟る')).toBeNull();
    expect(of('𠮟った', 0, 3, '𠮟る', [2, 3, 'っ'])).toBeNull();
  });

  it('OverlappingSpans_AreNotMarked', () => {
    expect(of('明日', 0, 2, '明日', [0, 1, 'あ'], [0, 2, 'あした'])).toBeNull();
  });

  for (const [s, e] of [[-1, 1], [0, 3], [1, 1]]) {
    it(`TokenOutsideTheText_IsNotMarked(${s}, ${e})`, () => {
      expect(of('漢字', s, e, '漢字', [0, 2, 'かんじ'])).toBeNull();
    });
  }

  for (const [value, expected] of [
    ['カタカナ', 'かたかな'],
    ['ヴァイオリン', 'ゔぁいおりん'],
    ['コーヒー', 'こーひー'],
    ['ヽヾ', 'ゝゞ'],
    ['ひらがな', 'ひらがな'],
    ['ヷ', 'ヷ'],
  ]) {
    it(`ToHiragana_MapsKatakanaAndLeavesTheRest(${value})`, () => {
      expect(toHiraganaServer(value)).toBe(expected);
    });
  }
});
