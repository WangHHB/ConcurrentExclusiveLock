# Correctness 确定性正确性测试

日期：2026-07-21

## 测试方案

运行快速、确定性行为测试，覆盖基础锁语义、Scope 自动释放、异常释放、升级、降级和 Pipeline 基础段转换。

该方案用于确认功能契约没有回归，不用于性能结论。

## 通过标准

- 所有测试均为 `PASS`。
- 进程返回码为 `0`。
- 任一测试失败、超时或抛出未预期异常，都视为不通过。

## 命令

```powershell
TestAndBenchmark.exe correctness
```

```powershell
TestAndBenchmark.exe test
```

查看入口：

```powershell
TestAndBenchmark.exe help
```
