# Third-party components

The core `concurrent-exclusive-lock` crate has no third-party runtime dependency.
The `cel-test-and-benchmark` program vendors the following crates solely for
benchmark comparison and their transitive synchronization support:

| Component | Version | License | Vendored path |
|---|---:|---|---|
| parking_lot | 0.12.5 | MIT OR Apache-2.0 | `parking_lot-vendor.zip -> vendor/parking_lot` |
| parking_lot_core | 0.9.12 | MIT OR Apache-2.0 | `vendor/parking_lot/core` |
| lock_api | 0.4.14 | MIT OR Apache-2.0 | `vendor/parking_lot/lock_api` |
| cfg-if | 1.0.4 | MIT OR Apache-2.0 | `parking_lot-vendor.zip -> vendor/cfg-if` |
| libc | 0.2.189 | MIT OR Apache-2.0 | `parking_lot-vendor.zip -> vendor/libc` |
| smallvec | 1.15.2 | MIT OR Apache-2.0 | `parking_lot-vendor.zip -> vendor/smallvec` |
| scopeguard | 1.2.0 | MIT OR Apache-2.0 | `parking_lot-vendor.zip -> vendor/scopeguard` |
| windows-link | 0.2.1 | MIT OR Apache-2.0 | `parking_lot-vendor.zip -> vendor/windows-link` |

The original license files are retained inside each vendored source directory.
Compatibility-only manifest/build-script adjustments are documented in
压缩包内的 `vendor/VENDOR_NOTES.md`。No synchronization algorithm source in parking_lot,
parking_lot_core, or lock_api was modified.
