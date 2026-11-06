# FC2 — Synthetic 100-machine inventory acceptance

The SQL acceptance runner now inserts 100 synthetic machines into its **disposable** V2 database using a single set-based statement. The site may already contain fixture machines, so the test measures the existing count and asserts exactly 100 additional machines rather than assuming a clean table.

The test uses the actual `SqlProtectedMachineInventoryReader`, an authorized site grant, and the shared G2 security gate. It asserts complete, unique and site-scoped results, no cross-site leakage and identical results from eight concurrent authorized reads. It prints single-read and eight-reader wall-clock elapsed times with the observed machine count. No factory database, adapter or production configuration is used.

**Interpretation:** CI elapsed times are diagnostic only. The runner does not yet collect SQL logical reads, lock waits, p95/p99 distributions, ingestion load, memory or CPU, so this is not a 100-machine throughput certification. The test must remain deterministic and must not fail based on hosted-runner timing variance.

Next: instrument total security authority SQL commands and gate waits; run controlled repeatable 100-machine load on a fixed developer SQL environment; build G3 trusted activation facts separately before active roster or dashboard publication.
