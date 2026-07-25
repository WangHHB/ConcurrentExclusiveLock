namespace LockBenchmark;

/// <summary>
/// 一次高级锁能力的完整调用协议。
/// </summary>
/// <remarks>
/// 实现负责获取锁、执行状态转换、调用业务 Work 并正确释放最终持有的锁状态。
/// 同一个实现实例会被多个测试线程共享，以触发真实的汇聚或权限转换。
/// </remarks>
internal interface IAdvancedLockFunction
{
    string Name { get; }

    /// <summary>
    /// 执行一次完整的高级锁操作。
    /// </summary>
    /// <param name="work">本 Test Case 独占的新业务工作集。</param>
    /// <returns>本次操作的业务摘要、状态转换结果和完成的 Work 数。</returns>
    AdvancedLockFunctionResult Execute(IWork work);
}

/// <summary>
/// 高级锁操作的单次执行结果。
/// </summary>
internal readonly struct AdvancedLockFunctionResult
{
    /// <summary>业务代码返回值的组合摘要。</summary>
    public long Checksum { get; }

    /// <summary>本次执行完成的 TickRead/TickWrite 数量。</summary>
    public int CompletedWorks { get; }

    /// <summary>
    /// 高级操作是否完成。对于升级操作，它表示当前调用者是否成为唯一升级成功者。
    /// </summary>
    public bool Succeeded { get; }


    public AdvancedLockFunctionResult(
        long checksum,
        int completedWorks,
        bool succeeded)
    {
        Checksum = checksum;
        CompletedWorks = completedWorks;
        Succeeded = succeeded;;
    }
}
