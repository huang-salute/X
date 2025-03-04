﻿using NewLife.Data;
using NewLife.Log;
using NewLife.Threading;

namespace NewLife.Net.Handlers;

/// <summary>消息匹配队列接口。用于把响应数据包配对到请求包</summary>
public interface IMatchQueue
{
    /// <summary>加入请求队列</summary>
    /// <param name="owner">拥有者</param>
    /// <param name="request">请求消息</param>
    /// <param name="msTimeout">超时取消时间</param>
    /// <param name="source">任务源</param>
    Task<Object> Add(Object owner, Object request, Int32 msTimeout, TaskCompletionSource<Object> source);

    /// <summary>检查请求队列是否有匹配该响应的请求</summary>
    /// <param name="owner">拥有者</param>
    /// <param name="response">响应消息</param>
    /// <param name="result">任务结果</param>
    /// <param name="callback">用于检查匹配的回调</param>
    /// <returns></returns>
    Boolean Match(Object owner, Object response, Object result, Func<Object, Object, Boolean> callback);

    /// <summary>清空队列</summary>
    void Clear();
}

/// <summary>消息匹配队列。子类可重载以自定义请求响应匹配逻辑</summary>
public class DefaultMatchQueue : IMatchQueue
{
    private readonly ItemWrap[] Items;
    private Int32 _Count;
    private TimerX _Timer;

    /// <summary>按指定大小来初始化队列</summary>
    /// <param name="size"></param>
    public DefaultMatchQueue(Int32 size = 256) => Items = new ItemWrap[size];

    /// <summary>加入请求队列</summary>
    /// <param name="owner">拥有者</param>
    /// <param name="request">请求的数据</param>
    /// <param name="msTimeout">超时取消时间</param>
    /// <param name="source">任务源</param>
    public virtual Task<Object> Add(Object owner, Object request, Int32 msTimeout, TaskCompletionSource<Object> source)
    {
        var now = Runtime.TickCount64;

        // 控制超时时间，默认15秒
        if (msTimeout <= 10) msTimeout = 15_000;

        var ext = owner as IExtend;
        var qi = new Item
        {
            Owner = owner,
            Request = request,
            EndTime = now + msTimeout,
            Source = source,
            Span = ext?["Span"] as ISpan,
        };

        // 加入队列
        var items = Items;
        var i = 0;
        for (i = 0; i < items.Length; ++i)
        {
            if (Interlocked.CompareExchange(ref items[i].Value, qi, null) == null) break;
        }
        if (i >= items.Length)
        {
            DefaultTracer.Instance?.NewError("net:MatchQueue:IsFull", new { items.Length });
            throw new XException("匹配队列已满[{0}]", items.Length);
        }

        Interlocked.Increment(ref _Count);

        if (_Timer == null)
        {
            lock (this)
            {
                _Timer ??= new TimerX(Check, null, 1000, 1000, "Match")
                {
                    Async = true,
                    CanExecute = () => _Count > 0
                };
            }
        }

        return source?.Task;
    }

    /// <summary>检查请求队列是否有匹配该响应的请求</summary>
    /// <param name="owner">拥有者</param>
    /// <param name="response">响应消息</param>
    /// <param name="result">任务结果</param>
    /// <param name="callback">用于检查匹配的回调</param>
    /// <returns></returns>
    public virtual Boolean Match(Object owner, Object response, Object result, Func<Object, Object, Boolean> callback)
    {
        if (_Count <= 0) return false;

        // 直接遍历，队列不会很长
        var qs = Items;
        for (var i = 0; i < qs.Length; i++)
        {
            var qi = qs[i].Value;
            if (qi == null) continue;

            if (qi.Owner == owner && callback(qi.Request, response))
            {
                qs[i].Value = null;
                Interlocked.Decrement(ref _Count);

                // 异步设置完成结果，否则可能会在当前线程恢复上层await，导致堵塞当前任务
                var src = qi.Source;
                if (src != null && !src.Task.IsCompleted)
                {
                    qi.Span?.AppendTag($"{Runtime.TickCount64} MatchQueue.SetResult(Matched)");
                    Task.Factory.StartNew(() => src.TrySetResult(result));
                }

                return true;
            }
        }

        if (SocketSetting.Current.Debug)
            XTrace.WriteLine("MatchQueue.Check 失败 [{0}] result={1} Items={2}", response, result, _Count);

        return false;
    }

    /// <summary>定时检查发送队列，超时未收到响应则重发</summary>
    /// <param name="state"></param>
    void Check(Object state)
    {
        if (_Count <= 0) return;

        // 直接遍历，队列不会很长
        var now = Runtime.TickCount64;
        var qs = Items;
        for (var i = 0; i < qs.Length; i++)
        {
            var qi = qs[i].Value;
            if (qi == null) continue;

            // 过期取消
            if (qi.EndTime <= now)
            {
                qs[i].Value = null;
                Interlocked.Decrement(ref _Count);

                // 异步取消任务，避免在当前线程执行上层await的延续任务
                var src = qi.Source;
                if (src != null && !src.Task.IsCompleted)
                {
                    qi.Span?.AppendTag($"{Runtime.TickCount64} MatchQueue.Expired({qi.EndTime}<={now})");

                    Task.Factory.StartNew(() => src.TrySetCanceled());
                }
            }
        }
    }

    /// <summary>清空队列</summary>
    public virtual void Clear()
    {
        var qs = Items;
        for (var i = 0; i < qs.Length; ++i)
        {
            var qi = qs[i].Value;
            if (qi == null) continue;

            qs[i].Value = null;
            Interlocked.Decrement(ref _Count);

            // 异步取消任务，避免在当前线程执行上层await的延续任务
            var src = qi.Source;
            if (src != null && !src.Task.IsCompleted)
            {
                qi.Span?.AppendTag("MatchQueue.Clear()");

                Task.Factory.StartNew(() => src.TrySetCanceled());
            }
        }
        _Count = 0;
    }

    class Item
    {
        public Object Owner { get; set; }
        public Object Request { get; set; }
        public Int64 EndTime { get; set; }
        public TaskCompletionSource<Object> Source { get; set; }
        public ISpan Span { get; set; }
    }
    struct ItemWrap
    {
        public Item Value;
    }
}