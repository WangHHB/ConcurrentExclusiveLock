// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock.testandbenchmark;

import java.time.Duration;

final class CliOptions {
    boolean help;
    boolean advancedCorrectness;
    boolean fullSemantics;
    boolean pipelineSemantics;
    boolean advancedPerf;
    Duration pipelineStress;
    Duration endurance;
    Duration contentionStress;

    int lockInstances = 1;
    int threads = Math.max(2, Runtime.getRuntime().availableProcessors());
    int operations = 10_000;
    String workload = "cpu";
    int work = 64;
    int readWork = -1;
    int writeWork = -1;
    int memoryMb = 16;
    int dictionarySize = 65_536;

    int semanticWorkers = 4;
    int semanticOperations = 256;
    long semanticSeed = System.nanoTime();
    int advancedOperations = 64;
    long advancedSeed = System.nanoTime() ^ 0x5deece66dL;

    static CliOptions parse(String[] args) {
        CliOptions options = new CliOptions();
        for (int index = 0; index < args.length; index++) {
            String arg = args[index];
            switch (arg) {
                case "--help", "-h" -> options.help = true;
                case "--advanced-correctness" -> options.advancedCorrectness = true;
                case "--full-semantics" -> options.fullSemantics = true;
                case "--pipeline-semantics" -> options.pipelineSemantics = true;
                case "--advanced-perf" -> options.advancedPerf = true;
                case "--pipeline-stress" -> options.pipelineStress = TestSupport.parseDuration(requireValue(args, ++index, arg));
                case "--endurance" -> options.endurance = TestSupport.parseDuration(requireValue(args, ++index, arg));
                case "--contention-stress" -> options.contentionStress = TestSupport.parseDuration(requireValue(args, ++index, arg));
                case "--lock-instances" -> options.lockInstances = positiveInt(requireValue(args, ++index, arg), arg);
                case "--threads" -> options.threads = positiveInt(requireValue(args, ++index, arg), arg);
                case "--operations" -> options.operations = positiveInt(requireValue(args, ++index, arg), arg);
                case "--workload" -> options.workload = requireValue(args, ++index, arg).toLowerCase(java.util.Locale.ROOT);
                case "--work" -> options.work = nonNegativeInt(requireValue(args, ++index, arg), arg);
                case "--read-work" -> options.readWork = nonNegativeInt(requireValue(args, ++index, arg), arg);
                case "--write-work" -> options.writeWork = nonNegativeInt(requireValue(args, ++index, arg), arg);
                case "--memory-mb" -> options.memoryMb = positiveInt(requireValue(args, ++index, arg), arg);
                case "--dictionary-size" -> options.dictionarySize = positiveInt(requireValue(args, ++index, arg), arg);
                case "--semantic-workers" -> options.semanticWorkers = Math.max(2, positiveInt(requireValue(args, ++index, arg), arg));
                case "--semantic-operations" -> options.semanticOperations = positiveInt(requireValue(args, ++index, arg), arg);
                case "--semantic-seed" -> options.semanticSeed = Long.parseLong(requireValue(args, ++index, arg));
                case "--advanced-operations" -> options.advancedOperations = positiveInt(requireValue(args, ++index, arg), arg);
                case "--advanced-seed" -> options.advancedSeed = Long.parseLong(requireValue(args, ++index, arg));
                default -> throw new IllegalArgumentException("unknown option: " + arg);
            }
        }

        if (options.readWork < 0) {
            options.readWork = options.work;
        }
        if (options.writeWork < 0) {
            options.writeWork = options.work;
        }
        return options;
    }

    boolean hasExplicitMode() {
        return advancedCorrectness || fullSemantics || pipelineSemantics || advancedPerf
                || pipelineStress != null || endurance != null || contentionStress != null;
    }

    private static String requireValue(String[] args, int index, String option) {
        if (index >= args.length) {
            throw new IllegalArgumentException("missing value for " + option);
        }
        return args[index];
    }

    private static int positiveInt(String value, String option) {
        int parsed = Integer.parseInt(value);
        if (parsed < 1) {
            throw new IllegalArgumentException(option + " must be greater than 0");
        }
        return parsed;
    }

    private static int nonNegativeInt(String value, String option) {
        int parsed = Integer.parseInt(value);
        if (parsed < 0) {
            throw new IllegalArgumentException(option + " must not be negative");
        }
        return parsed;
    }
}
