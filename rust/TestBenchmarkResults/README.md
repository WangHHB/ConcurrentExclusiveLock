# TestBenchmarkResults

`final/` contains the authoritative validation outputs for this package.

- `benchmarks/*.log`: raw benchmark output for each configuration;
- `benchmarks/all_results.csv`: all parsed benchmark rows;
- `benchmarks/all_results.json`: JSON form of all rows;
- `benchmarks/single_16t_w64_median.csv`: median/min/max for the repeated main configuration;
- `pipeline-smoke-30s.log`: short Pipeline stress and progress validation;
- `pipeline-stress-30m.log`: formal long Pipeline stress;
- `full-semantics.log`: deterministic and randomized semantic regression;
- `pipeline-semantics.log`: deterministic Pipeline contract regression;
- `contention-stress-60s.log`: Exclusive progress and isolation stress;
- `final-validation.console.log`: final validation-chain summary;
- `cargo-*.log`: format, Clippy, check, build, test, metadata, and core crate packaging output;
- `clean-empty-cargo-home-build.log`: clean offline Release build with an empty Cargo cache;
- `endurance-60s.log`: final Endurance run;
- `linux-artifact-inspection.log`: executable format, dynamic dependencies, and help output;
- `windows-cross-check.log`: exact reason a Windows executable was not produced in this Linux container.

The benchmark executable asserts final state-hash equality across every compared strategy in each scenario.
