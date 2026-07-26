# Verification Performed in the Build Sandbox

The source tree was compiled with:

```text
OpenJDK 21.0.10
javac --release 17 -Xlint:all
```

The Java core uses an explicitly non-fair scheduler:

```java
private final ReentrantLock monitor = new ReentrantLock(false);
```

The following modes were executed successfully:

- `--help`;
- `--advanced-correctness --lock-instances 8 --advanced-operations 8 --advanced-seed 12345`;
- `--full-semantics --lock-instances 8 --semantic-workers 4 --semantic-operations 64 --semantic-seed 12345`;
- `--pipeline-semantics --lock-instances 1 --semantic-workers 16 --semantic-operations 100 --semantic-seed 12345`;
- `--pipeline-stress 2s --lock-instances 4 --semantic-workers 16 --semantic-operations 100 --semantic-seed 12345`;
- `--endurance 2s`;
- `--contention-stress 2s --threads 32`;
- `--advanced-perf --threads 8 --operations 5000 --work 16`.

A full built-in-lock comparison was also executed with:

```powershell
java -cp out io.github.wanghhb.concurrentexclusivelock.testandbenchmark.TestAndBenchmark `
  --lock-instances 1 `
  --threads 64 `
  --workload memory `
  --operations 10000 `
  --memory-mb 64 `
  --read-work 64 `
  --write-work 64
```

The comparison covered:

- `synchronized`;
- non-fair `ReentrantLock`;
- non-fair `ReentrantReadWriteLock`;
- `StampedLock`;
- `CEL`;
- `CEL(ExclusiveOnly)`.

All six read/write ratios completed and every strategy produced the same final Work state. The full output is stored in `TestResults/benchmark-memory-64threads.txt`.

Maven was not installed in the sandbox, so `mvn clean package` was not executed there. The POM files were XML-parsed, all sources were compiled directly with `javac`, and a combined runnable JAR was assembled and executed with JDK tools.

These checks establish buildability, command-line operation, baseline semantics, short stress stability, and functioning built-in-lock comparisons. They do not replace multi-hour Pipeline stress, JCStress coverage, or formal JMH benchmarking on the target Windows/JDK environment.
