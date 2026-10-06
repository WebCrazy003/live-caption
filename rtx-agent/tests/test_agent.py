"""Agent API tests with fake models: `python -m unittest discover -s tests` (no GPU needed)."""
import json, os, sys, tempfile, threading, time, unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import numpy as np
import agent as agent_mod


class AgentTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        _, self.agent, self.server = agent_mod.build(
            ["--fake", "--port", "0", "--host", "127.0.0.1", "--idle-minutes", "0.01",
             "--state-dir", self.tmp.name, "--models-dir", self.tmp.name])
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.base = f"http://127.0.0.1:{self.server.server_address[1]}"
        self.token = None

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.tmp.cleanup()

    def call(self, method, path, body=None, raw=None, token=True):
        data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
        req = Request(self.base + path, data=data, method=method)
        if token and self.token:
            req.add_header("Authorization", f"Bearer {self.token}")
        try:
            with urlopen(req, timeout=10) as r:
                return r.status, json.loads(r.read())
        except HTTPError as e:
            with e:
                return e.code, json.loads(e.read())

    def pair(self):
        status, body = self.call("POST", "/pair", {"code": self.agent.pairing.code, "name": "test"})
        self.assertEqual(status, 200)
        self.token = body["token"]

    def load_and_wait(self, primary="parakeet-tdt-0.6b-v2", secondary="whisper-large-v3"):
        status, _ = self.call("POST", "/load", {"primary": primary, "secondary": secondary})
        self.assertEqual(status, 202)
        for _ in range(100):
            _, st = self.call("GET", "/status")
            if st["state"] in ("ready", "error"):
                return st
            time.sleep(0.05)
        self.fail("load never finished")

    def pcm(self, seconds):
        return (np.zeros(int(16000 * seconds), dtype="<i2")).tobytes()

    def test_hello_needs_no_token_and_reports_unpaired(self):
        status, body = self.call("GET", "/hello", token=False)
        self.assertEqual(status, 200)
        self.assertFalse(body["paired"])
        self.assertEqual(body["gpu"], "Fake GPU")

    def test_everything_else_needs_a_token(self):
        for method, path in [("GET", "/models"), ("GET", "/status"), ("POST", "/load"),
                             ("POST", "/unload"), ("POST", "/transcribe"), ("DELETE", "/pair")]:
            self.assertEqual(self.call(method, path, token=False)[0], 401, path)

    def test_pairing_wrong_code_then_lockout(self):
        wrong = "000000" if self.agent.pairing.code != "000000" else "111111"
        for _ in range(4):
            self.assertEqual(self.call("POST", "/pair", {"code": wrong})[0], 403)
        self.assertEqual(self.call("POST", "/pair", {"code": wrong})[0], 403)    # 5th trips the lock
        self.assertEqual(self.call("POST", "/pair", {"code": self.agent.pairing.code})[0], 429)

    def test_pair_persists_and_unpair_revokes(self):
        self.pair()
        self.assertTrue(self.call("GET", "/hello")[1]["paired"])
        with open(os.path.join(self.tmp.name, "tokens.json")) as f:
            saved = json.load(f)
        self.assertIn(self.token, saved)
        self.assertEqual(self.call("DELETE", "/pair")[0], 200)
        self.assertEqual(self.call("GET", "/status")[0], 401)

    def test_catalog_lists_defaults(self):
        self.pair()
        ids = {m["id"]: m for m in self.call("GET", "/models")[1]["models"]}
        self.assertTrue(ids["parakeet-tdt-0.6b-v2"]["tested"])
        self.assertTrue(ids["whisper-large-v3"]["tested"])

    def test_transcribe_before_load_is_409(self):
        self.pair()
        self.assertEqual(self.call("POST", "/transcribe?roles=primary", raw=self.pcm(1))[0], 409)

    def test_load_then_transcribe_both_roles(self):
        self.pair()
        st = self.load_and_wait()
        self.assertEqual(st["state"], "ready")
        self.assertEqual(st["loaded"], {"primary": "parakeet-tdt-0.6b-v2", "secondary": "whisper-large-v3"})
        status, body = self.call("POST", "/transcribe?roles=primary,secondary&id=7", raw=self.pcm(2))
        self.assertEqual(status, 200)
        self.assertEqual(body["id"], "7")
        self.assertEqual(body["primary"]["text"], "parakeet-tdt-0.6b-v2 heard 32000 samples")
        self.assertEqual(body["secondary"]["text"], "whisper-large-v3 heard 32000 samples")
        status, body = self.call("POST", "/transcribe?roles=primary", raw=self.pcm(1))
        self.assertNotIn("secondary", body)

    def test_split_final_and_interim_lane(self):
        self.pair()
        self.load_and_wait()
        body = self.call("POST", "/transcribe?roles=secondary&id=3", raw=self.pcm(1))[1]
        self.assertEqual(list(body), ["id", "secondary"])
        self.assertEqual(self.call("POST", "/transcribe?roles=primary&lane=interim", raw=self.pcm(1))[0], 200)
        self.assertEqual(self.call("POST", "/transcribe?lane=sideways", raw=self.pcm(1))[0], 400)

    def test_word_timings_on_request(self):
        self.pair()
        self.load_and_wait()
        body = self.call("POST", "/transcribe?roles=primary&lane=interim&words=1", raw=self.pcm(2))[1]
        words = body["primary"]["words"]
        self.assertEqual([w["word"] for w in words], body["primary"]["text"].split())
        self.assertTrue(all(w["start"] < w["end"] <= 2.0 for w in words))
        plain = self.call("POST", "/transcribe?roles=primary", raw=self.pcm(1))[1]
        self.assertNotIn("words", plain["primary"])

    def test_concurrent_primary_and_secondary_requests(self):
        self.pair()
        self.load_and_wait()
        results = []
        threads = [threading.Thread(target=lambda r=r: results.append(
            self.call("POST", f"/transcribe?roles={r}", raw=self.pcm(1))[0])) for r in ("primary", "secondary")]
        for t in threads: t.start()
        for t in threads: t.join()
        self.assertEqual(results, [200, 200])

    def test_secondary_none(self):
        self.pair()
        st = self.load_and_wait(secondary=None)
        self.assertEqual(st["loaded"]["secondary"], None)
        body = self.call("POST", "/transcribe?roles=primary,secondary", raw=self.pcm(1))[1]
        self.assertIn("primary", body)
        self.assertNotIn("secondary", body)

    def test_bad_requests(self):
        self.pair()
        self.load_and_wait()
        self.assertEqual(self.call("POST", "/load", {"primary": "nope"})[0], 400)
        self.assertEqual(self.call("POST", "/transcribe?roles=tertiary", raw=self.pcm(1))[0], 400)
        self.assertEqual(self.call("POST", "/transcribe", raw=b"\x00\x00\x00")[0], 400)
        self.assertEqual(self.call("POST", "/transcribe", raw=self.pcm(31))[0], 413)
        # The kept-alive server still answers after a rejected oversized body.
        self.assertEqual(self.call("POST", "/transcribe", raw=self.pcm(1))[0], 200)

    def test_reloading_same_pair_is_noop_and_unload_frees(self):
        self.pair()
        self.load_and_wait()
        before = self.agent.models.backends["primary"]
        self.load_and_wait()
        self.assertIs(self.agent.models.backends["primary"], before)
        st = self.call("POST", "/unload")[1]
        self.assertEqual(st["state"], "idle")
        self.assertEqual(self.agent.models.backends, {})

    def test_idle_unload(self):
        self.pair()
        self.load_and_wait()
        for _ in range(60):          # idle is 0.6 s; checked every 0.15 s
            if self.agent.models.status()["state"] == "idle":
                return
            time.sleep(0.1)
        self.fail("models were not unloaded when idle")


class JoinTokensTest(unittest.TestCase):
    def test_subwords_join_into_words_with_spans(self):
        import backends
        words = backends._join_tokens([" Yeah", ",", " I", " w", "atch"], [0.48, 0.64, 0.8, 1.2, 1.36], 2.0)
        self.assertEqual([w["word"] for w in words], ["Yeah,", "I", "watch"])
        self.assertEqual([(w["start"], w["end"]) for w in words], [(0.48, 0.8), (0.8, 1.2), (1.2, 1.44)])


if __name__ == "__main__":
    unittest.main()
