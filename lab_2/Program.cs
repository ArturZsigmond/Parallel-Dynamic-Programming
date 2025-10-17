using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

sealed class BoundedQueue<T>
{
    private readonly Queue<T> _q;
    private readonly int _capacity;
    private readonly object _lock = new();

    public BoundedQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _q = new Queue<T>(capacity);
    }

    // Producer-side: blocks while full
    public void Enqueue(T item)
    {
        lock (_lock)
        {
            while (_q.Count == _capacity)
                Monitor.Wait(_lock);               // wait for "not full"

            _q.Enqueue(item);
            Monitor.PulseAll(_lock);               // signal "not empty"
        }
    }

    // Consumer-side: blocks while empty; returns false if completed and empty
    // The 'done' flag is checked by caller via TryDequeue pattern below.
    public bool TryDequeue(out T? item, bool producerDone)
    {
        lock (_lock)
        {
            while (_q.Count == 0 && !producerDone)
                Monitor.Wait(_lock);               // wait for "not empty" (unless done)

            if (_q.Count == 0)
            {
                item = default!;
                return false;                     // empty and producerDone == true
            }

            item = _q.Dequeue();
            Monitor.PulseAll(_lock);               // signal "not full"
            return true;
        }
    }

    public int Count
    {
        get { lock (_lock) return _q.Count; }
    }
}

static class Program
{
    // ---- Tunables ----
    static int VectorLength  = 5_000_000;  // number of pairs (increase to stress test)
    static int QueueCapacity = 1_024;      // bounded buffer size; vary to measure performance
    static int MaxValue      = 10;         // vector entries in [0..MaxValue)
    // -------------------

    static long[] A = Array.Empty<long>();
    static long[] B = Array.Empty<long>();

    static volatile bool ProducerDone = false;
    static long ConsumerSum = 0;

    static void Main()
    {
        Console.WriteLine("Producer-Consumer Dot Product (mutex + condition variables via Monitor)");
        Console.WriteLine($"N={VectorLength}, QueueCapacity={QueueCapacity}\n");

        // Prepare vectors
        A = new long[VectorLength];
        B = new long[VectorLength];
        var rnd = new Random(12345);
        for (int i = 0; i < VectorLength; i++)
        {
            A[i] = rnd.Next(MaxValue);
            B[i] = rnd.Next(MaxValue);
        }

        // Expected result (single-thread) to verify correctness
        long expected = 0;
        for (int i = 0; i < VectorLength; i++)
            expected += A[i] * B[i];

        var buffer = new BoundedQueue<long>(QueueCapacity);

        var sw = Stopwatch.StartNew();

        // Producer: computes pairwise products and enqueues
        var producer = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < VectorLength; i++)
                {
                    long prod = A[i] * B[i];
                    buffer.Enqueue(prod);
                }
            }
            finally
            {
                ProducerDone = true;
                // Wake any waiting consumer
                // We can't pulse buffer's private lock from here, so we "tickle" it by Enqueue/Dequeue?
                // Better: rely on consumer TryDequeue(done) which checks ProducerDone before waiting.
                // To ensure no spurious wait, do a tiny sleep to yield:
                Thread.Yield();
            }
        })
        { IsBackground = true };

        // Consumer: dequeues and accumulates
        var consumer = new Thread(() =>
        {
            long acc = 0;
            while (true)
            {
                if (!buffer.TryDequeue(out long prod, ProducerDone))
                    break; // buffer empty and producer is done

                acc += prod;
            }
            Interlocked.Exchange(ref ConsumerSum, acc);
        })
        { IsBackground = true };

        producer.Start();
        consumer.Start();

        producer.Join();
        consumer.Join();
        sw.Stop();

        Console.WriteLine($"ConsumerSum = {ConsumerSum}");
        Console.WriteLine($"Expected    = {expected}");
        Console.WriteLine($"Result      = {(ConsumerSum == expected ? "OK" : "BROKEN")}");
        Console.WriteLine($"Elapsed     = {sw.ElapsedMilliseconds} ms");

        // Simple throughput metric: elements/sec
        var elemsPerSec = (long)((double)VectorLength / Math.Max(1, sw.Elapsed.TotalSeconds));
        Console.WriteLine($"Throughput  = {elemsPerSec:N0} elems/s");

        Console.WriteLine("\nTry varying QueueCapacity (e.g., 1, 2, 8, 64, 1024, 16384) and N to observe performance.");
    }
}
