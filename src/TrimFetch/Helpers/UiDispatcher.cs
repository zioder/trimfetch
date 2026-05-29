namespace TrimFetch.Helpers;

internal static class UiDispatcher
{
    public static bool HasThreadAccess =>
        App.DispatcherQueue?.HasThreadAccess ?? true;

    public static void Run(Action action)
    {
        var queue = App.DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(() => action());
    }

    public static Task RunAsync(Action action)
    {
        var queue = App.DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    public static Task InvokeAsync(Func<Task> action) => InvokeAsync(async () =>
    {
        await action();
        return true;
    });

    public static async Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        var queue = App.DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            return await action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TryEnqueue(() =>
        {
            _ = RunInvokedAsync(action, completion);
        });

        return await completion.Task;
    }

    private static async Task RunInvokedAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try
        {
            await action();
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private static async Task RunInvokedAsync<T>(Func<Task<T>> action, TaskCompletionSource<T> completion)
    {
        try
        {
            completion.TrySetResult(await action());
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }
}
