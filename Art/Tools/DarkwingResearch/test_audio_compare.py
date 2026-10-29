import unittest
from compare_audio_port import transition_membership


class AudioComparisonTests(unittest.TestCase):
    def test_missing_reference_transition_is_not_counted(self):
        self.assertEqual(transition_membership([(1, 10), (2, 20)], [(1, 10)]), 1)

    def test_empty_reference(self):
        self.assertEqual(transition_membership([(1, 10)], []), 0)

    def test_membership_does_not_claim_order_or_multiplicity(self):
        self.assertEqual(transition_membership([(1, 10), (1, 10)], [(1, 10)]), 2)


if __name__ == "__main__":
    unittest.main()
