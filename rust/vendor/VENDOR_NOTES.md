# Vendored benchmark dependencies

The benchmark includes a local, offline copy of `parking_lot 0.12.5` and its
required dependencies so that the comparison can be built without network
access.

The following compatibility-only manifest adjustments were made:

- unused optional `deadlock_detection`, `serde`, and `owning_ref` registry
  dependencies were removed from the vendored manifests;
- the Redox-only registry dependency was omitted because this release targets
  Linux and Windows;
- `windows-link 0.2.1` is vendored for Windows builds;
- the `parking_lot_core` build-script `rustc-check-cfg` emission was removed
  because Cargo 1.75 predates that directive;
- `libc 0.2.189` is allowed to carry newer lint names when compiled by Rust 1.75.

No synchronization implementation source was changed.
