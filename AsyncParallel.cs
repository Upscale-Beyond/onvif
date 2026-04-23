public static class AsyncParallel
{
    public static void InvokeBlocking(CancellationToken cancellationToken, params Func<CancellationToken, Task>[] asyncActions)
    {
        var actions = asyncActions
            .Select(f => (Action)(() => f(cancellationToken).GetAwaiter().GetResult()))
            .ToArray();

        Parallel.Invoke(new ParallelOptions
        {
            CancellationToken = cancellationToken
        }, actions);
    }
}
