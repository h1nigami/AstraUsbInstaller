"""Проверки CI-публикации Station через настоящие HTTP-запросы к локальному сервису."""

import copy
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import tempfile
import threading
import unittest

from tools import publish_station_release as publisher


class StationPublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        names = [
            "bestcam-station-v2.0.10-linux-x64.tar.gz",
            "bestcam-station-v2.0.10-linux-arm64.tar.gz",
            "bestcam-station-v2.0.10-win-x64.zip",
            "bestcam-station_2.0.10_amd64.deb",
            "bestcam-station_2.0.10_arm64.deb",
            "BestCamStationSetup-v2.0.10.exe",
        ]
        self.contents = {}
        for name in names:
            body = ("package: " + name).encode()
            self.contents[name] = body
            self.contents[name + ".sha256"] = (
                hashlib.sha256(body).hexdigest() + "  " + name + "\n"
            ).encode()
        for name, body in self.contents.items():
            (self.directory / name).write_bytes(body)
        self.release = {
            "tag_name": "v2.0.10", "draft": False, "prerelease": False,
            "assets": [{"name": name, "size": len(body),
                        "digest": "sha256:" + hashlib.sha256(body).hexdigest()}
                       for name, body in self.contents.items()],
        }
        self.stored = {}
        self.created = False
        self.stable = "2.0.9"
        self.pilot = None
        self.fail_upload = None
        self.fail_promote = False
        self.redirect_api = False
        self.advance_on_pilot = None
        self.early_conflict = False
        self.requests = []
        state = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def respond(self, code, body):
                self.send_response(code)
                self.end_headers()
                self.wfile.write(body if isinstance(body, bytes) else json.dumps(body).encode())

            def do_GET(self):
                state.requests.append(("GET", self.path, self.headers.get("Authorization")))
                if self.path.split("?")[0] == "/api/v1/catalog":
                    products = [{"slug": "bestcam", "version": "0.4.9"}]
                    if state.stable:
                        products.append({"slug": "bestcam-station", "version": state.stable})
                    return self.respond(200, {"products": products})
                prefix = "/bestcam-station/2.0.10/"
                name = self.path.removeprefix(prefix)
                if self.path.startswith(prefix) and (state.pilot == "2.0.10" or state.stable == "2.0.10") and name in state.stored:
                    return self.respond(200, state.stored[name])
                self.respond(404, {})

            def mutate(self):
                state.requests.append((self.command, self.path, self.headers.get("Authorization")))
                if self.headers.get("Authorization") != "Bearer station-only":
                    return self.respond(401, {})
                if state.early_conflict:
                    return self.respond(409, {})
                body = self.rfile.read(int(self.headers["Content-Length"]))
                if state.redirect_api:
                    self.send_response(302)
                    self.send_header("Location", "/redirect-target")
                    self.end_headers()
                    return
                if self.command == "POST" and self.path == "/api/v1/products/bestcam-station/releases":
                    if state.created:
                        return self.respond(409, {})
                    state.assertEqual({"version": "2.0.10"}, json.loads(body))
                    state.created = True
                    return self.respond(201, {})
                prefix = "/api/v1/products/bestcam-station/releases/2.0.10/files/"
                if self.path.startswith(prefix):
                    from urllib.parse import parse_qs, urlsplit
                    url = urlsplit(self.path)
                    name = url.path.removeprefix(prefix)
                    query = parse_qs(url.query)
                    state.assertEqual([hashlib.sha256(body).hexdigest()], query["sha256"])
                    state.assertEqual([str(len(body))], query["size"])
                    if name == state.fail_upload:
                        return self.respond(503, {})
                    if name in state.stored:
                        return self.respond(409, {})
                    state.stored[name] = body
                    return self.respond(201, {})
                if self.path in ("/api/v1/products/bestcam-station/channels/pilot",
                                 "/api/v1/products/bestcam-station/channels/stable"):
                    if state.fail_promote:
                        return self.respond(503, {})
                    channel = self.path.rsplit("/", 1)[1]
                    state.assertEqual({"version": "2.0.10"}, json.loads(body))
                    setattr(state, channel, "2.0.10")
                    if channel == "pilot" and state.advance_on_pilot:
                        state.stable = state.advance_on_pilot
                    return self.respond(200, {})
                self.respond(404, {})

            do_POST = mutate
            do_PUT = mutate

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.stop_server)
        self.base = f"http://127.0.0.1:{self.server.server_port}"

    def stop_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def publish(self, **kwargs):
        return publisher.publish(self.release, self.directory, self.base, "station-only", **kwargs)

    def mutations(self):
        return [r for r in self.requests if r[0] != "GET"]

    def test_complete_release_is_verified_before_stable(self):
        self.assertEqual("published", self.publish())
        self.assertEqual(self.contents, self.stored)
        self.assertEqual("2.0.10", self.stable)
        self.assertEqual("/api/v1/products/bestcam-station/channels/stable", self.requests[-1][1])
        self.assertTrue(all(r[2] is None for r in self.requests if r[0] == "GET"))
        self.assertTrue(all(r[1].startswith("/api/v1/products/bestcam-station/") for r in self.mutations()))

    def test_resume_upload_keeps_existing_files_and_completes_release(self):
        self.created = True
        name = "bestcam-station_2.0.10_amd64.deb"
        self.stored[name] = self.contents[name]
        self.assertEqual("published", self.publish())
        self.assertEqual(self.contents, self.stored)

    def test_early_conflict_for_large_file_is_resumable(self):
        self.early_conflict = True
        path = self.directory / "large.tmp"
        with path.open("wb") as stream:
            stream.write(b"large existing file" * 2_000_000)
        publisher.mutate(self.base, "station-only", "PUT",
                         "/releases/2.0.10/files/BestCamStationSetup-v2.0.10.exe",
                         file=path, duplicate_ok=True)
        self.assertEqual(1, len(self.requests))
        self.assertEqual({}, self.stored)

    def test_first_station_release_preserves_existing_product(self):
        self.stable = None
        self.assertEqual("published", self.publish())
        self.assertEqual("2.0.10", self.stable)
        self.assertTrue(all("/bestcam/" not in r[1] for r in self.mutations()))

    def test_token_is_not_forwarded_on_api_redirect(self):
        self.redirect_api = True
        with self.assertRaises(RuntimeError):
            self.publish()
        self.assertFalse(any(r[1] == "/redirect-target" for r in self.requests))
        self.assertEqual("2.0.9", self.stable)
        with self.assertRaises(RuntimeError):
            publisher.mutate(self.base, "station-only", "PUT",
                             "/releases/2.0.10/files/BestCamStationSetup-v2.0.10.exe",
                             file=self.directory / "BestCamStationSetup-v2.0.10.exe")
        self.assertFalse(any(r[1] == "/redirect-target" for r in self.requests))

    def test_conflicting_existing_file_blocks_stable(self):
        self.created = True
        self.stored["BestCamStationSetup-v2.0.10.exe"] = b"different existing file"
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual("2.0.9", self.stable)

    def test_failed_upload_keeps_previous_channels(self):
        self.fail_upload = "bestcam-station_2.0.10_amd64.deb"
        with self.assertRaises(Exception):
            self.publish()
        self.assertEqual("2.0.9", self.stable)
        self.assertIsNone(self.pilot)

    def test_failed_promotion_is_reported(self):
        self.fail_promote = True
        with self.assertRaises(Exception):
            self.publish()
        self.assertEqual("2.0.9", self.stable)

    def test_repeat_current_version_verifies_without_mutations(self):
        self.stable = "2.0.10"
        self.stored.update(self.contents)
        self.assertEqual("unchanged", self.publish())
        self.assertEqual([], self.mutations())

    def test_repeat_current_version_still_detects_corruption(self):
        self.stable = "2.0.10"
        self.stored.update(self.contents)
        self.stored["BestCamStationSetup-v2.0.10.exe"] = b"corrupt"
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual([], self.mutations())

    def test_older_release_cannot_roll_back_stable(self):
        self.stable = "2.1.0"
        self.assertEqual("superseded", self.publish())
        self.assertEqual([], self.mutations())

    def test_newer_stable_during_upload_is_preserved(self):
        self.advance_on_pilot = "2.1.0"
        self.assertEqual("superseded", self.publish())
        self.assertEqual("2.1.0", self.stable)
        self.assertFalse(any(r[1].endswith("/channels/stable") for r in self.mutations()))

    def test_check_mode_does_not_create_unpublished_release(self):
        self.assertEqual("checked", self.publish(check_only=True))
        self.assertEqual([], self.mutations())

    def test_check_mode_verifies_current_publication_without_token(self):
        self.stable = "2.0.10"
        self.stored.update(self.contents)
        self.assertEqual("unchanged", publisher.publish(self.release, self.directory, self.base, "", check_only=True))
        self.assertEqual([], self.mutations())

    def test_missing_token_blocks_mutations(self):
        with self.assertRaises(ValueError):
            publisher.publish(self.release, self.directory, self.base, "")
        self.assertEqual([], self.mutations())

    def test_untrusted_release_metadata_is_rejected_before_http(self):
        variants = [
            {"tag_name": "v1.25"}, {"tag_name": "v20.0.1"},
            {"tag_name": "v2.0.10-rc.1"}, {"tag_name": "v2.0.10+other"},
            {"draft": True}, {"prerelease": True},
        ]
        for change in variants:
            with self.subTest(change=change), self.assertRaises(ValueError):
                publisher.publish(self.release | change, self.directory, self.base, "station-only")
        self.assertEqual([], self.requests)

    def test_missing_or_foreign_github_asset_is_rejected(self):
        bad = copy.deepcopy(self.release)
        bad["assets"] = bad["assets"][:-1]
        with self.assertRaises(ValueError):
            publisher.publish(bad, self.directory, self.base, "station-only")
        bad["assets"].append({"name": "astra-usb-monitor-v1.25.tar.gz", "size": 10})
        with self.assertRaises(ValueError):
            publisher.publish(bad, self.directory, self.base, "station-only")
        self.assertEqual([], self.requests)

    def test_corrupt_local_package_is_rejected(self):
        (self.directory / "BestCamStationSetup-v2.0.10.exe").write_bytes(b"bad")
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual([], self.requests)

    def test_github_digest_mismatch_blocks_publication(self):
        self.release["assets"][0]["digest"] = "sha256:" + "0" * 64
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual([], self.requests)

    def test_checksum_sidecar_must_name_the_correct_package(self):
        name = "BestCamStationSetup-v2.0.10.exe.sha256"
        body = (hashlib.sha256(self.contents[name[:-7]]).hexdigest() + "  foreign.exe\n").encode()
        (self.directory / name).write_bytes(body)
        for asset in self.release["assets"]:
            if asset["name"] == name:
                asset.update(size=len(body), digest="sha256:" + hashlib.sha256(body).hexdigest())
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual([], self.requests)


if __name__ == "__main__":
    unittest.main()
