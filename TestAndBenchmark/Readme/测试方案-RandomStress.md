# Random Stress 随机压力测试

日期：2026-07-21

## 测试方案

重点暴力测试 Pipeline 随机组合，并保留少量 Scope 路径作为基础对照。

Pipeline 内部使用 Scope 完成权限进入、释放、升级、降级和异常释放。因此 Pipeline 经得住长期随机压力时，Scope 与 Core 的组合语义也会被一并覆盖。

长期压力可以一直运行，测试者关闭窗口或按 Ctrl+C 即可停止。

推荐参数只保留少量入口：`--profile` 选择强度，`--seed` 复现，`--workers` 指定工作线程，`--lock-instances` 指定独立锁实例数量。其他细节由 profile 固化。

## 通过标准

- 运行期间没有非预期异常。
- 没有 Concurrent / Exclusive 观察重叠。
- 结束时没有观察状态泄漏。
- Pipeline 段异常后可以重新获得 Exclusive。
- 任一失败都必须输出 seed 和完整复现命令。

## 命令

快速冒烟：

```powershell
TestAndBenchmark.exe stress --profile quick
```

标准压力：

```powershell
TestAndBenchmark.exe stress --profile standard
```

长期运行：

```powershell
TestAndBenchmark.exe stress --profile forever
```

打满机器：

```powershell
TestAndBenchmark.exe stress --profile max
```

指定 seed 复现：

```powershell
TestAndBenchmark.exe stress --profile standard --seed 123456
```

指定机器压力：

```powershell
TestAndBenchmark.exe stress --profile max --workers 128 --lock-instances 1
```
