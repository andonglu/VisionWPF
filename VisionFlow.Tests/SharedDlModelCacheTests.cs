using VisionFlow.Tools;

namespace VisionFlow.Tests;

/// <summary>
/// D1 共享模型句柄池专项（§5.4，非 HALCON 用例）：引用计数平衡（按"实例持有"）、
/// 键切换先还后取、参数分池、Invalidate 标脏与归零回收、编辑副本不泄漏、
/// Flow 锚点缺失回退 Instance 的判定。池内条目用假句柄构造，不调用 HALCON。
/// </summary>
public class SharedDlModelCacheTests
{
    /// <summary>模拟工具实例的持有语义：同键复用不重复 Acquire，键变先还后取，ReleaseResources 只还一份。</summary>
    private sealed class FakeToolHolder
    {
        private DlModelCacheEntry _held;

        public DlModelCacheEntry Ensure(DlModelPoolKey key, Func<DlModelCacheEntry> factory)
        {
            if (_held != null && _held.Key.Equals(key) && SharedDlModelCache.IsUsable(_held))
            {
                return _held;
            }
            SharedDlModelCache.Release(_held);
            _held = SharedDlModelCache.Acquire(key, factory);
            return _held;
        }

        public void ReleaseResources()
        {
            SharedDlModelCache.Release(_held);
            _held = null;
        }
    }

    private static DlModelPoolKey Key(object scope, string path = @"C:\models\fake.hdl", long ticks = 100,
        int batchSize = 1, bool optimize = true, DeepLearningModelKind kind = DeepLearningModelKind.Auto)
    {
        return DlModelCachePolicy.BuildKey(scope, path, ticks, kind,
            DeepLearningDevicePreference.Auto, batchSize, optimize);
    }

    private static Func<DlModelCacheEntry> Factory(DlModelPoolKey key, Action onClear = null)
    {
        return () => new DlModelCacheEntry
        {
            ResolvedPath = key.ResolvedPath,
            ModelType = "detection",
            ClearAction = onClear
        };
    }

    [Fact]
    public void 同键Acquire复用同一条目_引用累加_归零清除()
    {
        var scope = new object();
        DlModelPoolKey key = Key(scope);
        int loads0 = SharedDlModelCache.DebugTotalLoadCount;
        int alive0 = SharedDlModelCache.DebugAliveEntryCount;
        int cleared = 0;

        DlModelCacheEntry first = SharedDlModelCache.Acquire(key, Factory(key, () => cleared++));
        DlModelCacheEntry second = SharedDlModelCache.Acquire(key, Factory(key, () => cleared++));

        Assert.Same(first, second);
        Assert.Equal(1, SharedDlModelCache.DebugTotalLoadCount - loads0);
        Assert.Equal(2, GetRefCount(key));
        Assert.Equal(1, SharedDlModelCache.DebugAliveEntryCount - alive0);

        SharedDlModelCache.Release(first);
        Assert.True(SharedDlModelCache.TryGetRefCount(key, out int remaining));
        Assert.Equal(1, remaining);

        SharedDlModelCache.Release(second);
        Assert.False(SharedDlModelCache.TryGetRefCount(key, out _));
        Assert.Equal(0, SharedDlModelCache.DebugAliveEntryCount - alive0);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void 实例持有语义_同键不重复加引用_键切换先还后取()
    {
        var scope = new object();
        var holder = new FakeToolHolder();
        int loads0 = SharedDlModelCache.DebugTotalLoadCount;
        DlModelPoolKey keyA = Key(scope, ticks: 100);
        DlModelPoolKey keyB = Key(scope, ticks: 200);

        DlModelCacheEntry entryA = holder.Ensure(keyA, Factory(keyA));
        holder.Ensure(keyA, Factory(keyA));
        holder.Ensure(keyA, Factory(keyA));
        Assert.Equal(1, SharedDlModelCache.DebugTotalLoadCount - loads0);
        Assert.Equal(1, GetRefCount(keyA));

        DlModelCacheEntry entryB = holder.Ensure(keyB, Factory(keyB));
        Assert.NotSame(entryA, entryB);
        Assert.False(SharedDlModelCache.TryGetRefCount(keyA, out _));
        Assert.Equal(1, GetRefCount(keyB));

        holder.ReleaseResources();
        Assert.False(SharedDlModelCache.TryGetRefCount(keyB, out _));
    }

    [Fact]
    public void 作用域或参数不同_自然分成多个池条目()
    {
        var scopeA = new object();
        var scopeB = new object();
        DlModelPoolKey byScope1 = Key(scopeA);
        DlModelPoolKey byScope2 = Key(scopeB);
        DlModelPoolKey byBatch = Key(scopeA, batchSize: 4);
        DlModelPoolKey byKind = Key(scopeA, kind: DeepLearningModelKind.Detection);

        int loads0 = SharedDlModelCache.DebugTotalLoadCount;
        DlModelCacheEntry e1 = SharedDlModelCache.Acquire(byScope1, Factory(byScope1));
        DlModelCacheEntry e2 = SharedDlModelCache.Acquire(byScope2, Factory(byScope2));
        DlModelCacheEntry e3 = SharedDlModelCache.Acquire(byBatch, Factory(byBatch));
        DlModelCacheEntry e4 = SharedDlModelCache.Acquire(byKind, Factory(byKind));

        Assert.NotSame(e1, e2);
        Assert.NotSame(e1, e3);
        Assert.NotSame(e1, e4);
        Assert.Equal(4, SharedDlModelCache.DebugTotalLoadCount - loads0);

        SharedDlModelCache.Release(e1);
        SharedDlModelCache.Release(e2);
        SharedDlModelCache.Release(e3);
        SharedDlModelCache.Release(e4);
    }

    [Fact]
    public void Invalidate只标脏_新Acquire不复用_旧条目归零后清除()
    {
        var scope = new object();
        DlModelPoolKey key = Key(scope);
        int loads0 = SharedDlModelCache.DebugTotalLoadCount;
        int alive0 = SharedDlModelCache.DebugAliveEntryCount;

        DlModelCacheEntry oldEntry = SharedDlModelCache.Acquire(key, Factory(key));
        SharedDlModelCache.Invalidate(key.ResolvedPath);
        Assert.False(SharedDlModelCache.IsUsable(oldEntry));

        DlModelCacheEntry newEntry = SharedDlModelCache.Acquire(key, Factory(key));
        Assert.NotSame(oldEntry, newEntry);
        Assert.Equal(2, SharedDlModelCache.DebugTotalLoadCount - loads0);
        Assert.Equal(2, SharedDlModelCache.DebugAliveEntryCount - alive0);
        Assert.Equal(1, GetRefCount(key));

        // 旧条目（已不在登记字典中）仍由原持有者按引用归还，归零即清除。
        SharedDlModelCache.Release(oldEntry);
        Assert.Equal(1, SharedDlModelCache.DebugAliveEntryCount - alive0);
        Assert.Equal(1, GetRefCount(key));

        SharedDlModelCache.Release(newEntry);
        Assert.Equal(0, SharedDlModelCache.DebugAliveEntryCount - alive0);
    }

    [Fact]
    public void 回收规则唯一_引用归零即清除_与Stale无关()
    {
        var scope = new object();
        DlModelPoolKey key = Key(scope);
        int cleared = 0;

        DlModelCacheEntry entry = SharedDlModelCache.Acquire(key, Factory(key, () => cleared++));
        SharedDlModelCache.Invalidate(key.ResolvedPath);
        SharedDlModelCache.Release(entry);

        Assert.False(SharedDlModelCache.TryGetRefCount(key, out _));
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void 编辑副本场景_两工具各持一份_一份释放另一份仍可用()
    {
        var scope = new object();
        DlModelPoolKey key = Key(scope);
        var editorCopy = new FakeToolHolder();
        var mainTool = new FakeToolHolder();

        DlModelCacheEntry copyEntry = editorCopy.Ensure(key, Factory(key));
        DlModelCacheEntry mainEntry = mainTool.Ensure(key, Factory(key));
        Assert.Same(copyEntry, mainEntry);
        Assert.Equal(2, GetRefCount(key));

        // 编辑副本取消 / 提交：只归还它自己持有的那一份，正主不受影响。
        editorCopy.ReleaseResources();
        Assert.True(SharedDlModelCache.IsUsable(mainEntry));
        Assert.Equal(1, GetRefCount(key));

        mainTool.ReleaseResources();
        Assert.False(SharedDlModelCache.TryGetRefCount(key, out _));
    }

    [Fact]
    public void 按键与按句柄归还_效果一致()
    {
        var scope = new object();
        DlModelPoolKey key = Key(scope);
        DlModelCacheEntry byKey = SharedDlModelCache.Acquire(key, Factory(key));
        SharedDlModelCache.Release(key);
        Assert.False(SharedDlModelCache.TryGetRefCount(key, out _));

        DlModelCacheEntry byHandle = SharedDlModelCache.Acquire(key, Factory(key));
        SharedDlModelCache.ReleaseHandle(byHandle.Handle);
        Assert.False(SharedDlModelCache.TryGetRefCount(key, out _));
    }

    [Fact]
    public void Flow模式无锚点_回退Instance_Project与有锚点Flow成立()
    {
        var anchor = new object();

        Assert.False(DlModelCachePolicy.TryResolveSharedScope(ModelCacheMode.Instance, anchor, out _));
        Assert.False(DlModelCachePolicy.TryResolveSharedScope(ModelCacheMode.Flow, null, out _));

        Assert.True(DlModelCachePolicy.TryResolveSharedScope(ModelCacheMode.Flow, anchor, out object flowScope));
        Assert.Same(anchor, flowScope);

        Assert.True(DlModelCachePolicy.TryResolveSharedScope(ModelCacheMode.Project, null, out object projectScope));
        Assert.Same(DlModelCachePolicy.ProjectScopeToken, projectScope);
    }

    private static int GetRefCount(DlModelPoolKey key)
    {
        Assert.True(SharedDlModelCache.TryGetRefCount(key, out int refCount));
        return refCount;
    }
}
