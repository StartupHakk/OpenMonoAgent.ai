import unittest

from slug import slugify


class TestSlugify(unittest.TestCase):
    def test_basic(self):
        self.assertEqual(slugify("Hello World"), "hello-world")

    def test_trims(self):
        self.assertEqual(slugify("  --Hi!!  "), "hi")


if __name__ == "__main__":
    unittest.main()
