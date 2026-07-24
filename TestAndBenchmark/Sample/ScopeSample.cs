using IntomicLib;

namespace TestAndBenchmark.Sample;

internal static class ScopeSample
{
    public static void ConcurrentOnly()
    {
        var room = new RoomState();

        using (var scope = new ConcurrentExclusiveLockScope(room.Lock))
        {
            scope.AcquireConcurrent();

            // 只需要允许并发访问时，获取 Concurrent，完成后交给 Dispose 自动释放。
            // Acquire Concurrent when the operation may run with other Concurrent operations; Dispose can release it automatically.
            int visibleVersion = room.VisibleVersion;
            int onlinePlayers = room.OnlinePlayers;

            room.LastObservedVersion = visibleVersion;
            room.LastObservedPlayers = onlinePlayers;

            // scope.ReleaseConcurrent();
            // 最后可以手动释放，也可以让 scope 在 Dispose 时自动释放。
            // You may release manually here, or let scope.Dispose() release the final held access.
        }
    }

    public static void ExclusiveOnly()
    {
        var room = new RoomState();

        using (var scope = new ConcurrentExclusiveLockScope(room.Lock))
        {
            scope.AcquireExclusive();

            // 只需要独占访问时，直接获取 Exclusive。
            // Exclusive 请求进入竞争窗口后，会阻止新的 Concurrent 继续进入。
            // Acquire Exclusive when the operation must run alone.
            // Once an Exclusive request enters the contention window, new Concurrent entries are blocked.
            room.Score += 10;
            room.Version++;
            room.VisibleVersion = room.Version;

            // scope.ReleaseExclusive();
            // 最后可以手动释放，也可以让 scope 在 Dispose 时自动释放。
            // You may release manually here, or let scope.Dispose() release the final held access.
        }
    }

    public static bool TryConcurrentAsOuterEntry()
    {
        var room = new RoomState();

        using (var scope = new ConcurrentExclusiveLockScope(room.Lock))
        {
            // Try 获取适合作为最外层入口：成功才进入权限区域，失败就走降级路径。
            // Try acquisition is best used as the outer entry point: enter the access region only on success.
            int concurrentID = scope.TryAcquireConcurrent();
            if (concurrentID == 0)
            {
                return false;
            }

            room.LastObservedVersion = room.VisibleVersion;

            // scope.ReleaseConcurrent();
            // 最后可以手动释放，也可以让 scope 在 Dispose 时自动释放。
            // You may release manually here, or let scope.Dispose() release the final held access.
            return true;
        }
    }

    public static void ExclusiveDowngradeToConcurrent()
    {
        var room = new RoomState();

        using (var scope = new ConcurrentExclusiveLockScope(room.Lock))
        {
            scope.AcquireExclusive();

            // 先独占提交必须隔离的状态变化。
            // First commit the state change that must be isolated.
            room.Score += 50;
            room.Version++;

            // 提交完成后，如果后续只需要允许并发访问，可原地降级到 Concurrent。
            // After the isolated commit, downgrade in place when the remaining work only needs Concurrent access.
            scope.ExclusiveToConcurrent();

            // 现在已持有 Concurrent，可以继续生成可见快照或刷新缓存。
            // The scope now holds Concurrent access and can build visible snapshots or refresh caches.
            room.VisibleVersion = room.Version;
            room.LastObservedPlayers = room.OnlinePlayers;

            // scope.ReleaseConcurrent();
            // 当前最终持有的是 Concurrent，可以手动释放，也可以让 scope 在 Dispose 时自动释放。
            // The final held access is Concurrent; release it manually or let scope.Dispose() release it.
        }
    }

    public static bool ConcurrentUpgradeToExclusiveWithEpoch()
    {
        var room = new RoomState();

        using (var scope = new ConcurrentExclusiveLockScope(room.Lock))
        {
            scope.AcquireConcurrent();

            // 先在 Concurrent 区域做快照检查，判断是否真的需要进入独占提交。
            // First inspect a snapshot in the Concurrent region and decide whether an isolated commit is needed.
            bool playerExists = room.OnlinePlayers > 0;
            bool scoreNeedsCommit = room.PendingScoreDelta != 0;
            int nextEpochID = room.Version + 1;

            if (!playerExists || !scoreNeedsCommit)
            {
                return false;
            }

            // 通过 EpochID 原地升级到 Exclusive。
            // 成功后当前 scope 持有 Exclusive；失败时原 Concurrent 已自动释放。
            // Upgrade in place to Exclusive through EpochID.
            // On success the scope holds Exclusive; on failure the original Concurrent access has been released.
            if (!scope.TryConcurrentToExclusiveWithRaiseEpochID(nextEpochID))
            {
                return false;
            }

            room.Score += room.PendingScoreDelta;
            room.PendingScoreDelta = 0;
            room.Version = nextEpochID;

            // scope.ReleaseExclusive();
            // 当前最终持有的是 Exclusive，可以手动释放，也可以让 scope 在 Dispose 时自动释放。
            // The final held access is Exclusive; release it manually or let scope.Dispose() release it.
            return true;
        }
    }

    public static bool ConcurrentUpgradeToExclusiveWithContext()
    {
        var room = new RoomState();
        int matchContextID = 20260722;

        using (var scope = new ConcurrentExclusiveLockScope(room.Lock))
        {
            scope.AcquireConcurrent();

            // ContextID 可表达业务上下文身份，例如同一局比赛、同一个房间任务或同一批处理。
            // ContextID can represent a business context, such as one match, one room task, or one batch.
            bool sameMatchStillActive = room.CurrentMatchID == matchContextID;
            if (!sameMatchStillActive)
            {
                return false;
            }

            // 通过 ContextID 原地升级到 Exclusive。
            // 适合“只有第一个成功切换上下文的人负责独占提交”的业务协议。
            // Upgrade in place to Exclusive through ContextID.
            // This fits protocols where only the first caller that switches context performs the isolated commit.
            if (!scope.TryConcurrentToExclusiveWithSwitchContextID(matchContextID))
            {
                return false;
            }

            room.MatchSettlementCount++;
            room.Version++;

            // scope.ReleaseExclusive();
            // 当前最终持有的是 Exclusive，可以手动释放，也可以让 scope 在 Dispose 时自动释放。
            // The final held access is Exclusive; release it manually or let scope.Dispose() release it.
            return true;
        }
    }

    private sealed class RoomState
    {
        public readonly ConcurrentExclusiveLock Lock = ConcurrentExclusiveLock.Create();

        public int CurrentMatchID = 20260722;
        public int OnlinePlayers = 8;
        public int PendingScoreDelta = 20;
        public int Score;
        public int Version;
        public int VisibleVersion;
        public int LastObservedVersion;
        public int LastObservedPlayers;
        public int MatchSettlementCount;
    }
}
