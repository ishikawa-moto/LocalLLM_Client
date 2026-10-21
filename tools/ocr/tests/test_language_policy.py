"""Public synthetic decoder regressions; no documents, service or network access.

Run using the already provisioned offline OCR Python (RapidOCR/numpy pinned).
"""
import importlib.util
from pathlib import Path
import unittest
import numpy as np
from rapidocr.ch_ppocr_rec.utils import CTCLabelDecode

spec = importlib.util.spec_from_file_location('ocr_worker_language', Path(__file__).parents[1] / 'local_ocr_worker.py')
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)

class LanguagePolicyTests(unittest.TestCase):
    def decoder(self):
        return w.JapaneseDecode(CTCLabelDecode(character=list('日本語漢字国学ABC123汉语这한글ㄱㄴ')))

    def predictions(self, decoder, chars):
        preds = np.zeros((1, len(chars), len(decoder.character)), dtype=np.float32)
        for i, char in enumerate(chars):
            preds[0, i, decoder.character.index(char)] = .96
            preds[0, i, 0] = .01
        return preds

    def test_japanese_kanji_kana_ascii(self):
        self.assertTrue(all(w.target_character(c) for c in '日本語漢字国学かなカナABC123 .,:/＠１２３'))

    def test_hangul_syllables_and_jamo_excluded(self):
        self.assertFalse(any(w.target_character(c) for c in '한국어한글ㄱㄴ가힣'))

    def test_chinese_only_forms_excluded(self):
        self.assertFalse(any(w.target_character(c) for c in '汉语这们'))
        self.assertFalse(any(w.target_character(c) for c in 'ㄅㄆ'))

    def test_shared_han_preserved(self):
        self.assertTrue(all(w.target_character(c) for c in '漢字国学中文'))

    def test_good_output_and_score_unchanged(self):
        decoder = self.decoder()
        preds = self.predictions(decoder, '日本語ABC123')
        expected = decoder.decoder(preds)
        self.assertEqual(decoder(preds), expected)
        self.assertEqual(decoder.excluded_steps, 0)

    def test_foreign_winner_not_replaced_by_weaker_japanese(self):
        decoder = self.decoder()
        preds = self.predictions(decoder, '日汉한本')
        preds[0, 1:3, decoder.character.index('字')] = .90
        original = preds.copy()
        lines, words = decoder(preds)
        self.assertEqual(lines[0][0], '日本' + w.EXCLUDED_MARKER)
        self.assertEqual(decoder.excluded_steps, 2)
        self.assertEqual(words, [])
        np.testing.assert_equal(preds, original)

    def test_low_foreign_runnerup_does_not_add_marker(self):
        decoder = self.decoder()
        preds = self.predictions(decoder, '日本')
        preds[0, :, decoder.character.index('汉')] = .5
        self.assertEqual(decoder(preds)[0][0][0], '日本')
        self.assertEqual(decoder.excluded_steps, 0)

    def test_tied_foreign_runnerup_preserves_original_allowed_winner(self):
        decoder = self.decoder()
        preds = self.predictions(decoder, '日本')
        preds[0, :, decoder.character.index('汉')] = .96
        self.assertEqual(decoder(preds)[0][0][0], '日本')
        self.assertEqual(decoder.excluded_steps, 0)

    def test_foreign_gap_keeps_ctc_repeats_separate(self):
        decoder = self.decoder()
        self.assertEqual(decoder(self.predictions(decoder, '日汉日'))[0][0][0], '日日' + w.EXCLUDED_MARKER)

    def test_all_excluded_has_explicit_marker(self):
        decoder = self.decoder()
        self.assertEqual(decoder(self.predictions(decoder, '汉한글'))[0][0][0], w.EXCLUDED_MARKER)

    def test_batch_markers_only_on_affected_line(self):
        decoder = self.decoder()
        preds = np.concatenate([self.predictions(decoder, '日本'), self.predictions(decoder, '한글')])
        lines, _ = decoder(preds)
        self.assertEqual([line[0] for line in lines], ['日本', w.EXCLUDED_MARKER])

    def test_vocab_drift_and_word_boxes_fail_closed(self):
        decoder = self.decoder()
        with self.assertRaises(w.OcrError): decoder(np.zeros((1, 2, 3)))
        with self.assertRaises(w.OcrError): decoder(self.predictions(decoder, '日'), return_word_box=True)

if __name__ == '__main__': unittest.main(verbosity=2)
