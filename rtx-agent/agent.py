"""LocalCaption RTX agent (SPEC-18): runs Accent mode's speech models on this PC for the Mac app.

Holds no models until the Mac asks (`POST /load`), unloads them after an idle period, and
answers `POST /transcribe` with the primary and secondary model's text for one utterance.

    python agent.py [--port 8765] [--idle-minutes 15] [--fake]
"""
import argparse, hmac, json, logging, os, secrets, socket, subprocess, sys, threading, time
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

import numpy as np

import backends

VERSION = "1.0.0"
HERE = os.path.dirname(os.path.abspath(__file__))
MAX_AUDIO_S = 30
QUEUE_WAIT_S = 5          # a request that cannot start within this is answered 503
PAIR_FAILS, PAIR_LOCK_S = 5, 600
VRAM_MARGIN_MB = 500
log = logging.getLogger("agent")


class HTTPError(Exception):
    def __init__(self, status, message, **extra):
        super().__init__(message)
        self.status, self.body = status, {"error": message, **extra}


def _read_json(path, default):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return default


# ---------------------------------------------------------------- pairing

class Pairing:
    """One code per agent run; each paired Mac gets its own token, kept in state/tokens.json."""

    def __init__(self, state_dir):
        self.path = os.path.join(state_dir, "tokens.json")
        self.tokens = _read_json(self.path, {})
        self.code = f"{secrets.randbelow(10**6):06d}"
        self.fails, self.locked_until = 0, 0.0
        self.lock = threading.Lock()
        with open(os.path.join(state_dir, "pairing-code.txt"), "w") as f:
            f.write(self.code + "\n")

    @property
    def paired(self):
        return bool(self.tokens)

    def pair(self, code, name):
        with self.lock:
            if time.time() < self.locked_until:
                raise HTTPError(429, "too many wrong codes; try again later")
            if not hmac.compare_digest(str(code), self.code):
                self.fails += 1
                if self.fails >= PAIR_FAILS:
                    self.fails, self.locked_until = 0, time.time() + PAIR_LOCK_S
                raise HTTPError(403, "wrong pairing code")
            self.fails = 0
            token = secrets.token_urlsafe(32)
            self.tokens[token] = {"name": name or "Mac", "paired_at": time.strftime("%Y-%m-%dT%H:%M:%S")}
            self._save()
            return token

    def check(self, header):
        token = (header or "").removeprefix("Bearer ").strip()
        if not token or not any(hmac.compare_digest(token, t) for t in self.tokens):
            raise HTTPError(401, "not paired")
        return token

    def unpair(self, token):
        with self.lock:
            self.tokens.pop(token, None)
            self._save()

    def _save(self):
        tmp = self.path + ".tmp"
        with open(tmp, "w") as f:
            json.dump(self.tokens, f, indent=2)
        os.replace(tmp, self.path)


# ---------------------------------------------------------------- models

_gpu_cache = (0.0, ("", 0, 0))


def gpu_info(max_age=2.0):
    """(name, total MB, free MB) from nvidia-smi; ("", 0, 0) without an NVIDIA GPU. Cached for
    `max_age` s: /status is polled every second while models load."""
    global _gpu_cache
    if time.monotonic() - _gpu_cache[0] < max_age:
        return _gpu_cache[1]
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=name,memory.total,memory.free",
                              "--format=csv,noheader,nounits"], capture_output=True, text=True,
                             timeout=5, check=True).stdout.splitlines()[0]
        name, total, free = (x.strip() for x in out.split(","))
        info = name, int(total), int(free)
    except Exception:
        info = "", 0, 0
    _gpu_cache = (time.monotonic(), info)
    return info


class Models:
    """The loaded primary/secondary pair and the background work that changes it."""

    ROLES = ("primary", "secondary")
    LANES = ("interim", "final")

    def __init__(self, models_dir, fake=False):
        self.catalog = {e["id"]: e for e in _read_json(os.path.join(HERE, "catalog.json"), [])}
        self.models_dir, self.fake = models_dir, fake
        self.lock = threading.Lock()
        self.state, self.model, self.progress, self.message = "idle", None, 0.0, ""
        self.loaded = {"primary": None, "secondary": None}       # role -> catalog id
        self.target = None                                         # pair being loaded
        self.backends = {}                                         # role -> Backend
        self.pool = ThreadPoolExecutor(max_workers=4)
        # One utterance at a time per (lane, role): interim never waits behind a final, and the
        # Mac can ask for a final's primary and secondary in two concurrent requests, so the
        # caption shows as soon as the fast primary answers.
        self.lanes = {(lane, role): threading.Semaphore(1)
                      for lane in self.LANES for role in self.ROLES}

    def path(self, model_id):
        return os.path.join(self.models_dir, model_id)

    def describe(self):
        return [{"id": e["id"], "label": e["label"], "family": e["family"], "size_mb": e["size_mb"],
                 "vram_mb": e["vram_mb"], "tested": e["tested"],
                 "downloaded": self.fake or backends.is_downloaded(self.path(e["id"])),
                 "loaded": e["id"] in self.loaded.values()} for e in self.catalog.values()]

    def status(self):
        with self.lock:
            return {"state": self.state, "model": self.model, "progress": round(self.progress, 3),
                    "message": self.message, "loaded": dict(self.loaded)}

    def load(self, primary, secondary):
        for mid in filter(None, (primary, secondary)):
            if mid not in self.catalog:
                raise HTTPError(400, f"unknown model {mid}")
        if not primary:
            raise HTTPError(400, "primary model is required")
        want = {"primary": primary, "secondary": secondary or None}
        with self.lock:
            if self.state in ("downloading", "loading", "warming"):
                if self.target == want:
                    return
                raise HTTPError(409, "busy loading another pair; try again when it finishes")
            if self.state == "ready" and self.loaded == want:
                return
            if not self.fake:
                self._check_vram(want)
            self.target = want
            self.state, self.model, self.progress, self.message = "loading", primary, 0.0, ""
        threading.Thread(target=self._load, args=(want,), daemon=True).start()

    def _check_vram(self, want):
        _, total, free = gpu_info(max_age=0)
        if not total:
            return    # no nvidia-smi: let the load itself fail if it must
        freed = sum(self.catalog[m]["vram_mb"] for m in self.loaded.values() if m)
        need = sum(self.catalog[m]["vram_mb"] for m in want.values() if m)
        if need + VRAM_MARGIN_MB > free + freed:
            raise HTTPError(409, f"not enough GPU memory: need about {need} MB, {free + freed} MB free",
                            need_mb=need, free_mb=free + freed)

    def _set(self, **kw):
        with self.lock:
            for k, v in kw.items():
                setattr(self, k, v)

    def _load(self, want):
        try:
            self.unload(quiet=True)
            built = {}
            for role in self.ROLES:
                mid = want[role]
                if not mid:
                    continue
                entry, path = self.catalog[mid], self.path(mid)
                if not self.fake and not backends.is_downloaded(path):
                    self._set(state="downloading", model=mid, progress=0.0)
                    backends.download(entry, path, lambda f: self._set(progress=f))
                self._set(state="loading", model=mid, progress=0.0)
                t = time.perf_counter()
                built[role] = backends.load(entry, path, fake=self.fake)
                self._set(state="warming", model=mid)
                backends.warm(built[role])
                log.info("loaded %s as %s in %.1f s", mid, role, time.perf_counter() - t)
            with self.lock:
                self.backends, self.loaded = built, dict(want)
                self.state, self.model, self.progress, self.message = "ready", None, 1.0, ""
        except Exception as e:
            log.exception("load failed")
            self._set(state="error", progress=0.0, message=f"{type(e).__name__}: {e}")

    def unload(self, quiet=False):
        with self.lock:
            old, self.backends = self.backends, {}
            self.loaded = {"primary": None, "secondary": None}
            if not quiet:
                self.state, self.model, self.progress, self.message = "idle", None, 0.0, ""
        if old:
            import gc
            gc.collect()
            log.info("unloaded %d model(s)", len(old))

    def transcribe(self, audio, roles, lane):
        held = []
        try:
            deadline = time.monotonic() + QUEUE_WAIT_S
            for role in roles:      # fixed ROLES order, so two requests can't deadlock
                sem = self.lanes[(lane, role)]
                if not sem.acquire(timeout=max(0.0, deadline - time.monotonic())):
                    raise HTTPError(503, "busy")
                held.append(sem)
            with self.lock:
                if self.state != "ready":
                    raise HTTPError(409, f"models are not ready ({self.state})")
                picked = {r: self.backends[r] for r in roles if r in self.backends}
            if roles == ["primary"] and "primary" not in picked:
                raise HTTPError(409, "no primary model loaded")
            futures = {r: self.pool.submit(_timed, b, audio) for r, b in picked.items()}
            return {r: f.result() for r, f in futures.items()}
        finally:
            for sem in held:
                sem.release()


def _timed(backend, audio):
    t = time.perf_counter()
    text = backend.transcribe(audio)
    return {"text": text, "ms": round((time.perf_counter() - t) * 1000)}


# ---------------------------------------------------------------- HTTP

class Agent:
    def __init__(self, args):
        self.state_dir = args.state_dir
        os.makedirs(self.state_dir, exist_ok=True)
        self.pairing = Pairing(self.state_dir)
        self.models = Models(args.models_dir, fake=args.fake)
        self.idle_s = args.idle_minutes * 60
        self.last_activity = time.time()
        self.name = socket.gethostname()
        self.gpu, self.vram_total, _ = ("Fake GPU", 24000, 24000) if args.fake else gpu_info()

    def touch(self):
        self.last_activity = time.time()

    def idle_loop(self):
        while True:
            time.sleep(min(30, max(0.1, self.idle_s / 4)))
            if self.models.status()["state"] == "ready" and time.time() - self.last_activity > self.idle_s:
                log.info("idle for %d min: unloading", self.idle_s // 60)
                self.models.unload()


def make_handler(agent):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"     # keep-alive: the Mac reuses one connection

        def log_message(self, fmt, *a):
            log.debug("%s " + fmt, self.address_string(), *a)

        def do_GET(self):
            self._dispatch("GET")

        def do_POST(self):
            self._dispatch("POST")

        def do_DELETE(self):
            self._dispatch("DELETE")

        def _dispatch(self, method):
            url = urlparse(self.path)
            route = ROUTES.get((method, url.path))
            try:
                if not route:
                    raise HTTPError(404, "no such endpoint")
                fn, needs_auth = route
                token = agent.pairing.check(self.headers.get("Authorization")) if needs_auth else None
                status, body = fn(self, parse_qs(url.query), token)
            except HTTPError as e:
                status, body = e.status, e.body
            except Exception as e:
                log.exception("request failed")
                status, body = 500, {"error": f"{type(e).__name__}: {e}"}
            data = json.dumps(body).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def body(self, limit):
            n = int(self.headers.get("Content-Length") or 0)
            if n > limit:
                self.rfile.read(n)    # drain so the kept-alive connection stays usable
                raise HTTPError(413, f"body over {limit} bytes")
            return self.rfile.read(n)

        def json_body(self):
            try:
                return json.loads(self.body(64 * 1024) or b"{}")
            except json.JSONDecodeError:
                raise HTTPError(400, "body is not JSON")

    def hello(h, q, _):
        return 200, {"name": agent.name, "version": VERSION, "gpu": agent.gpu,
                     "vram_total_mb": agent.vram_total, "paired": agent.pairing.paired}

    def pair(h, q, _):
        b = h.json_body()
        return 200, {"token": agent.pairing.pair(b.get("code", ""), b.get("name", "")), "name": agent.name}

    def unpair(h, q, token):
        agent.pairing.unpair(token)
        return 200, {}

    def models(h, q, _):
        return 200, {"models": agent.models.describe()}

    def load(h, q, _):
        b = h.json_body()
        agent.touch()
        agent.models.load(b.get("primary"), b.get("secondary"))
        return 202, agent.models.status()

    def status(h, q, _):
        agent.touch()       # the Mac polls while Accent mode is selected: keeps models loaded
        _, _, free = (0, 0, 0) if agent.models.fake else gpu_info()
        return 200, {**agent.models.status(), "vram_used_mb": max(0, agent.vram_total - free)}

    def unload(h, q, _):
        agent.models.unload()
        return 200, agent.models.status()

    def transcribe(h, q, _):
        asked = {r for r in q.get("roles", ["primary"])[0].split(",") if r}
        lane = q.get("lane", ["final"])[0]
        if not asked or not asked <= set(Models.ROLES) or lane not in Models.LANES:
            raise HTTPError(400, "roles: primary and/or secondary; lane: interim or final")
        roles = [r for r in Models.ROLES if r in asked]
        raw = h.body(MAX_AUDIO_S * backends.SR * 2)
        if len(raw) % 2:
            raise HTTPError(400, "body must be 16-bit PCM")
        audio = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0
        agent.touch()
        return 200, {"id": q.get("id", [""])[0], **agent.models.transcribe(audio, roles, lane)}

    ROUTES = {
        ("GET", "/hello"): (hello, False),
        ("POST", "/pair"): (pair, False),
        ("DELETE", "/pair"): (unpair, True),
        ("GET", "/models"): (models, True),
        ("POST", "/load"): (load, True),
        ("GET", "/status"): (status, True),
        ("POST", "/unload"): (unload, True),
        ("POST", "/transcribe"): (transcribe, True),
    }
    return Handler


# ---------------------------------------------------------------- discovery & startup

def lan_addresses():
    addrs = {a[4][0] for a in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)}
    return sorted(a for a in addrs if not a.startswith("127."))


def advertise(agent, port):
    """Bonjour/mDNS so the Mac can list this PC without typing an address. Optional."""
    try:
        from zeroconf import ServiceInfo, Zeroconf
    except ImportError:
        log.warning("zeroconf not installed: not advertising on the LAN")
        return None
    zc = Zeroconf()
    info = ServiceInfo("_localcaption._tcp.local.", f"{agent.name}._localcaption._tcp.local.",
                       parsed_addresses=lan_addresses(), port=port,
                       properties={"version": VERSION, "gpu": agent.gpu}, server=f"{agent.name}.local.")
    zc.register_service(info)
    return zc


def show_pairing_code(agent):
    log.info("pairing code: %s", agent.pairing.code)
    if sys.stdout:     # None under pythonw (the scheduled task)
        print(f"LocalCaption RTX agent on {agent.name} — pairing code {agent.pairing.code}", flush=True)
    if sys.platform == "win32" and not agent.pairing.paired:
        import ctypes
        threading.Thread(daemon=True, target=lambda: ctypes.windll.user32.MessageBoxW(
            None, f"Pairing code: {agent.pairing.code}\n\nEnter it on your Mac in LocalCaption → "
                  "Settings → Accent mode.", "LocalCaption RTX agent", 0x40040)).start()


def build(argv=None):
    """Parse arguments and create the agent and its (not yet serving) HTTP server."""
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--idle-minutes", type=float, default=15)
    ap.add_argument("--state-dir", default=os.path.join(HERE, "state"))
    ap.add_argument("--models-dir", default=os.path.join(HERE, "models"))
    ap.add_argument("--fake", action="store_true", help="fake models, for tests")
    ap.add_argument("--no-advertise", action="store_true")
    args = ap.parse_args(argv)
    agent = Agent(args)
    server = ThreadingHTTPServer((args.host, args.port), make_handler(agent))
    server.daemon_threads = True
    threading.Thread(target=agent.idle_loop, daemon=True).start()
    return args, agent, server


def main(argv=None):
    args, agent, server = build(argv)
    handlers = [logging.FileHandler(os.path.join(agent.state_dir, "agent.log"))]
    if sys.stderr:
        handlers.append(logging.StreamHandler())
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=handlers)
    zc = None if args.no_advertise else advertise(agent, server.server_address[1])
    show_pairing_code(agent)
    log.info("listening on %s:%d (gpu: %s)", args.host, server.server_address[1], agent.gpu or "none")
    try:
        server.serve_forever()
    finally:
        if zc:
            zc.close()


if __name__ == "__main__":
    main()
