import copy
import unittest
from run_check import evaluate_suite, EXPECTED_CASES


class SuiteVerdictTests(unittest.TestCase):
    def fixture(self):
        return dict(schema=1, cases=24, resultPassed=24, strictPassed=24, errors=0, verdict="PASS",
                    results=[dict(name=n, result="PASS", strict="PASS") for n in sorted(EXPECTED_CASES)])

    def test_pass(self):
        self.assertEqual(evaluate_suite(self.fixture()), 0)

    def test_result_match_does_not_hide_strict_failure(self):
        r = self.fixture()
        r["results"][0]["strict"] = "FAIL"
        r.update(strictPassed=23, verdict="FAIL")
        self.assertEqual(evaluate_suite(r), 1)

    def test_execution_error(self):
        r = self.fixture()
        r["results"][0].update(result="ERROR", strict="ERROR")
        r.update(resultPassed=23, strictPassed=23, errors=1, verdict="ERROR")
        self.assertEqual(evaluate_suite(r), 2)

    def test_reject_incomplete_or_forged_totals(self):
        r = self.fixture()
        for field, value in [("results", r["results"][:-1]), ("strictPassed", 23), ("verdict", "UNKNOWN")]:
            altered = copy.deepcopy(r)
            altered[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                evaluate_suite(altered)


if __name__ == "__main__":
    unittest.main()
