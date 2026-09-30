// 测试会修改进程级的输入和工具注册表，不能与其他运行用例并发。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
