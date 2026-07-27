# Visual Studio 2026 项目

无需安装 CMake。

1. 双击项目根目录的 `ConcurrentExclusiveLock.sln`。
2. 顶部配置选择 `Release` 和 `x64`。
3. 右键 `TestAndBenchmark`，选择“设为启动项目”。
4. 选择“生成解决方案”。
5. 按 `Ctrl+F5` 运行。

Release 配置已经预设以下正式跑分参数：

```text
--lock-instances 8 --threads 8 --workload memory --operations 100000 --memory-mb 64 --read-work 640 --write-work 640
```

生成文件位于：

```text
bin\x64\Release\TestAndBenchmark.exe
```

也可以在 PowerShell 中执行：

```powershell
.\build-vs.ps1
.\run-benchmark-vs.ps1
```

项目使用 Visual Studio 2026 的 `v145` 平台工具集。Visual Studio Installer 中必须安装“使用 C++ 的桌面开发”。
