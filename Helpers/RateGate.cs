using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// 进程级滑动窗口限流闸门：任意 rolling window 内最多放行 <see cref="_maxRequests"/> 次请求，
/// 超出则异步等待窗口内最早一次请求滑出后再放行（不占线程，支持取消）。
/// 由「NVIDIA Nemotron」引擎独享一枚实例（官方免费档 ~40 RPM，按 nvapi key 账户级共享），
/// 在每次真实 HTTP 尝试（含重试）前取名额，主动把请求速率压在限流之下而非被动吃 429；
/// 闸门只作用于持有它的引擎，其它引擎（智谱/通义/自定义）的调用策略完全不受影响。
/// </summary>
public sealed class RateGate
{
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _stamps = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <param name="maxRequests">窗口内允许的最大请求数（如 40）。</param>
    /// <param name="window">滑动窗口长度（如 1 分钟）。</param>
    public RateGate(int maxRequests, TimeSpan window)
    {
        if (maxRequests < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRequests), "窗口内最大请求数必须 >= 1。");
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "滑动窗口长度必须为正。");
        _maxRequests = maxRequests;
        _window = window;
    }

    /// <summary>
    /// 取一个限流名额：窗口未满立即返回并占位；已满则等待最早记录滑出窗口后重试。
    /// 名额一旦发放即占用窗口（与后续请求是否真正成功无关，按最保守口径计数）。
    /// </summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task wait;
            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                while (_stamps.Count > 0 && now - _stamps.Peek() >= _window)
                    _stamps.Dequeue();

                if (_stamps.Count < _maxRequests)
                {
                    _stamps.Enqueue(now);
                    return;
                }

                // 窗口已满：睡到最早一条记录滑出为止（乐观情况下刚好放行）
                wait = Task.Delay(_stamps.Peek() + _window - now, ct);
            }
            finally
            {
                _mutex.Release();
            }

            await wait.ConfigureAwait(false);
        }
    }
}
