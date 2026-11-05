"""Проверки условий выпуска независимых продуктов."""

from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]


def job_body(workflow, job):
    text = (ROOT / ".github" / "workflows" / workflow).read_text(encoding="utf-8")
    return re.search(rf"^  {job}:\n(.*?)(?=^  [\w-]+:|\Z)", text, re.M | re.S).group(1)


def enabled(workflow, job, event, tag="", prerelease=False, action=None):
    condition = re.search(r"^    if: (.+)$", job_body(workflow, job), re.M).group(1)
    values = {
        "github.event_name": event,
        "github.event.release.prerelease": prerelease,
        "github.event.release.tag_name": tag,
        "github.event.action": action or ("published" if prerelease else "released"),
        "inputs.tag": tag,
    }
    for name, value in values.items():
        condition = condition.replace(name, repr(value))
    condition = condition.replace("&&", " and ").replace("||", " or ")
    condition = re.sub(r"\btrue\b", "True", condition)
    condition = re.sub(r"\bfalse\b", "False", condition)
    return eval(condition, {"__builtins__": {}, "startsWith": str.startswith})


class ReleaseChannelsTests(unittest.TestCase):
    def test_python_only_builds_stable_v1(self):
        for tag, prerelease, expected in (("v1.25", False, True), ("v1.25", True, False),
                                          ("v2.0.9", False, False), ("v2.0.9", True, False)):
            with self.subTest(tag=tag, prerelease=prerelease):
                self.assertEqual(expected, enabled("release.yml", "package", "release", tag, prerelease))


if __name__ == "__main__":
    unittest.main()
