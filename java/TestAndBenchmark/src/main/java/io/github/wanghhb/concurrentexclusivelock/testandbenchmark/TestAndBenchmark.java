// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock.testandbenchmark;

/** Command-line semantic tests, stress tests, and performance comparisons. */
public final class TestAndBenchmark {
    private TestAndBenchmark() {
    }

    public static void main(String[] args) {
        try {
            CliOptions options = CliOptions.parse(args);
            if (options.help) {
                printHelp();
                return;
            }

            boolean ran = false;
            if (options.advancedCorrectness) {
                SemanticTests.runAdvanced(options);
                ran = true;
            }
            if (options.fullSemantics) {
                SemanticTests.runFull(options);
                ran = true;
            }
            if (options.pipelineSemantics) {
                SemanticTests.runPipelineSemantics(options);
                ran = true;
            }
            if (options.pipelineStress != null) {
                PipelineStress.run(options.pipelineStress, options);
                ran = true;
            }
            if (options.endurance != null) {
                StressRunners.runEndurance(options.endurance, options);
                ran = true;
            }
            if (options.contentionStress != null) {
                StressRunners.runContention(options.contentionStress, options);
                ran = true;
            }
            if (options.advancedPerf) {
                BenchmarkRunner.runAdvanced(options);
                ran = true;
            }
            if (!ran) {
                BenchmarkRunner.runStandard(options);
            }
        } catch (Throwable exception) {
            System.err.println("FAILED: " + exception);
            exception.printStackTrace(System.err);
            System.exit(1);
        }
    }

    private static void printHelp() {
        System.out.println("ConcurrentExclusiveLock Java - TestAndBenchmark");
        System.out.println();
        System.out.println("Usage:");
        System.out.println("  java -jar TestAndBenchmark.jar [mode] [options]");
        System.out.println();
        System.out.println("Semantic and stress modes:");
        System.out.println("  --advanced-correctness           Advanced upgrade/downgrade correctness tests");
        System.out.println("  --full-semantics                 Deterministic contracts plus random legal paths");
        System.out.println("  --pipeline-semantics             Fixed Pipeline transition tests");
        System.out.println("  --pipeline-stress <duration>     Random Pipeline stress test");
        System.out.println("  --endurance <duration>           Persistent-lock endurance test");
        System.out.println("  --contention-stress <duration>   Single-lock high-contention test");
        System.out.println();
        System.out.println("Performance modes:");
        System.out.println("  no mode                           Standard comparison: synchronized, ReentrantLock,");
        System.out.println("                                    ReentrantReadWriteLock, StampedLock, CEL,");
        System.out.println("                                    and CEL(ExclusiveOnly)");
        System.out.println("  --advanced-perf                  CEL advanced permission-path comparison");
        System.out.println();
        System.out.println("Common benchmark options:");
        System.out.println("  --lock-instances <n>             Independent lock + Work instances (default 1)");
        System.out.println("  --threads <n>                    Dedicated threads per lock instance");
        System.out.println("  --operations <n>                 Operations per thread");
        System.out.println("  --workload <type>                cpu | memory | dictionary | ledger | payload");
        System.out.println("  --work <n>                       Read and write business steps");
        System.out.println("  --read-work <n>                  Read business steps");
        System.out.println("  --write-work <n>                 Write business steps");
        System.out.println("  --memory-mb <n>                  Memory MiB per lock instance");
        System.out.println("  --dictionary-size <n>            Data size per lock instance");
        System.out.println();
        System.out.println("Semantic options:");
        System.out.println("  --semantic-workers <n>           Dedicated workers per lock (minimum 2)");
        System.out.println("  --semantic-operations <n>        Paths or Pipeline rounds per lock");
        System.out.println("  --semantic-seed <n>              Reproducible random seed");
        System.out.println("  --advanced-operations <n>        Operations for advanced correctness");
        System.out.println("  --advanced-seed <n>              Advanced correctness seed");
        System.out.println();
        System.out.println("Duration formats:");
        System.out.println("  30s  10m  24h  1d  hh:mm:ss");
        System.out.println();
        System.out.println("Examples:");
        System.out.println("  java -jar TestAndBenchmark.jar --full-semantics --lock-instances 8 --semantic-workers 4 --semantic-operations 256");
        System.out.println("  java -jar TestAndBenchmark.jar --pipeline-stress 10m --lock-instances 8 --semantic-workers 64 --semantic-operations 1000");
        System.out.println("  java -jar TestAndBenchmark.jar --lock-instances 1 --threads 16 --workload cpu --operations 100000 --work 0");
    }
}
