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
    def test_station_builds_once_per_release_and_supports_promotion(self):
        for job in ("verify", "build", "windows-setup"):
            for prerelease, action, expected in (
                (False, "published", False), (False, "released", True),
                (True, "published", True), (True, "released", False),
            ):
                with self.subTest(job=job, prerelease=prerelease, action=action):
                    self.assertEqual(expected, enabled("release-station.yml", job, "release", "v2.0.10", prerelease, action))

    def test_download_publisher_accepts_only_stable_station_releases(self):
        for event, tag, prerelease, action, expected in (
            ("release", "v2.0.10", False, "released", True),
            ("release", "v2.0.10", False, "published", False),
            ("release", "v2.0.10", True, "published", False),
            ("release", "v1.25", False, "released", False),
            ("release", "v20.0.1", False, "released", False),
            ("workflow_dispatch", "v2.0.10", False, "", False),
            ("push", "", False, "", False),
            ("pull_request", "", False, "", False),
        ):
            with self.subTest(event=event, tag=tag, action=action):
                self.assertEqual(expected, enabled("release-station.yml", "publish-download", event, tag, prerelease, action))

    def test_station_accepts_stable_and_prerelease_v2(self):
        for prerelease in (False, True):
            for job in ("verify", "build", "windows-setup"):
                with self.subTest(job=job, prerelease=prerelease):
                    self.assertTrue(enabled("release-station.yml", job, "release", "v2.0.9", prerelease))

    def test_station_rejects_other_product_for_release_and_manual_run(self):
        for event in ("release", "workflow_dispatch"):
            for tag in ("v1.25", "v10.0", "other"):
                for prerelease in (False, True):
                    for job in ("verify", "build", "windows-setup"):
                        with self.subTest(event=event, tag=tag, job=job):
                            self.assertFalse(enabled("release-station.yml", job, event, tag, prerelease))

    def test_manual_station_build_keeps_tag_guard(self):
        for job in ("verify", "build", "windows-setup"):
            self.assertTrue(enabled("release-station.yml", job, "workflow_dispatch", "v2.0.9"))

    def test_source_checks_do_not_publish(self):
        for event in ("push", "pull_request"):
            self.assertTrue(enabled("release-station.yml", "verify", event))
            for job in ("build", "windows-setup"):
                self.assertFalse(enabled("release-station.yml", job, event))

    def test_python_only_builds_stable_v1(self):
        for tag, prerelease, expected in (("v1.25", False, True), ("v1.25", True, False),
                                          ("v2.0.9", False, False), ("v2.0.9", True, False)):
            with self.subTest(tag=tag, prerelease=prerelease):
                self.assertEqual(expected, enabled("release.yml", "package", "release", tag, prerelease))

    def test_station_upload_actions_explicitly_preserve_latest(self):
        for job in ("build", "windows-setup"):
            body = job_body("release-station.yml", job)
            upload = re.search(r"uses: softprops/action-gh-release@[^\n]+\n(.*?)(?=^      - |\Z)", body, re.M | re.S)
            value = re.search(r"^          make_latest: [\"']?(false)[\"']?\s*$", upload.group(1), re.M)
            self.assertIsNotNone(value, f"{job}: загрузка вложений может переключить latest")


if __name__ == "__main__":
    unittest.main()
