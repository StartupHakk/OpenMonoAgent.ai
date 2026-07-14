import unittest

from cart import checkout_total


class TestPricing(unittest.TestCase):
    def test_no_discount(self):
        self.assertEqual(checkout_total([(1000, 2)], 0), 2000)

    def test_simple_discount(self):
        self.assertEqual(checkout_total([(1000, 1)], 10), 900)


if __name__ == "__main__":
    unittest.main()
