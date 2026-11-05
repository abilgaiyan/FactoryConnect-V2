"""Exercise the real host; CI owns process startup and cleanup."""
import json
import time
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:5080"

def request(path, method="GET", expected=200):
    req = urllib.request.Request(BASE + path, method=method)
    try:
        response = urllib.request.urlopen(req, timeout=3)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        assert response.status == expected, (path, response.status)
        body = json.load(response)
        if expected == 503:
            assert response.headers.get_content_type() == "application/problem+json"
            assert body["status"] == 503
        return body

for attempt in range(60):
    try:
        request("/health/live")
        break
    except (OSError, AssertionError):
        if attempt == 59:
            raise
        time.sleep(0.5)

request("/health/ready", expected=503)
request("/health", expected=503)
config = request("/dashboard/config")
assert config["sources"] == []
assert config["reportingBasePath"] == "/"
assert config["requestTimeoutMilliseconds"] > 0
for path, method in [
    ("/api/machines/v1/00000000-0000-0000-0000-000000000001/current-state", "GET"),
    ("/api/machines/v1/00000000-0000-0000-0000-000000000001/activity/query", "POST"),
    ("/api/machines/v1/00000000-0000-0000-0000-000000000001/counter/query", "POST"),
    ("/api/commissioning/v1/snapshot", "GET"),
    *[(f"/api/reporting/v1/operational-metrics/{kind}/query", "POST")
      for kind in ("shifts", "production-days", "production-day-shifts")],
]:
    request(path, method, expected=503)
print("Backend foundation smoke checks passed")
