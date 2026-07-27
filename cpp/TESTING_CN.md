# 语义测试与压力测试说明

## 目的

TestAndBenchmark 用于验证 C 核心以及 C++ Scope/Pipeline 的权限协议正确性。

语义测试回答：

> 在竞争、抢占、升级、降级、业务 ID 失败、异常、超时和反复复用后，每把锁是否仍然遵守定义好的 Concurrent/Exclusive 协议？

吞吐量不是语义测试的通过条件。

## 构建

```shell
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

单配置构建下的可执行文件：

```text
build/TestAndBenchmark/TestAndBenchmark
```

多配置生成器可能会再增加配置目录。

## 测试模式

### `--full-semantics`

先执行确定性契约，再执行随机合法路径。

覆盖：

- C API 初始化和直接调用；
- 初始快照和业务 ID；
- Concurrent ID 与 maxConcurrent；
- Concurrent/Exclusive 不重叠；
- 抢占式 Exclusive；
- 无条件升级；
- 降级；
- 多升级请求串行；
- ContextID 单赢家升级；
- EpochID 条件升级；
- Scope 正常和异常释放；
- 超时路径；
- Pipeline 权限转换；
- Pipeline Try 失败；
- Pipeline 异常传播和最终释放；
- 多把独立锁上的随机合法路径。

```shell
./build/TestAndBenchmark/TestAndBenchmark \
  --full-semantics \
  --lock-instances 8 \
  --semantic-workers 4 \
  --semantic-operations 256
```

`--advanced-correctness` 当前是该模式的别名。

### `--pipeline-semantics`

只执行固定 Pipeline 契约：

```shell
./build/TestAndBenchmark/TestAndBenchmark --pipeline-semantics
```

### `--pipeline-stress <duration>`

在指定时长内反复执行随机合法 Pipeline 模板。

组合包含：

- 独立 Concurrent；
- 独立 Exclusive；
- None 边界；
- ConvergeConcurrent；
- ConvergeExclusive；
- TryConcurrent；
- TryExclusive；
- EpochID 条件收敛；
- 故意注入的 Segment 异常。

每个受保护业务区都会持续检查 Concurrent 和 Exclusive 业务探针不能重叠；结束时还会检查所有锁都回到 Idle。

```shell
./build/TestAndBenchmark/TestAndBenchmark \
  --pipeline-stress 10m \
  --lock-instances 8 \
  --semantic-workers 8 \
  --semantic-operations 256
```

### `--contention-stress <duration>`

大量专用线程反复竞争同一把锁的普通 Exclusive。

输出总获取次数和每线程最小/最大获取次数。它用于观察实际等待推进情况，不是严格公平性测试。

```shell
./build/TestAndBenchmark/TestAndBenchmark \
  --contention-stress 10m \
  --semantic-workers 64
```

## 参数

| 参数 | 含义 |
|---|---|
| `--lock-instances` | 独立锁数量。 |
| `--semantic-workers` | 每把锁专用线程数；高竞争模式下表示总线程数。 |
| `--semantic-operations` | 每线程随机合法路径轮数；Pipeline 压测中表示每批最大轮数。 |
| `--semantic-seed` | 可选的 64 位复现种子，支持十进制和 `0x`。 |

时长示例：

```text
500ms
30s
10m
24h
1d
```

## 核心检查规则

1. Exclusive 业务区不得与 Concurrent 重叠。
2. 两个 Exclusive 业务区不得重叠。
3. Concurrent ID 必须位于请求范围内。
4. 抢占式 Exclusive 压力出现后，新的 Concurrent 必须停止进入。
5. 多个升级请求的 Exclusive 区域必须串行。
6. 相同 ContextID 的条件升级必须只有一个赢家。
7. 条件升级失败后，原 Concurrent 必须自动释放。
8. Scope 正常结束和异常退出都必须释放最终权限。
9. Pipeline Try 失败必须跳过当前段并从 None 继续。
10. Pipeline 异常必须释放最终权限并继续传播。
11. 每项测试结束后锁都必须可复用并回到 Idle。

## 推荐发布验证顺序

1. 跑完整语义回归。
2. 跑 CTest。
3. 先跑 10 分钟 Pipeline 压测验证命令和随机批次。
4. 普通发布前跑数小时 Pipeline 压测。
5. 重大算法或平台后端修改后延长到 24 小时以上。
6. 每个支持的操作系统分别跑单锁高竞争诊断。
7. 编译器支持时运行 Sanitizer。

## Sanitizer

AddressSanitizer + UndefinedBehaviorSanitizer：

```shell
cmake -S . -B build-asan -G Ninja \
  -DCMAKE_BUILD_TYPE=Debug \
  -DCMAKE_C_FLAGS="-fsanitize=address,undefined -fno-omit-frame-pointer" \
  -DCMAKE_CXX_FLAGS="-fsanitize=address,undefined -fno-omit-frame-pointer" \
  -DCMAKE_EXE_LINKER_FLAGS="-fsanitize=address,undefined"

cmake --build build-asan
./build-asan/TestAndBenchmark/TestAndBenchmark --full-semantics
```

ThreadSanitizer：

```shell
cmake -S . -B build-tsan -G Ninja \
  -DCMAKE_BUILD_TYPE=Debug \
  -DCMAKE_C_FLAGS="-fsanitize=thread -fno-omit-frame-pointer" \
  -DCMAKE_CXX_FLAGS="-fsanitize=thread -fno-omit-frame-pointer" \
  -DCMAKE_EXE_LINKER_FLAGS="-fsanitize=thread"

cmake --build build-tsan
./build-tsan/TestAndBenchmark/TestAndBenchmark --full-semantics
```

Sanitizer 的可用性和兼容性取决于操作系统与编译器。

## 失败和复现

任意断言失败或工作线程未捕获异常都会使当前模式以非零退出码结束，并输出 `ERROR:`。

随机测试失败时，应保持测试模式、拓扑和操作量不变，并使用相同的 `--semantic-seed` 复现。
